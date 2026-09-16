"""Read-only ELF64 inspection and narrowly checked DT_NEEDED derivative comparison."""
from __future__ import annotations
import hashlib
import struct
from pathlib import Path


def sha(data):
    return hashlib.sha256(data).hexdigest()


def parse(path):
    data = Path(path).read_bytes()
    assert data[:7] == b"\x7fELF\x02\x01\x01", "Only ELF64 little endian version 1"
    e = struct.unpack_from("<16sHHIQQQIHHHHHH", data)
    assert e[2] == 62 and e[9] == 56 and e[11] == 64
    assert 0 < e[10] < 256 and 0 < e[13] < e[12] < 4096
    ph = [struct.unpack_from("<IIQQQQQQ", data, e[5] + i * 56) for i in range(e[10])]
    sh = [struct.unpack_from("<IIQQQQIIQQ", data, e[6] + i * 64) for i in range(e[12])]
    for p in ph:
        assert p[2] + p[5] <= len(data) and p[5] <= p[6]
    names_section = sh[e[13]]
    names = data[names_section[4]:names_section[4] + names_section[5]]

    def string(blob, index):
        assert 0 <= index < len(blob)
        end = blob.find(b"\0", index)
        assert end >= index
        return blob[index:end].decode("utf-8", "strict")

    sections = []
    for s in sh:
        name = string(names, s[0])
        payload = b"" if s[1] == 8 else data[s[4]:s[4] + s[5]]
        assert s[1] == 8 or len(payload) == s[5]
        sections.append({"name": name, "type": s[1], "flags": s[2], "vaddr": s[3],
            "offset": s[4], "bytes": s[5], "link": s[6], "info": s[7], "align": s[8],
            "entryBytes": s[9], "payload": payload, "sha256": sha(payload)})
    by = {s["name"]: s for s in sections}
    assert len(by) == len(sections), "Duplicate section names are unsupported"
    dynamic = []
    needed = []
    if ".dynamic" in by:
        strings = by[".dynstr"]["payload"]
        for tag, value in struct.iter_unpack("<qQ", by[".dynamic"]["payload"]):
            if tag == 0:
                break
            dynamic.append([tag, value])
            if tag == 1:
                needed.append(string(strings, value))
    symbols = {}
    for s in sections:
        if s["type"] not in (2, 11):
            continue
        assert s["entryBytes"] == 24
        strings = sections[s["link"]]["payload"]
        rows = []
        for name, info, other, index, value, size in struct.iter_unpack("<IBBHQQ", s["payload"]):
            section = sections[index]["name"] if 0 < index < 0xff00 else index
            if info & 15 == 3 and isinstance(section, str):
                value = ["section-relative", value - by[section]["vaddr"]]
            rows.append([string(strings, name), info, other, section, value, size])
        symbols[s["name"]] = rows
    return {"path": str(path), "bytes": len(data), "sha256": sha(data), "elf": e,
        "programHeaders": ph, "sections": sections, "byName": by, "dynamic": dynamic,
        "needed": needed, "symbols": symbols, "data": data}


def summary(elf):
    return {"path": elf["path"], "bytes": elf["bytes"], "sha256": elf["sha256"],
        "type": elf["elf"][1], "machine": elf["elf"][2], "entry": elf["elf"][4],
        "needed": elf["needed"], "programHeaders": elf["programHeaders"],
        "sections": [{k: v for k, v in s.items() if k != "payload"} for s in elf["sections"]],
        "symbolCounts": {k: len(v) for k, v in elf["symbols"].items()}}


def mapped_layout(elf):
    """Tie section bytes to loader mappings, including actual RELRO coverage."""
    loads = [p for p in elf["programHeaders"] if p[0] == 1 and p[6]]
    assert loads and loads == sorted(loads, key=lambda p: p[3])
    for index, p in enumerate(loads):
        assert p[7] in (0, 1) or (p[7] & (p[7] - 1) == 0 and (p[3] - p[2]) % p[7] == 0)
        assert p[3] + p[6] <= 2**64 and p[1] & 3 != 3
        if index:
            assert loads[index - 1][3] + loads[index - 1][6] <= p[3], "Overlapping PT_LOAD virtual ranges"
    mapped = {}
    for s in elf["sections"]:
        if not s["flags"] & 2 or not s["bytes"]:
            continue
        owners = [p for p in loads if p[3] <= s["vaddr"] and s["vaddr"] + s["bytes"] <= p[3] + p[6]]
        assert len(owners) == 1, (s["name"], "Unique PT_LOAD required")
        p = owners[0]
        assert p[1] & 4 and (not s["flags"] & 1 or p[1] & 2) and (not s["flags"] & 4 or p[1] & 1)
        if s["type"] != 8:
            assert s["offset"] == p[2] + s["vaddr"] - p[3], (s["name"], "Section/segment file mapping differs")
            assert s["offset"] + s["bytes"] <= p[2] + p[5]
        mapped[s["name"]] = p[1]
    dynamic = [p for p in elf["programHeaders"] if p[0] == 2]
    if ".dynamic" in elf["byName"]:
        s = elf["byName"][".dynamic"]
        assert len(dynamic) == 1
        p = dynamic[0]
        assert (p[2], p[3], p[5], p[6]) == (s["offset"], s["vaddr"], s["bytes"], s["bytes"])
    else:
        assert not dynamic
    relros = [p for p in elf["programHeaders"] if p[0] == 0x6474e552]
    covered = []
    for p in relros:
        assert len([q for q in loads if q[3] <= p[3] and p[3] + p[6] <= q[3] + q[6]]) == 1
        # glibc rounds the beginning and end down to the system page boundary.
        start, end = p[3] & ~4095, (p[3] + p[6]) & ~4095
        covered.extend(s["name"] for s in elf["sections"] if s["flags"] & 2 and s["bytes"] and
                       start <= s["vaddr"] and s["vaddr"] + s["bytes"] <= end)
    return {"sectionSegmentFlags": mapped, "relroCoveredSections": sorted(set(covered)),
            "sectionBytesMatchLoadMappings": True, "loadRangesAlignedAndDisjoint": True}


def compare_needed_derivative(original, derived, needed):
    a, b = parse(original), parse(derived)
    layout_a, layout_b = mapped_layout(a), mapped_layout(b)
    assert layout_a == layout_b, "Section permissions or effective RELRO coverage changed"
    assert a["sha256"] != b["sha256"], "Expected a separately identified derivative"
    assert a["elf"][:5] == b["elf"][:5] and a["elf"][7:9] == b["elf"][7:9]
    assert set(a["byName"]) == set(b["byName"])
    assert b["needed"] == [needed] + a["needed"], "Exactly one required dependency, fixed order"
    assert a["symbols"] == b["symbols"], "Symbol identities/values must be preserved"
    metadata = {".dynamic", ".dynstr", ".dynsym", ".symtab"}
    checked = []
    for name, s in a["byName"].items():
        t = b["byName"][name]
        for field in ("type", "flags", "align", "entryBytes"):
            assert s[field] == t[field], (name, field)
        if name not in metadata:
            assert s["bytes"] == t["bytes"] and s["payload"] == t["payload"], name
            checked.append(name)
        if s["flags"] & 4 or name in {".text", ".rodata", ".data", ".bss", ".init_array", ".fini_array", ".got", ".got.plt", ".eh_frame", ".eh_frame_hdr", "pck"}:
            assert s["vaddr"] == t["vaddr"], (name, "virtual address changed")
    assert b["byName"][".dynstr"]["payload"].startswith(a["byName"][".dynstr"]["payload"])
    address_tags = {5: ".dynstr", 6: ".dynsym", 4: ".hash", 0x6ffffef5: ".gnu.hash",
        23: ".rela.plt", 7: ".rela.dyn", 0x6ffffffe: ".gnu.version_r", 0x6ffffff0: ".gnu.version"}

    def normalize(elf):
        rows = []
        for tag, value in elf["dynamic"]:
            if tag == 1:
                continue
            if tag == 10:
                assert value == elf["byName"][".dynstr"]["bytes"]
                value = ".dynstr size"
            elif tag in address_tags:
                name = address_tags[tag]
                assert value == elf["byName"][name]["vaddr"], (tag, name)
                value = name
            rows.append([tag, value])
        return rows

    assert normalize(a) == normalize(b), "Unexpected dynamic semantic change"
    assert not any(p[0] == 1 and p[1] & 3 == 3 for p in b["programHeaders"]), "New RWX load mapping"
    for segment_type in (3, 0x6474e551):
        before = [p for p in a["programHeaders"] if p[0] == segment_type]
        after = [p for p in b["programHeaders"] if p[0] == segment_type]
        assert len(before) == len(after)
        for p, q in zip(before, after):
            assert p[1] == q[1] and p[5:8] == q[5:8]
            assert a["data"][p[2]:p[2] + p[5]] == b["data"][q[2]:q[2] + q[5]]
    return {"passed": True, "original": summary(a), "derived": summary(b),
        "addedNeeded": needed, "unchangedPayloadSections": checked,
        "normalizedSymbolTablesIdentical": True, "dynamicSemanticsPreserved": True,
        "codeAndStateVirtualAddressesPreserved": True, "noRwxLoadSegments": True,
        "mappedLayout": layout_b,
        "scope": "Static checked ELF transformation; runtime guard/order acceptance is separate."}
