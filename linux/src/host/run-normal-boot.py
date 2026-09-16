#!/usr/bin/env python3
"""Bounded original-engine host with explicit local inputs.

No downloads, installs or changes to global settings.
Every invocation makes a fresh private evidence root and preserves failed attempts.
"""
from __future__ import annotations

import argparse
import ctypes
import grp
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import platform
import pwd
import resource
import shutil
import signal
import stat
import struct
import zipfile
import subprocess
import sys
import time
import traceback
import uuid


AUTH_ROOT = None
AUTH_CANONICAL = None
AUTH_JOB = None
SOURCES = ("HostControls.csproj", "global.json", "project.godot", "Main.tscn",
           "Main.cs", "LinuxSeccompGuard.cs", "ReleaseHostIdentity.cs", "HostControlSuite.cs",
           "GodotPluginsInitializer.cs", "ManagedBootstrap.cs", "EarlyGuardMarker.cs",
           "NormalBootConfig.cs", "NormalGameLoader.cs", "NormalGameRead.cs", "NormalBootLogs.cs", "ProjectSettingsContract.cs", "Actions.cs", "Bridge.cs")
AUX_SOURCES = ("prepare_release_elf.py", "hq_elf.py", "guard-source-manifest.json",
               "normal_boot_inputs.py", "game-input-contract.json",
               "original-project.binary", "expected-script-map.json", "game-assemblies-pinned.json",
               "native-libraries-pinned.json", "profile.save", "prefs.save", "progress.save")
GAME_BASELINE = None
GUARD_SHA256 = "e6a4317547a27b6a5733e0c1825f02ad12a19ddf1d2d4974a2a7ab74bd016902"
GUARD_SOURCE_SHA256 = None

NATIVE_CONTROL_REPORT_SHA256 = "9b51420edd1a4024c0f9e4b4699c69ab91f4e65a5c0623d7e3b53207a80d5936"
FILTER_SHA256 = "0a0656c1ebc6e7f0051f19d358db2139b9ad8c659ebf5ded3bce9424fe8b65d8"
DERIVED_EXE_SHA256 = "1ede4f676c929c7beee8810efd1e0b67626e7b42f0ad2506bef569ee17fbc618"
DERIVED_EXE_BYTES = 70357054
ELF_PREPARER_SHA256 = "2f929854e66493d2168b81b682df955fa4df7aed93c4b182aa9313ed59c7eab0"
ELF_READER_SHA256 = "bab541bf6de0a72727b5b82a668d8aeab4b3db7d475c1db0c14222dcf4420442"
GODOT_BINARY = "Godot_v4.5.1-stable_mono_linux.x86_64"
NAMESPACES = ("mnt", "pid", "net", "user", "uts", "ipc")
IDENTITY_FILES = ("passwd", "group", "nsswitch.conf")
BOOTSTRAP_FILE_LIMIT = 2 * 1024**4  # CoreCLR 9.0.7 x64 memfd logical size, not physical allocation.
PERSISTENT_FILE_LIMIT = 256 * 1024**2
PERSISTENT_TOTAL_LIMIT = 1024**3
PERSISTENT_POLL_SECONDS = 0.25
STOP_SIGNAL: int | None = None


def utc() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path: Path, value: object) -> None:
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def mkdir(path: Path) -> None:
    path.mkdir(mode=0o700)


def ordinary(path: Path, directory: bool = False) -> os.stat_result:
    info = path.lstat()
    wanted = stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)
    if not wanted or (not directory and info.st_nlink != 1):
        raise ValueError(f"Not an ordinary {'directory' if directory else 'single-link file'}: {path}")
    if info.st_uid != os.getuid():
        raise ValueError(f"Private input is owned by another uid: {path}")
    return info


def checked_private(path: Path, private: Path, directory: bool = True) -> Path:
    path = path.absolute()
    relative = path.relative_to(private)
    if ".." in relative.parts:
        raise ValueError("Parent traversal is not allowed")
    current = private
    ordinary(current, True)
    for index, part in enumerate(relative.parts):
        current /= part
        ordinary(current, directory if index == len(relative.parts) - 1 else True)
    return current


def checked_external(path: Path, directory=True):
    path = path.absolute()
    if ".." in path.parts: raise ValueError("Parent traversal")
    for parent in path.parents:
        if parent.is_symlink(): raise ValueError("Symlink input ancestor: " + str(parent))
    ordinary(path, directory)
    return path


def inspect_tree(path: Path) -> dict[str, int]:
    ordinary(path, True)
    count = size = 0
    for parent, directories, files in os.walk(path, followlinks=False):
        for name in directories:
            ordinary(Path(parent) / name, True)
        for name in files:
            info = ordinary(Path(parent) / name)
            count += 1
            size += info.st_size
    return {"regularFileCount": count, "fileBytes": size}


def copy_file(source: Path, destination: Path) -> dict[str, object]:
    before = ordinary(source)
    required = before.st_size + 1024 ** 3
    if shutil.disk_usage(destination.parent).free < required:
        raise OSError(f"Insufficient disk space before ordinary copy: need {required} bytes")
    with source.open("rb") as incoming, destination.open("xb") as outgoing:
        while block := incoming.read(1024 * 1024):
            outgoing.write(block)
        outgoing.flush()
        os.fsync(outgoing.fileno())
    os.chmod(destination, 0o600)
    after, copied = ordinary(source), ordinary(destination)
    original_hash, copied_hash = digest(source), digest(destination)
    if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (
            after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns):
        raise RuntimeError(f"Source changed during ordinary copy: {source}")
    if (after.st_dev, after.st_ino) == (copied.st_dev, copied.st_ino) or original_hash != copied_hash:
        raise RuntimeError(f"Copy identity or hash check failed: {source}")
    return {"source": str(source), "destination": str(destination), "bytes": copied.st_size,
            "sha256": copied_hash, "destinationNlink": copied.st_nlink}


def copy_tree(source: Path, destination: Path) -> list[dict[str, object]]:
    ordinary(source, True)
    mkdir(destination)
    records = []
    for item in sorted(source.iterdir()):
        if stat.S_ISDIR(item.lstat().st_mode):
            records.extend(copy_tree(item, destination / item.name))
        else:
            records.append(copy_file(item, destination / item.name))
    return records


def project_identity(user: pwd.struct_passwd, group: grp.struct_group,
                     uid: int, euid: int, gid: int, egid: int) -> dict[str, object]:
    """Project only this actual account; do not copy passwords, GECOS or group members."""
    if any(type(number) is not int or not 0 <= number < 2**32 - 1
           for number in (uid, euid, gid, egid, user.pw_uid, user.pw_gid, group.gr_gid)):
        raise ValueError("Identity requires non-reserved uint32 UID/GID values")
    if uid != euid or gid != egid or user.pw_uid != uid or user.pw_gid != gid or group.gr_gid != gid:
        raise ValueError("Real/effective identity and the account primary UID/GID must agree")

    def field(value: str, label: str, *, path: bool = False, empty: bool = False) -> str:
        if not isinstance(value, str) or (not value and not empty) or len(value) > 4096:
            raise ValueError(f"Invalid identity field: {label}")
        if any(character in ":\\" or not character.isprintable() for character in value):
            raise ValueError(f"Unsafe identity field: {label}")
        if path:
            if value and (not value.startswith("/") or value.startswith("//")
                          or str(PurePosixPath(value)) != value or ".." in PurePosixPath(value).parts):
                raise ValueError(f"Identity path must be an ordinary absolute POSIX path: {label}")
        elif (len(value) > 255 or "/" in value or value in (".", "..") or value.startswith(("+", "-"))
              or any(c.isspace() for c in value)):
            raise ValueError(f"Identity name must not contain path components or whitespace: {label}")
        return value

    return {"userName": field(user.pw_name, "userName"), "userId": uid, "groupId": gid,
            "groupName": field(group.gr_name, "groupName"),
            "homeDirectory": field(user.pw_dir, "homeDirectory", path=True),
            "shell": field(user.pw_shell, "shell", path=True, empty=True)}


def create_identity(directory: Path) -> dict[str, object]:
    uid, euid, gid, egid = os.getuid(), os.geteuid(), os.getgid(), os.getegid()
    user = pwd.getpwuid(uid)  # No getpwall/getgrall, full database or credential-file reads.
    identity = project_identity(user, grp.getgrgid(gid), uid, euid, gid, egid)
    contents = {"passwd": f'{identity["userName"]}:*:{uid}:{gid}::{identity["homeDirectory"]}:{identity["shell"]}\n',
                "group": f'{identity["groupName"]}:*:{gid}:\n',
                "nsswitch.conf": "passwd: files\ngroup: files\ninitgroups: files\n"}
    mkdir(directory)
    for name in IDENTITY_FILES:
        with (directory / name).open("x", encoding="utf-8") as stream:
            stream.write(contents[name])
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(directory / name, 0o400)
        ordinary(directory / name)
    identity["filesSha256"] = {name: digest(directory / name) for name in IDENTITY_FILES}
    write_json(directory.parent / "identity-input.json", {"source": "pwd.getpwuid(real_uid), grp.getgrgid(real_gid)",
        "projection": identity, "originalHomeValue": os.environ.get("HOME"),
        "omitted": ["password fields", "GECOS", "group member lists", "all other account records"],
        "files": [{"hostPath": str(directory / name), "namespacePath": "/etc/" + name,
                   "mode": "0400", "binding": "read-only"} for name in IDENTITY_FILES]})
    return identity


def verify_identity(directory: Path, identity: dict[str, object]) -> dict[str, object]:
    try:
        ordinary(directory, True)
        files = []
        for name in IDENTITY_FILES:
            info = ordinary(directory / name)
            files.append({"name": name, "sha256": digest(directory / name), "mode": stat.S_IMODE(info.st_mode)})
        passed = (sorted(path.name for path in directory.iterdir()) == sorted(IDENTITY_FILES)
                  and all(item["mode"] == 0o400 and item["sha256"] == identity["filesSha256"][item["name"]]
                          for item in files))
        return {"passed": passed, "files": files}
    except Exception as error:
        return {"passed": False, "error": repr(error)}


def process_table() -> dict[int, dict[str, object]]:
    rows = {}
    for path in Path("/proc").iterdir():
        if not path.name.isdecimal():
            continue
        try:
            if path.stat().st_uid != os.getuid():
                continue
            raw = (path / "stat").read_text()
            fields = raw[raw.rfind(")") + 2:].split()
            rows[int(path.name)] = {"pid": int(path.name), "ppid": int(fields[1]),
                                    "state": fields[0], "startTicks": int(fields[19]),
                                    "rssBytes": max(0, int(fields[21])) * os.sysconf("SC_PAGE_SIZE")}
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
    return rows


def discover(rows: dict[int, dict[str, object]], known: dict[int, int], initial_children: set[int]) -> None:
    while True:
        active = {pid for pid, tick in known.items() if rows.get(pid, {}).get("startTicks") == tick}
        found = {pid: int(row["startTicks"]) for pid, row in rows.items() if pid not in known
                 and (row["ppid"] in active or (row["ppid"] == os.getpid() and pid not in initial_children))}
        if not found:
            return
        known.update(found)


def live_refs(rows: dict[int, dict[str, object]], known: dict[int, int]) -> list[dict[str, object]]:
    return [rows[pid] for pid, tick in known.items()
            if rows.get(pid, {}).get("startTicks") == tick and rows[pid]["state"] != "Z"]


def send_owned(pid: int, start_ticks: int, number: int) -> dict[str, object]:
    result: dict[str, object] = {"pid": pid, "startTicks": start_ticks, "signal": number}
    try:
        if process_table().get(pid, {}).get("startTicks") != start_ticks:
            result["result"] = "already_gone_or_identity_changed"
            return result
        descriptor = None
        try:
            if hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal"):
                try:
                    descriptor = os.pidfd_open(pid)
                except OSError as error:
                    if error.errno not in (22, 38):
                        raise
            if process_table().get(pid, {}).get("startTicks") != start_ticks:
                result["result"] = "identity_changed_before_signal"
            elif descriptor is not None:
                signal.pidfd_send_signal(descriptor, number)
                result["result"] = "sent_with_pidfd"
            else:
                os.kill(pid, number)
                result["result"] = "sent_after_starttime_check"
        finally:
            if descriptor is not None:
                os.close(descriptor)
    except ProcessLookupError:
        result["result"] = "already_gone"
    except OSError as error:
        result.update(result="signal_failed", errno=error.errno, error=str(error))
    return result


def reap_adopted(known: dict[int, int], launcher: int) -> list[dict[str, int]]:
    reaped = []
    rows = process_table()
    for pid, tick in known.items():
        if pid == launcher:
            continue
        row = rows.get(pid)
        if row is None or row["startTicks"] != tick or row["ppid"] != os.getpid():
            continue
        try:
            got, status = os.waitpid(pid, os.WNOHANG)
            if got:
                reaped.append({"pid": got, "waitStatus": status, "exitCode": os.waitstatus_to_exitcode(status)})
        except ChildProcessError:
            pass
    return reaped


def persistent_snapshot(roots: tuple[Path, ...], *, file_limit: int = PERSISTENT_FILE_LIMIT,
                        total_limit: int = PERSISTENT_TOTAL_LIMIT) -> dict[str, object]:
    """Metadata-only polling of the current work/log roots; never follows symlinks.

    This is delayed supervision, not a hard filesystem quota. Anonymous memfds are
    deliberately outside these named persistent roots and remain under RSS supervision.
    """
    started = time.monotonic()
    seen: set[tuple[int, int]] = set()
    total = allocated = entries = vanished = non_regular = duplicates = 0
    largest: dict[str, object] | None = None
    errors, violations = [], []

    def finish() -> dict[str, object]:
        return {"roots": [str(path) for path in roots], "logicalBytes": total, "allocatedBytes": allocated,
            "uniqueRegularFiles": len(seen), "entriesObserved": entries, "transientMissingEntries": vanished,
            "nonRegularEntriesNotFollowed": non_regular, "duplicateInodesCountedOnce": duplicates,
            "largestFile": largest, "errors": errors, "violations": violations,
            "scanSeconds": time.monotonic() - started, "passed": not errors and not violations}

    def walk_error(error: OSError) -> None:
        nonlocal vanished
        if isinstance(error, FileNotFoundError):
            vanished += 1  # A temporary name can disappear during a scan; retain the observation count.
        else:
            errors.append({"path": str(error.filename), "errno": error.errno, "error": str(error)})

    for root in roots:
        try:
            ordinary(root, True)
            # fwalk's directory descriptors and follow_symlinks=False avoid scanning link targets.
            for directory, directories, files, descriptor in os.fwalk(root, follow_symlinks=False, onerror=walk_error):
                for name in directories + files:
                    entries += 1
                    if entries > 20000:
                        violations.append({"kind": "scan_entry_bound", "limit": 20000})
                        return finish()
                    path = str(Path(directory) / name)
                    try:
                        info = os.stat(name, dir_fd=descriptor, follow_symlinks=False)
                    except OSError as error:
                        walk_error(error)
                        continue
                    if stat.S_ISDIR(info.st_mode):
                        continue
                    if not stat.S_ISREG(info.st_mode):
                        non_regular += 1
                        continue
                    identity = (info.st_dev, info.st_ino)
                    if identity in seen:
                        duplicates += 1
                        continue
                    seen.add(identity)
                    total += info.st_size
                    allocated += info.st_blocks * 512
                    if largest is None or info.st_size > largest["bytes"]:
                        largest = {"path": path, "bytes": info.st_size}
                    if info.st_size > file_limit:
                        violations.append({"kind": "file_size", "path": path, "bytes": info.st_size, "limit": file_limit})
                    if total > total_limit:
                        violations.append({"kind": "aggregate_size", "observedBytes": total, "limit": total_limit})
                    if violations:
                        return finish()  # This is already a failing lower bound; stop before scanning more.
        except (OSError, ValueError) as error:
            errors.append({"path": str(root), "errno": getattr(error, "errno", None), "error": str(error)})
    return finish()


def run_stage(root: Path, name: str, command: list[str], environment: dict[str, str],
              wall_seconds: int, cpu_seconds: int, rss_limit: int, *, work: Path) -> dict[str, object]:
    global STOP_SIGNAL
    if work not in (root / "build-work", root / "runtime-work"):
        raise ValueError("Only this run's explicit writable work directory may be monitored")
    ordinary(work, True)
    stage = root / name
    mkdir(stage)
    record: dict[str, object] = {"stage": name, "processRunId": environment["PROCESS_RUN_ID"],
        "startedUtc": utc(), "argv": command, "environment": environment, "stdin": "DEVNULL",
        "closeFds": True, "startNewSession": True, "wallLimitSeconds": wall_seconds,
        "cpuLimitSoftHardSeconds": [cpu_seconds, cpu_seconds + 1], "rssLimitBytes": rss_limit,
        "coreLimitBytes": 0, "fileLimitBytes": BOOTSTRAP_FILE_LIMIT,
        "fileLimitReason": "CoreCLR 9.0.7 x64 ftruncate(doublemapper memfd, 2 TiB logical size); preserve W^X",
        "requiredHostFileLimitAfterReadyBytes": PERSISTENT_FILE_LIMIT,
        "persistentMonitor": {"roots": [str(work), str(stage)], "perFileLogicalLimitBytes": PERSISTENT_FILE_LIMIT,
            "aggregateLogicalLimitBytes": PERSISTENT_TOTAL_LIMIT, "targetPollIntervalSeconds": PERSISTENT_POLL_SECONDS,
            "entryScanBound": 20000, "mode": "metadata_polling_not_hard_quota",
            "latency": "Sampling, scan time and scheduling can delay termination and permit overshoot; transient files can be missed"}}
    write_json(stage / "command.json", record)
    initial = process_table()
    initial_children = {pid for pid, row in initial.items() if row["ppid"] == os.getpid()}
    known: dict[int, int] = {}
    reaped, signals = [], []
    maximum_rss, reason, launcher_exit_at = 0, None, None
    started = time.monotonic()
    storage_stats: dict[str, object] = {"sampleCount": 0, "maximumLogicalBytes": 0, "maximumAllocatedBytes": 0,
        "maximumFileBytes": 0, "maximumScanSeconds": 0.0, "maximumSampleGapSeconds": 0.0}
    latest_storage: dict[str, object] | None = None
    last_sample_started: float | None = None
    next_sample = 0.0

    def limits() -> None:
        os.umask(0o077)
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        resource.setrlimit(resource.RLIMIT_CPU, (cpu_seconds, cpu_seconds + 1))
        resource.setrlimit(resource.RLIMIT_FSIZE, (BOOTSTRAP_FILE_LIMIT,) * 2)

    with (stage / "stdout.txt").open("xb") as stdout, (stage / "stderr.txt").open("xb") as stderr, \
            (stage / "persistent-samples.jsonl").open("x", encoding="utf-8") as storage:
        def sample_storage(force: bool = False) -> str | None:
            nonlocal latest_storage, last_sample_started, next_sample
            now = time.monotonic()
            if not force and now < next_sample:
                return None
            latest_storage = persistent_snapshot((work, stage))
            latest_storage["stageElapsedSeconds"] = now - started
            storage.write(json.dumps(latest_storage, sort_keys=True) + "\n")
            storage.flush()
            storage_stats["sampleCount"] += 1
            for field, observed in (("maximumLogicalBytes", latest_storage["logicalBytes"]),
                                    ("maximumAllocatedBytes", latest_storage["allocatedBytes"]),
                                    ("maximumFileBytes", (latest_storage["largestFile"] or {}).get("bytes", 0)),
                                    ("maximumScanSeconds", latest_storage["scanSeconds"])):
                storage_stats[field] = max(storage_stats[field], observed)
            if last_sample_started is not None:
                storage_stats["maximumSampleGapSeconds"] = max(storage_stats["maximumSampleGapSeconds"], now - last_sample_started)
            last_sample_started, next_sample = now, now + PERSISTENT_POLL_SECONDS
            if latest_storage["errors"]:
                return "persistent_file_scan_failed"
            if latest_storage["violations"]:
                return "persistent_file_budget_exceeded"
            return None

        child = subprocess.Popen(command, cwd=root, env=environment, stdin=subprocess.DEVNULL,
                                 stdout=stdout, stderr=stderr, close_fds=True,
                                 start_new_session=True, preexec_fn=limits)
        record["launcherPid"] = child.pid
        try:
            while True:
                rows = process_table()
                if child.pid in rows and child.pid not in known:
                    known[child.pid] = int(rows[child.pid]["startTicks"])
                discover(rows, known, initial_children)
                current = live_refs(rows, known)
                rss = sum(int(row["rssBytes"]) for row in current)
                maximum_rss = max(maximum_rss, rss)
                return_code = child.poll()
                reaped.extend(reap_adopted(known, child.pid))
                if STOP_SIGNAL is not None:
                    reason = f"supervisor_signal_{STOP_SIGNAL}"
                elif time.monotonic() - started > wall_seconds:
                    reason = "wall_timeout"
                elif rss > rss_limit:
                    reason = "sampled_aggregate_rss_limit"
                else:
                    reason = sample_storage(force=return_code is not None)
                if reason is None and return_code is not None:
                    launcher_exit_at = launcher_exit_at or time.monotonic()
                    if not current:
                        break
                    if time.monotonic() - launcher_exit_at > 0.5:
                        reason = "descendants_remained_after_launcher_exit"
                if reason:
                    break
                time.sleep(0.05)
        except BaseException as error:
            reason = "supervisor_exception"
            record["error"] = repr(error)
            record["traceback"] = traceback.format_exc()
        finally:
            for number, grace in ((signal.SIGTERM, 1.0), (signal.SIGKILL, 2.0)):
                deadline = time.monotonic() + grace
                while True:
                    rows = process_table()
                    discover(rows, known, initial_children)
                    remaining = live_refs(rows, known)
                    if not remaining:
                        break
                    for row in remaining:
                        signals.append(send_owned(int(row["pid"]), int(row["startTicks"]), number))
                    child.poll()
                    reaped.extend(reap_adopted(known, child.pid))
                    if time.monotonic() >= deadline:
                        break
                    time.sleep(0.1)
            try:
                return_code = child.wait(timeout=1)
            except subprocess.TimeoutExpired:
                return_code = None
                reason = reason or "launcher_not_reaped"
            reaped.extend(reap_adopted(known, child.pid))
            rows = process_table()
            discover(rows, known, initial_children)
            remaining = live_refs(rows, known)
            try:
                final_storage_reason = sample_storage(force=True)
                reason = reason or final_storage_reason
            except Exception as error:
                reason = reason or "persistent_file_scan_failed"
                storage_stats["finalSampleError"] = repr(error)
            record.update(finishedUtc=utc(), wallSeconds=time.monotonic() - started,
                          rawReturnCode=return_code, stopReason=reason, maximumSampledRssBytes=maximum_rss,
                          processIdentities=[{"pid": pid, "startTicks": tick} for pid, tick in sorted(known.items())],
                          cleanupSignals=signals, reapedAdoptedChildren=reaped, remainingLiveOwnedProcesses=remaining,
                          persistentSamples=storage_stats, finalPersistentSample=latest_storage,
                          passed=return_code == 0 and reason is None and not remaining)
            write_json(stage / "result.json", record)
    print(json.dumps({"stage": name, "passed": record["passed"], "rawReturnCode": record["rawReturnCode"],
                      "stopReason": reason, "evidenceRoot": str(root)}), flush=True)
    return record


def clean_environment(process_id: str) -> dict[str, str]:
    environment = {"PATH": "/dotnet:/usr/bin:/bin", "LANG": "C.UTF-8", "LC_ALL": "C.UTF-8",
        "DOTNET_ROOT": "/dotnet", "DOTNET_ROOT_X64": "/dotnet", "DOTNET_HOST_PATH": "/dotnet/dotnet",
        "DOTNET_CLI_HOME": "/work/dotnet-cli", "XDG_DATA_HOME": "/work/xdg-data",
        "XDG_CACHE_HOME": "/work/xdg-cache", "XDG_CONFIG_HOME": "/work/xdg-config",
        "XDG_RUNTIME_DIR": "/work/xdg-runtime", "TMPDIR": "/work/tmp", "TMP": "/work/tmp", "TEMP": "/work/tmp",
        "NUGET_PACKAGES": "/work/nuget-packages", "NUGET_HTTP_CACHE_PATH": "/work/nuget-http",
        "NUGET_PLUGINS_CACHE_PATH": "/work/nuget-plugins", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "false",
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true", "DOTNET_NOLOGO": "1",
        "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "DOTNET_EnableDiagnostics": "0",
        "COMPlus_DbgEnableMiniDump": "0", "DOTNET_PROCESSOR_COUNT": "2",
        "DOTNET_EnableWriteXorExecute": "1",
        "SDL_JOYSTICK_DISABLE_UDEV": "1", "SDL_HIDAPI_UDEV": "0",
        "PROCESS_RUN_ID": process_id}
    for name in ("HOME", "USER", "LOGNAME"):
        if name in os.environ:
            environment[name] = os.environ[name]  # Preserve HOME; never point it at the task directory.
    return environment


def make_work(path: Path) -> None:
    mkdir(path)
    for name in ("project", "dotnet-cli", "xdg-data", "xdg-cache", "xdg-config", "xdg-runtime", "tmp",
                 "nuget-packages", "nuget-http", "nuget-plugins", "shm"):
        mkdir(path / name)


def bwrap_command(binary: str, dotnet: Path, godot: Path, work: Path, readonly: Path,
                  identity: Path, command: list[str], *, userland: Path, guard: Path | None = None,
                  game: Path | None = None, observation_stage: Path | None = None) -> list[str]:
    args = [binary, "--unshare-user", "--unshare-pid", "--unshare-net", "--unshare-ipc", "--unshare-uts",
            "--die-with-parent", "--new-session", "--cap-drop", "ALL", "--ro-bind", str(userland / "usr"), "/usr"]
    for name, target in USERLAND_TOP_ALIASES.items():
        ordinary(userland / target, True)
        args += ["--symlink", target, "/" + name]
    args += ["--dir", "/etc"]
    for relative in ("etc/os-release", "etc/fonts"):
        source = userland / relative
        ordinary(source, relative == "etc/fonts")
        args += ["--ro-bind", str(source), "/" + relative]
    ordinary(identity, True)
    for name in IDENTITY_FILES:
        ordinary(identity / name)
        args += ["--ro-bind", str(identity / name), "/etc/" + name]
    args += ["--proc", "/proc", "--dev", "/dev",
             "--ro-bind", str(dotnet), "/dotnet", "--ro-bind", str(godot), "/godot",
             "--ro-bind", str(readonly), "/readonly", "--bind", str(work), "/work",
             "--bind", str(work / "tmp"), "/tmp", "--bind", str(work / "shm"), "/dev/shm"]
    if guard is not None:
        ordinary(guard, True)
        args += ["--ro-bind", str(guard), "/guard"]
    if game is not None:
        ordinary(game, True)
        if stat.S_IMODE(game.stat().st_mode) & 0o222:
            raise ValueError("Normal game input directory must be immutable")
        args += ["--ro-bind", str(game), "/game"]
    if observation_stage is not None:
        if game is None or observation_stage != work.parent / "godot-normal-boot":
            raise ValueError("Only this normal stage may provide its read-only output streams")
        # run_stage opens these new source files before Popen invokes bwrap. The game
        # sees read-only binds; the original supervisor stream writers remain unchanged.
        for stream in ("stdout", "stderr"):
            destination = work / ("normal-game." + stream + ".log")
            with destination.open("xb"):
                pass
            os.chmod(destination, 0o400)
            args += ["--ro-bind", str(observation_stage / (stream + ".txt")),
                     "/work/normal-game." + stream + ".log"]
    args += ["--chdir", "/work/project",
             "--remount-ro", "/dev/pts", "--remount-ro", "/dev", "--remount-ro", "/proc",
             "--remount-ro", "/", "--"]
    return args + command



EXE_SHA256 = "0b0ae3859c4c26d352b6483dc1cb11601a07bbc9890d97bcebae0d5d0bdd99ff"
EXE_BYTES = 70349168
RUNTIME_PACKAGE = "microsoft.netcore.app.runtime.linux-x64.9.0.7.nupkg"
RUNTIME_SHA256 = "dcee5d2ead014561d75a5d09655a9d7aa29c4f3258c5dc638765dcb8eed37cae"
RESTORE_PACKAGES = {
    RUNTIME_PACKAGE: {"packageId": "Microsoft.NETCore.App.Runtime.linux-x64", "version": "9.0.7",
                      "sha256": RUNTIME_SHA256, "bytes": 38892350, "zipMemberCount": 199,
                      "role": "runtime_and_restore"},
    "microsoft.aspnetcore.app.runtime.linux-x64.9.0.7.nupkg": {
        "packageId": "Microsoft.AspNetCore.App.Runtime.linux-x64", "version": "9.0.7",
        "sha256": "d6dfa9dc6cfd3886e05400c9c0eba4fb6e7a5ff22ab936b87acd101b0f798700",
        "bytes": 12488631, "zipMemberCount": 151, "role": "restore_download_only"},
}
API_SHA256 = "0e4897ecdfb31456a97c7d8028dfb8d7dbdc632e2f73fc9b438d7b266a139289"
ASSEMBLY_NAME = "E003LinuxHostControls"
DATA_NAME = "data_E003LinuxHostControls_linuxbsd_x86_64"


def verify_executable(path: Path, expected: str) -> dict[str, object]:
    ordinary(path)
    if expected != EXE_SHA256 or path.stat().st_size != EXE_BYTES or digest(path) != expected:
        raise ValueError("Executable differs from the fixed same-manifest Linux input")
    adjacent = [path.with_suffix(".pck"), Path(str(path) + ".pck"),
                path.parent / "data.pck", path.parent / "project.godot"]
    if any(item.exists() or item.is_symlink() for item in adjacent):
        raise ValueError("Executable working directory contains a discoverable pack or project")
    with path.open("rb") as stream:
        header = stream.read(64)
        if header[:7] != b"\x7fELF\x02\x01\x01" or struct.unpack_from("<HH", header, 16) != (2, 62):
            raise ValueError("Expected the reviewed Linux x64 ELF executable")
        offset = struct.unpack_from("<Q", header, 40)[0]
        entry_size, count, names_index = struct.unpack_from("<HHH", header, 58)
        if entry_size != 64 or not (0 < names_index < count <= 256) or offset + count * 64 > EXE_BYTES:
            raise ValueError("Unexpected ELF section table")
        stream.seek(offset)
        entries = [struct.unpack("<IIQQQQIIQQ", stream.read(64)) for _ in range(count)]
        names_entry = entries[names_index]
        if names_entry[5] > 65536 or names_entry[4] + names_entry[5] > EXE_BYTES:
            raise ValueError("Unexpected ELF section names")
        stream.seek(names_entry[4])
        names = stream.read(names_entry[5])
        packs = [entry for entry in entries if names[entry[0]:].split(b"\0", 1)[0] == b"pck"]
        if len(packs) != 1 or packs[0][4:6] != (64131784, 8):
            raise ValueError("Reviewed empty pck section changed")
        stream.seek(packs[0][4])
        pack_bytes = stream.read(16)
        stream.seek(-12, os.SEEK_END)
        trailer = stream.read(12)
        stream.seek(55345416)
        version = stream.read(64).split(b"\0", 1)[0]
    if any(pack_bytes[index:index + 4] == b"GDPC" for index in range(8)) or trailer[-4:] == b"GDPC":
        raise ValueError("Standard embedded PCK marker is forbidden")
    if version != b"4.5.1.m.14.mono.custom_build":
        raise ValueError("Reviewed static native version string changed")
    return {"path": str(path), "sha256": expected, "bytes": EXE_BYTES,
            "elfClass": 64, "machine": 62, "pckSectionOffset": 64131784,
            "pckSectionBytes": 8, "standardEmbeddedPackMarkersAbsent": True,
            "adjacentPackAndProjectAbsent": True, "staticVersionString": version.decode()}


def verify_runtime_feed(feed: Path) -> dict[str, object]:
    ordinary(feed, True)
    if {path.name for path in feed.iterdir()} != set(RESTORE_PACKAGES):
        raise ValueError("Runtime feed must contain exactly the two fixed Linux 9.0.7 packages")
    packages = {}
    runtime_dlls = []
    for name, expected in RESTORE_PACKAGES.items():
        package = feed / name
        info = ordinary(package)
        if info.st_size != expected["bytes"] or digest(package) != expected["sha256"]:
            raise ValueError(f"Runtime package differs from the fixed HQ-verified NuGet input: {name}")
        with zipfile.ZipFile(package) as archive:
            members = archive.namelist()
            if len(members) != expected["zipMemberCount"]:
                raise ValueError(f"Fixed runtime package member count changed: {name}")
            if name == RUNTIME_PACKAGE:
                runtime_dlls = sorted(Path(member).name for member in members
                    if member.startswith("runtimes/linux-x64/lib/net9.0/") and member.endswith(".dll"))
        packages[name] = {"path": str(package), **expected}
    if not runtime_dlls or "System.Private.CoreLib.dll" not in runtime_dlls:
        raise ValueError("The fixed runtime package has no expected managed runtime")
    core = packages[RUNTIME_PACKAGE]
    return {"path": core["path"], "sha256": core["sha256"], "bytes": core["bytes"],
            "zipMemberCount": core["zipMemberCount"], "runtimeDllNames": runtime_dlls,
            "restorePackages": packages}


USERLAND_SCHEMA = "e004b-private-userland-v1"
USERLAND_ARCHIVE_MANIFEST_SHA256 = "4750928de056148298e0f1010c347a6a2e85ab58f89d47194ee6b4f944cdbb7d"
USERLAND_BASE_SHA256 = "242cd8898b33ea806ef5f13b1076ed7c76f9f989d18384452f7166692438ff1a"
USERLAND_TOP_ALIASES = {"bin": "usr/bin", "sbin": "usr/sbin", "lib": "usr/lib", "lib64": "usr/lib64"}
USERLAND_REQUIRED_FILES = (
    "usr/bin/true", "usr/lib64/ld-linux-x86-64.so.2", "usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2",
    "usr/lib/x86_64-linux-gnu/libc.so.6", "usr/lib/x86_64-linux-gnu/libm.so.6",
    "usr/lib/x86_64-linux-gnu/libdl.so.2", "usr/lib/x86_64-linux-gnu/libpthread.so.0",
    "usr/lib/x86_64-linux-gnu/libstdc++.so.6", "usr/lib/x86_64-linux-gnu/libgcc_s.so.1",
    "usr/lib/x86_64-linux-gnu/libicuuc.so.70", "usr/lib/x86_64-linux-gnu/libicui18n.so.70",
    "usr/lib/x86_64-linux-gnu/libicudata.so.70", "usr/lib/x86_64-linux-gnu/libcurl.so.4",
    "usr/lib/x86_64-linux-gnu/libssl.so.3", "usr/lib/x86_64-linux-gnu/libcrypto.so.3",
    "usr/lib/x86_64-linux-gnu/libfontconfig.so.1", "usr/lib/x86_64-linux-gnu/libfreetype.so.6",
    "usr/lib/x86_64-linux-gnu/libpng16.so.16", "usr/lib/x86_64-linux-gnu/libexpat.so.1",
    "usr/lib/x86_64-linux-gnu/libnghttp2.so.14", "usr/lib/x86_64-linux-gnu/libpsl.so.5",
    "usr/lib/x86_64-linux-gnu/libldap-2.5.so.0", "usr/lib/x86_64-linux-gnu/liblber-2.5.so.0",
    "usr/lib/x86_64-linux-gnu/librtmp.so.1", "usr/lib/x86_64-linux-gnu/libssh.so.4",
    "usr/lib/x86_64-linux-gnu/libsasl2.so.2", "usr/lib/x86_64-linux-gnu/libbrotlidec.so.1",
    "usr/lib/x86_64-linux-gnu/libbrotlicommon.so.1", "usr/lib/locale/C.utf8/LC_CTYPE",
    "usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "etc/fonts/fonts.conf", "etc/os-release")


def valid_sha256(value: object) -> bool:
    return isinstance(value, str) and len(value) == 64 and all(char in "0123456789abcdef" for char in value)


def userland_relative(value: object) -> str:
    if not isinstance(value, str) or not value or "\\" in value:
        raise ValueError("Userland paths must be nonempty relative POSIX strings")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or str(path) != value or path.parts[0] not in ("usr", "etc"):
        raise ValueError(f"Invalid userland manifest path: {value}")
    return value


def verify_userland(root: Path, manifest_path: Path, expected_sha256: str) -> dict[str, object]:
    ordinary(root, True)
    manifest_before = ordinary(manifest_path)
    if not valid_sha256(expected_sha256) or manifest_before.st_size > 32 * 1024**2:
        raise ValueError("A fixed SHA-256 and bounded userland manifest are required")
    data = manifest_path.read_bytes()
    if hashlib.sha256(data).hexdigest() != expected_sha256:
        raise ValueError("Userland manifest SHA-256 mismatch")
    manifest = json.loads(data)
    if not (manifest.get("schema") == USERLAND_SCHEMA and manifest.get("baseVersion") == "22.04.5"
            and manifest.get("glibcPackageVersion") == "2.35-0ubuntu3.8"
            and manifest.get("ordinaryFilesOnly") is True and manifest.get("maintainerScriptsExecuted") is False
            and manifest.get("gameExecuted") is False
            and manifest.get("archiveManifestSha256") == USERLAND_ARCHIVE_MANIFEST_SHA256):
        raise ValueError("Userland manifest does not match the reviewed base and preparation contract")
    aliases = manifest.get("topAliases", {})
    if any(aliases.get(name) != target for name, target in USERLAND_TOP_ALIASES.items()):
        raise ValueError("Required Ubuntu top-level aliases changed")
    archives = manifest.get("archives")
    if not isinstance(archives, list) or len(archives) != 18 or not all(
            isinstance(item, dict) and isinstance(item.get("path"), str)
            and PurePosixPath(item["path"]).name == item["path"] and valid_sha256(item.get("sha256"))
            and isinstance(item.get("package"), str) and isinstance(item.get("version"), str)
            and isinstance(item.get("url"), str) and item["url"].startswith("https://")
            and type(item.get("bytes")) is int and 0 < item["bytes"] < 64 * 1024**2 for item in archives):
        raise ValueError("Userland archive provenance is missing or malformed")
    archive_names = {item["path"] for item in archives}
    if len(archive_names) != 18 or not any(item["package"] == "ubuntu-base"
            and item["version"] == "22.04.5" and item["sha256"] == USERLAND_BASE_SHA256 for item in archives):
        raise ValueError("Fixed Ubuntu base archive is not identified")
    rows = manifest.get("files")
    if not isinstance(rows, list) or not 100 <= len(rows) <= 20000:
        raise ValueError("Unexpected userland file count")
    files = {}
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("Malformed userland file record")
        relative = userland_relative(row.get("path"))
        if (relative in files or type(row.get("bytes")) is not int or not 0 <= row["bytes"] <= 64 * 1024**2
                or not valid_sha256(row.get("sha256")) or type(row.get("mode")) is not int
                or row["mode"] not in (0o400, 0o500) or row.get("nlink") != 1
                or row.get("archiveSource") not in archive_names
                or not isinstance(row.get("materializedAlias"), bool)):
            raise ValueError(f"Malformed or duplicate userland file record: {relative}")
        userland_relative(row.get("resolvedVirtualPath"))
        files[relative] = row
    if manifest.get("fileCount") != len(files) or manifest.get("bytes") != sum(row["bytes"] for row in files.values()):
        raise ValueError("Userland manifest aggregate metadata disagrees")
    if manifest["bytes"] > 512 * 1024**2 or not set(USERLAND_REQUIRED_FILES).issubset(files):
        raise ValueError("Required userland dependencies are missing or the root exceeds its bound")
    if "etc/ld.so.cache" in files or any(path in files for path in ("etc/passwd", "etc/group", "etc/nsswitch.conf")):
        raise ValueError("The root must not supply stale loader cache or an alternate identity")
    observed = set()
    directories = 0
    if {path.name for path in root.iterdir()} != {"usr", "etc"}:
        raise ValueError("Userland root must contain only usr and etc")
    for parent, children, names in os.walk(root, followlinks=False):
        parent_path = Path(parent)
        if stat.S_IMODE(ordinary(parent_path, True).st_mode) != 0o500:
            raise ValueError(f"Userland directory mode differs: {parent_path}")
        directories += 1
        for name in children:
            ordinary(parent_path / name, True)
        for name in names:
            path = parent_path / name
            relative = path.relative_to(root).as_posix()
            before = ordinary(path)
            if relative not in files:
                raise ValueError(f"Unlisted userland file: {relative}")
            row = files[relative]
            if before.st_size != row["bytes"] or stat.S_IMODE(before.st_mode) != row["mode"] or digest(path) != row["sha256"]:
                raise ValueError(f"Userland file content or mode differs: {relative}")
            after = ordinary(path)
            if any(getattr(before, key) != getattr(after, key) for key in ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns")):
                raise ValueError(f"Userland file changed while being verified: {relative}")
            observed.add(relative)
    if observed != set(files):
        raise ValueError("Userland file set differs from the fixed manifest")
    if any(files[name]["mode"] != 0o500 for name in ("usr/bin/true", "usr/lib64/ld-linux-x86-64.so.2")):
        raise ValueError("Required executable or interpreter lacks its fixed executable mode")
    manifest_after = ordinary(manifest_path)
    if any(getattr(manifest_before, key) != getattr(manifest_after, key)
            for key in ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns")):
        raise ValueError("Userland manifest changed while being verified")
    return {"root": str(root), "declaredRoot": manifest.get("root"), "manifestPath": str(manifest_path),
            "manifestSha256": expected_sha256, "schema": USERLAND_SCHEMA, "files": files,
            "fileCount": len(files), "directoryCount": directories, "bytes": manifest["bytes"],
            "archives": archives, "topAliases": USERLAND_TOP_ALIASES,
            "baseVersion": manifest["baseVersion"], "glibcPackageVersion": manifest["glibcPackageVersion"],
            "verifiedCompleteOrdinaryTree": True}


def userland_summary(info: dict[str, object]) -> dict[str, object]:
    return {key: value for key, value in info.items() if key != "files"}


def userland_runtime_config(info: dict[str, object]) -> dict[str, object]:
    files = info["files"]
    visible = {path: row for path, row in files.items()
        if path.startswith("usr/") or path.startswith("etc/fonts/") or path == "etc/os-release"}
    required = {"/" + path: {key: row[key] for key in
        ("path", "sha256", "bytes", "mode", "archiveSource", "resolvedVirtualPath", "materializedAlias")}
        for path in USERLAND_REQUIRED_FILES for row in (files[path],)}
    return {"manifestSha256": info["manifestSha256"], "filesSha256": {"/" + path: row["sha256"] for path, row in visible.items()},
            "requiredDependencies": required, "archives": info["archives"]}


def prepare_native_host(immutable: Path, build: Path, runtime: Path, executable: Path,
                        runtime_info: dict[str, object], evidence: Path, derived_sha256: str) -> dict[str, object]:
    published = build / "publish"
    inspect_tree(published)
    files = sorted(path for path in published.rglob("*") if path.is_file())
    if not 50 <= len(files) <= 500:
        raise ValueError("Unexpected self-contained publish file count")
    required = {ASSEMBLY_NAME + ".dll", ASSEMBLY_NAME + ".deps.json", ASSEMBLY_NAME + ".runtimeconfig.json",
                "GodotSharp.dll", "System.Private.CoreLib.dll", "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so"}
    relative = {str(path.relative_to(published)) for path in files}
    if not required.issubset(relative) or any(Path(name).suffix.lower() in (".pck", ".zip") for name in relative):
        raise ValueError("Incomplete self-contained publish output or unexpected resource pack")
    allowed = set(runtime_info["runtimeDllNames"]) | {ASSEMBLY_NAME + ".dll", "GodotSharp.dll"}
    unknown = sorted(name for name in relative if name.lower().endswith(".dll") and name not in allowed)
    if unknown or digest(published / "GodotSharp.dll") != API_SHA256:
        raise ValueError(f"Unexpected published assemblies or Godot API hash: {unknown}")
    runtime_config = json.loads((published / (ASSEMBLY_NAME + ".runtimeconfig.json")).read_text())
    options = runtime_config.get("runtimeOptions", {})
    frameworks = options.get("includedFrameworks", [])
    if options.get("tfm") != "net9.0" or options.get("framework") or options.get("frameworks") or not any(
            item.get("name") == "Microsoft.NETCore.App" and item.get("version") == "9.0.7" for item in frameworks):
        raise ValueError("Publish runtimeconfig is not self-contained .NET 9.0.7")
    deps = json.loads((published / (ASSEMBLY_NAME + ".deps.json")).read_text())
    if not deps.get("runtimeTarget", {}).get("name", "").endswith("/linux-x64"):
        raise ValueError("Publish deps.json has no fixed linux-x64 runtime target")
    for name in SOURCES:
        copy_file(immutable / name, runtime / "project" / name)
    host = runtime / "native-host"
    mkdir(host)
    executable_record = copy_file(executable, host / "SlayTheSpire2")
    os.chmod(host / "SlayTheSpire2", 0o500)
    if digest(host / "SlayTheSpire2") != derived_sha256:
        raise ValueError("Derived executable changed during its ordinary runtime copy")
    records = copy_tree(published, host / DATA_NAME)
    for path in (host / DATA_NAME).rglob("*"):
        if path.is_file():
            os.chmod(path, 0o400)
    manifest = {str(path.relative_to(published)): digest(path) for path in files}
    write_json(evidence / "runtime-output-copy-manifest.json", records)
    write_json(evidence / "native-executable-copy-manifest.json", executable_record)
    if (runtime / "project/.godot").exists() or any(path.suffix.lower() in (".pck", ".zip")
            for path in (runtime / "project").iterdir()):
        raise ValueError("Runtime project has an alternate generated or packed route")
    return {"assemblyOriginalName": ASSEMBLY_NAME, "publishedFilesSha256": manifest,
            "allowedAssemblyNames": sorted(Path(name).stem for name in allowed),
            "executableSha256": derived_sha256, "parentExecutableSha256": EXE_SHA256, "godotSharpSha256": API_SHA256,
            "runtimeConfig": runtime_config, "runtimeTarget": deps["runtimeTarget"]}


def load_owned_module(path: Path, name: str):
    ordinary(path)
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def verify_guard_provenance(sources):
    guard = AUTH_ROOT / ".private/guard-build/guard.so"
    source_manifest = sources / "guard-source-manifest.json"
    if digest(source_manifest) != GUARD_SOURCE_SHA256 or digest(guard) != GUARD_SHA256:
        raise ValueError("VM guard or fixed source manifest changed")
    for row in json.loads(source_manifest.read_text())["records"][:3]:
        original = guard.parent / Path(row["path"]).name
        ordinary(original)
        if digest(original) != row["sha256"]:
            raise ValueError("VM guard source changed")
    return {"guardLibraryPath": str(guard), "guardLibrarySha256": GUARD_SHA256,
            "guardSourceManifestSha256": GUARD_SOURCE_SHA256, "filterSha256": FILTER_SHA256,
            "vmBuildReceipt": str(guard.parent / "build.json")}


def prepare_guard_bind(evidence: Path, runtime: Path, native_id: str,
                       provenance: dict[str, object]) -> tuple[Path, dict[str, object]]:
    guard = evidence / "guard"
    mkdir(guard)
    record = copy_file(Path(provenance["guardLibraryPath"]), guard / "libE004EarlyGuard.so")
    if record["sha256"] != GUARD_SHA256 or record["bytes"] != 15080:
        raise ValueError("Production guard changed during ordinary copy")
    os.chmod(guard / "libE004EarlyGuard.so", 0o400)
    lineage = {"schema": "e004b-bootstrap-lineage-v1", "processRunId": native_id,
               "parentExecutableSha256": EXE_SHA256, "derivedExecutableSha256": DERIVED_EXE_SHA256,
               "guardLibrarySha256": GUARD_SHA256, "guardSourceManifestSha256": GUARD_SOURCE_SHA256,
               "filterSha256": FILTER_SHA256, "operation": "ordinary-copy-plus-one-DT_NEEDED"}
    write_json(guard / "e004b-bootstrap-lineage.json", lineage)
    os.chmod(guard / "e004b-bootstrap-lineage.json", 0o400)
    lineage_hash = digest(guard / "e004b-bootstrap-lineage.json")
    os.chmod(guard, 0o500)
    config = {"schema": "e004b-managed-bootstrap-config-v2", "processRunId": native_id,
              "parentExecutableSha256": EXE_SHA256, "derivedExecutableSha256": DERIVED_EXE_SHA256,
              "guardLibraryPath": "/guard/libE004EarlyGuard.so", "guardLibrarySha256": GUARD_SHA256,
              "lineageManifestPath": "/guard/e004b-bootstrap-lineage.json", "lineageManifestSha256": lineage_hash}
    write_json(runtime / "managed-bootstrap-config.json", config)
    if stat.S_IMODE((runtime / "managed-bootstrap-config.json").stat().st_mode) != 0o600:
        raise ValueError("Managed bootstrap configuration must have mode 0600")
    for name in ("e004b-early-guard.marker", "managed-bootstrap-events.jsonl", "managed-bootstrap-failure.json"):
        if (runtime / name).exists() or (runtime / name).is_symlink():
            raise ValueError("Refuse stale early-entry evidence before launch")
    info = {**provenance, "lineageManifestSha256": lineage_hash, "copy": record}
    write_json(evidence / "guard-copy-manifest.json", info)
    return guard, info


def verify_normal_host_payload(runtime: Path, config: dict[str, object]) -> dict[str, object]:
    """Independent post-process byte validation; never load the executable or DLLs."""
    host = runtime / "native-host"
    data = host / DATA_NAME
    inspect_tree(data)
    observed = {}
    for path in sorted(data.rglob("*")):
        if path.is_dir():
            continue
        info = ordinary(path)
        if stat.S_IMODE(info.st_mode) & 0o222:
            raise ValueError("Published runtime file acquired write bits")
        observed[str(path.relative_to(data))] = digest(path)
    executable = host / "SlayTheSpire2"
    info = ordinary(executable)
    if (observed != config["publishedFilesSha256"] or digest(executable) != config["executableSha256"]
            or info.st_size != DERIVED_EXE_BYTES or stat.S_IMODE(info.st_mode) != 0o500):
        raise ValueError("Published runtime or derived executable bytes changed after the normal phase")
    return {"passed": True, "fileCount": len(observed), "publishedFilesSha256": observed,
            "derivedExecutableSha256": digest(executable), "derivedExecutableBytes": info.st_size,
            "verification": "Outer supervisor reads every actual ordinary copy after process cleanup"}


class BuildComplete(Exception):
    pass


def main() -> int:
    global AUTH_ROOT, AUTH_CANONICAL, GAME_BASELINE, GUARD_SOURCE_SHA256
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target-root", type=Path, required=True)
    parser.add_argument("--seed", required=True)
    parser.add_argument("--legacy-prefix-limit", type=int, default=0)
    parser.add_argument("--root-run-id", default=None)
    parser.add_argument("--decision-limit", type=int, default=300)
    parser.add_argument("--choice-index", type=int, default=1)
    parser.add_argument("--tutorials", choices=("yes", "no"), required=True)
    parser.add_argument("--dotnet-root", type=Path, required=True)
    parser.add_argument("--godot-root", type=Path, required=True,
                        help="Directory directly containing the official Linux Godot executable and GodotSharp")
    parser.add_argument("--executable-working-copy", type=Path, required=True)
    parser.add_argument("--executable-sha256", required=True)
    parser.add_argument("--runtime-feed", type=Path, required=True)
    parser.add_argument("--userland-root", type=Path, required=True)
    parser.add_argument("--userland-manifest", type=Path, required=True)
    parser.add_argument("--userland-manifest-sha256", required=True)
    parser.add_argument("--source-dir", type=Path, default=Path(__file__).absolute().parent)
    parser.add_argument("--bwrap", default="/usr/bin/bwrap")
    parser.add_argument("--game-baseline", type=Path, required=True)
    parser.add_argument("--build-only", action="store_true")
    parser.add_argument("--start", choices=["new", "continue"], default="new")
    args = parser.parse_args()
    AUTH_ROOT = args.target_root.absolute()
    AUTH_CANONICAL = AUTH_ROOT
    GAME_BASELINE = args.game_baseline.absolute()
    GUARD_SOURCE_SHA256 = digest(args.source_dir / "guard-source-manifest.json")
    os.umask(0o077)
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "amd64"):
        parser.error("Execution requires Linux x86_64; help/static review can run elsewhere")
    if args.choice_index < 0:
        parser.error("Explicit zero-based choice index must be nonnegative")
    if args.target_root.absolute() not in (AUTH_ROOT, AUTH_CANONICAL) or AUTH_ROOT.resolve(strict=True) != AUTH_CANONICAL:
        parser.error("Authorized root or verified system /home alias has changed")
    root = args.target_root.resolve(strict=True)
    ordinary(root, True)
    private = root / ".private"
    ordinary(private, True)
    if stat.S_IMODE(private.stat().st_mode) != 0o700:
        parser.error(".private must already be mode 0700")
    # Resolve only the already verified external /home alias. Every private component stays ordinary.
    def normalize(path: Path) -> Path:
        value = path.absolute()
        return AUTH_CANONICAL / value.relative_to(AUTH_ROOT) if value.is_relative_to(AUTH_ROOT) else value
    dotnet = checked_external(args.dotnet_root)
    godot = checked_external(args.godot_root)
    sources = checked_private(normalize(args.source_dir), private)
    executable = checked_private(normalize(args.executable_working_copy), private, directory=False)
    runtime_feed = checked_external(args.runtime_feed)
    executable_info = verify_executable(executable, args.executable_sha256)
    runtime_info = verify_runtime_feed(runtime_feed)
    userland = checked_external(args.userland_root)
    userland_manifest = checked_external(args.userland_manifest, False)
    userland_info = verify_userland(userland, userland_manifest, args.userland_manifest_sha256)
    tools_info = {"dotnet": inspect_tree(dotnet), "godot": inspect_tree(godot)}
    for path in (dotnet / "dotnet", godot / GODOT_BINARY):
        ordinary(path)
        if not os.access(path, os.X_OK):
            raise PermissionError(f"Tool is not executable: {path}")
    ordinary(dotnet / "sdk/9.0.303", True)
    ordinary(dotnet / "shared/Microsoft.NETCore.App/9.0.7", True)
    feed = godot / "GodotSharp/Tools/nupkgs"
    for package in ("Godot.NET.Sdk", "Godot.SourceGenerators", "GodotSharp", "GodotSharpEditor"):
        ordinary(feed / f"{package}.4.5.1.nupkg")
    aux_sources = AUX_SOURCES + (("current_run.save",) if args.start == "continue" else ())
    for name in SOURCES + aux_sources:
        ordinary(sources / name)
    if digest(sources / "prepare_release_elf.py") != ELF_PREPARER_SHA256 or digest(sources / "hq_elf.py") != ELF_READER_SHA256:
        raise ValueError("Fixed ELF preparation source or comparator changed")
    guard_provenance = verify_guard_provenance(sources)
    evidence_parent = private / "evidence"
    if not evidence_parent.exists():
        mkdir(evidence_parent)
    checked_private(evidence_parent, private)
    process_id = "vm-continuous-" + time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:12]
    evidence = evidence_parent / process_id
    mkdir(evidence)
    report: dict[str, object] = {"schema": "e004b-normal-boot-supervisor-v1", "processRunId": process_id,
        "startedUtc": utc(), "passed": False, "nativeGamePhaseAttempted": False,
        "gameManagedAssemblyObserved": False, "normalBootAccepted": False,
        "acceptanceScope": "Bounded observation and artifact collection only; HQ full progress and raw-log validation still required",
        "authorizedJob": AUTH_JOB,
        "targetRoot": str(root), "originalHomeValue": os.environ.get("HOME"), "toolTrees": tools_info,
        "evidenceRoot": str(evidence), "phases": [], "quota": "not_measured_by_this_control",
        "releaseInput": {"executable": executable_info, "runtimePackage": runtime_info,
                         "userland": userland_summary(userland_info)}, "guardInput": guard_provenance}
    print(json.dumps({"evidenceRoot": str(evidence), "processRunId": process_id}), flush=True)
    identity = None
    game_root = None
    inputs_preparer = None
    game_contract = None
    release_config = None
    runtime = None
    try:
        if STOP_SIGNAL is not None:
            raise InterruptedError("Supervisor stop requested before start")
        identity = create_identity(evidence / "identity")
        userland_manifest_copy = copy_file(userland_manifest, evidence / "userland-input-manifest.json")
        os.chmod(evidence / "userland-input-manifest.json", 0o400)
        write_json(evidence / "userland-manifest-copy-record.json", userland_manifest_copy)
        preflight_env = {key: value for key, value in os.environ.items() if key in ("HOME", "USER", "LOGNAME")}
        preflight_env["PATH"] = "/usr/bin:/bin"
        for name, argv in (("version", [args.bwrap, "--version"]), ("help", [args.bwrap, "--help"])):
            result = subprocess.run(argv, env=preflight_env, stdin=subprocess.DEVNULL, capture_output=True, timeout=5, check=False)
            write_json(evidence / ("bwrap-" + name + ".json"), {"argv": argv, "environment": preflight_env,
                "returnCode": result.returncode, "stdout": result.stdout.decode(errors="replace"),
                "stderr": result.stderr.decode(errors="replace")})
            if result.returncode:
                raise RuntimeError("bwrap metadata command failed")
            if name == "help":
                for flag in ("--remount-ro", "--unshare-user", "--unshare-net", "--unshare-pid", "--unshare-uts",
                             "--unshare-ipc", "--new-session", "--die-with-parent", "--cap-drop", "--symlink"):
                    if flag.encode() not in result.stdout:
                        raise RuntimeError(f"Installed bwrap lacks required flag: {flag}")
        libc = ctypes.CDLL(None, use_errno=True)
        libc.prctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong]
        libc.prctl.restype = ctypes.c_int
        ctypes.set_errno(0)
        subreaper = libc.prctl(36, 1, 0, 0, 0)
        report["childSubreaper"] = {"returnValue": subreaper, "errno": ctypes.get_errno()}
        if subreaper != 0:
            raise RuntimeError("Cannot establish local child subreaper for owned-process cleanup")
        immutable = evidence / "input-source"
        mkdir(immutable)
        source_records = [copy_file(sources / name, immutable / name) for name in SOURCES + aux_sources]
        supervisor_path = Path(__file__).absolute()
        source_records.append(copy_file(supervisor_path, immutable / "run-controls.py"))
        for path in immutable.iterdir():
            os.chmod(path, 0o400)
        write_json(evidence / "input-manifest.json", source_records)
        readonly = evidence / "readonly"
        mkdir(readonly)
        runtime_package_records = []
        for package_name, expected_package in RESTORE_PACKAGES.items():
            runtime_package_record = copy_file(runtime_feed / package_name, readonly / package_name)
            if (runtime_package_record["sha256"] != expected_package["sha256"]
                    or runtime_package_record["bytes"] != expected_package["bytes"]):
                raise ValueError(f"Runtime package changed before its readonly copy: {package_name}")
            os.chmod(readonly / package_name, 0o400)
            runtime_package_records.append({"packageName": package_name, **runtime_package_record})
        write_json(evidence / "runtime-package-copy-manifest.json", runtime_package_records)
        sentinel = readonly / "sentinel"
        sentinel.write_text("E003 ordinary read-only bind sentinel\n", encoding="utf-8")
        sentinel_hash, sentinel_mode = digest(sentinel), stat.S_IMODE(sentinel.stat().st_mode)
        hidden = evidence / "host-only-sentinel"
        hidden.write_text("This host path must never be visible in the namespace.\n", encoding="utf-8")
        build, runtime = evidence / "build-work", evidence / "runtime-work"
        make_work(build)
        make_work(runtime)
        for name in SOURCES:
            copy_file(immutable / name, build / "project" / name)
        (build / "project/NuGet.Config").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n'
            '    <clear />\n    <add key="official-local-godot-4.5.1" value="/godot/GodotSharp/Tools/nupkgs" />\n'
            '    <add key="hq-fixed-runtime-9.0.7" value="/readonly" />\n'
            '  </packageSources>\n</configuration>\n', encoding="utf-8")
        phases = report["phases"]
        for name, command in (
            ("restore", ["/dotnet/dotnet", "restore", "HostControls.csproj", "--configfile", "/work/project/NuGet.Config",
                         "--disable-parallel", "--disable-build-servers", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false",
                         "-p:Configuration=ExportRelease", "-r", "linux-x64", "-p:SelfContained=true",
                         "-p:RuntimeFrameworkVersion=9.0.7", "-p:PublishTrimmed=false"]),
            ("publish", ["/dotnet/dotnet", "publish", "HostControls.csproj", "-c", "ExportRelease", "--no-restore",
                       "-r", "linux-x64", "--self-contained", "true", "-o", "/work/publish",
                       "-p:GodotTargetPlatform=linuxbsd", "-p:RuntimeFrameworkVersion=9.0.7", "-p:PublishTrimmed=false",
                       "--disable-build-servers", "-m:1", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false"])):
            env = clean_environment(process_id + "-" + name + "-" + uuid.uuid4().hex[:8])
            phase = run_stage(evidence, name, bwrap_command(args.bwrap, dotnet, godot, build, readonly, evidence / "identity", command, userland=userland),
                              env, 120, 100, 3 * 1024**3, work=build)
            phases.append(phase)
            if not phase["passed"]:
                raise RuntimeError(f"{name} failed; subsequent stages were not launched")
        if args.build_only:
            report["passed"] = True
            report["acceptanceScope"] = "Offline managed build only; no game execution"
            raise BuildComplete()
        # This reviewed Python stage reads the fixed original and writes a fresh ELF; it executes no ELF.
        elf_evidence = build / "project/elf-preparation"
        mkdir(elf_evidence)
        derived_executable = build / "project/SlayTheSpire2-early-guard"
        command = [sys.executable, "-I", "-S", "-B", str(immutable / "prepare_release_elf.py"),
                   "--source", str(executable), "--destination", str(derived_executable),
                   "--hq-elf", str(immutable / "hq_elf.py"), "--evidence-dir", str(elf_evidence)]
        preparation = run_stage(evidence, "prepare-release-elf", command,
            clean_environment(process_id + "-elf-" + uuid.uuid4().hex[:8]), 40, 30, 1024**3, work=build)
        phases.append(preparation)
        if not preparation["passed"]:
            raise RuntimeError("Static release ELF preparation failed; native stage was not launched")
        elf_manifest = json.loads((elf_evidence / "manifest.json").read_text())
        if (elf_manifest.get("passed") is not True or elf_manifest.get("parentSha256") != EXE_SHA256
                or elf_manifest.get("childSha256") != DERIVED_EXE_SHA256
                or elf_manifest.get("childBytes") != DERIVED_EXE_BYTES
                or digest(derived_executable) != DERIVED_EXE_SHA256
                or ordinary(derived_executable).st_size != DERIVED_EXE_BYTES):
            raise ValueError("Linux ordinary derivation differs from the approved static result")
        report["elfPreparation"] = {"path": str(elf_evidence / "manifest.json"),
                                    "sha256": digest(elf_evidence / "manifest.json"), "manifest": elf_manifest}
        release_config = prepare_native_host(immutable, build, runtime, derived_executable, runtime_info, evidence, DERIVED_EXE_SHA256)
        native_id = process_id + "-godot-" + uuid.uuid4().hex[:8]
        invisible = sorted({str(AUTH_ROOT), str(private), str(sources), str(hidden),
                            identity["homeDirectory"], "/home", "/root", "/run/user",
                            str(GAME_BASELINE), str(private / "migration-inputs")})
        assert len(invisible) == 10
        write_json(runtime / "action-config.json", {"choiceIndex": args.choice_index, "tutorials": args.tutorials, "seed": args.seed, "decisionLimit": args.decision_limit, "legacyPrefixLimit": args.legacy_prefix_limit,
            "rootRunId": args.root_run_id or process_id, "slAttempt": 0, "processRunId": native_id, "recovery": None, "start": args.start})
        config = {"processRunId": native_id, "expectedHome": os.environ.get("HOME"), "invisiblePaths": invisible,
                  "sentinelSha256": sentinel_hash, "identity": identity,
                  "hostNamespaceIds": {name: os.readlink("/proc/self/ns/" + name) for name in NAMESPACES}}
        write_json(runtime / "control-config.json", config)
        release_config["processRunId"] = native_id
        release_config["userland"] = userland_runtime_config(userland_info)
        write_json(runtime / "release-host-config.json", release_config)
        guard, guard_info = prepare_guard_bind(evidence, runtime, native_id, guard_provenance)
        inputs_preparer = load_owned_module(immutable / "normal_boot_inputs.py", "e004b_normal_boot_inputs")
        game_contract = json.loads((immutable / "game-input-contract.json").read_text())
        game_root, normal_inputs = inputs_preparer.prepare(immutable, evidence, runtime, GAME_BASELINE, private,
            ordinary=ordinary, checked_private=checked_private, copy_file=copy_file, write_json=write_json)
        report["normalGameInputCopy"] = {"path": str(evidence / "normal-game-input-copy-manifest.json"),
            "sha256": digest(evidence / "normal-game-input-copy-manifest.json"), "gameRoot": str(game_root)}
        command = ["/work/native-host/SlayTheSpire2", "--headless", "--audio-driver", "Dummy", "--path", "/work/project",
                   "--main-pack", "/game/SlayTheSpire2.pck", "--force-steam=off",
                   "--", "--e003-linux-host-controls", "--config=/work/control-config.json"]
        normal_config = inputs_preparer.build_config(immutable, normal_inputs, native_id, command)
        write_json(runtime / "normal-boot-config.json", normal_config)
        environment = clean_environment(native_id)
        environment["LD_LIBRARY_PATH"] = "/work/native-host:/game/data_sts2_linuxbsd_x86_64"
        native_command = bwrap_command(args.bwrap, dotnet, godot, runtime, readonly, evidence / "identity", command,
            userland=userland, guard=guard, game=game_root, observation_stage=evidence / "godot-normal-boot")
        report["nativeGamePhaseAttempted"] = True
        phase = run_stage(evidence, "godot-normal-boot", native_command, environment, 1200, 1000, 4 * 1024**3, work=runtime)
        phases.append(phase)
        action_result = json.loads((runtime / "normal-boot-result.json").read_text())
        report["normalBoot"] = {"passed": action_result.get("outcome") in ("continuous_game_over", "continuous_budget_truncated")
            and action_result.get("probeCompleted") is True, "gameManagedAssemblyObserved": True,
            "resultPath": str(runtime / "normal-boot-result.json")}
        report["acceptanceScope"] = "VM bounded original-menu Silent A0 first real decision/action/successor; no fidelity or training claim"
        report["gameManagedAssemblyObserved"] = report["normalBoot"].get("gameManagedAssemblyObserved", False)
        native_report = json.loads((runtime / "control-report.json").read_text())
        expected_checks = {"fsize_lowered_and_verified", "dynamic_jit_after_limit_and_guard", "doublemapper_wx_after_jit",
            "socket_diagnostics_recorded",
            "guard_install", "existing_thread_tsync", "new_managed_thread", "all_observed_threads_guarded",
            "no_socket_file_descriptors", "headless_dummy_userdata", "home_value_preserved", "new_namespaces",
            "nss_current_user_identity", "nss_identity_files_readonly",
            "sparse_writable_mounts", "no_effective_capabilities", "private_write", "readonly_sentinel_read",
            "readonly_create_denied", "readonly_unlink_denied", "readonly_chmod_denied", "managed_readonly_write_denied",
            "readonly_sentinel_unchanged", "process_start_denied", "fork_denied", "clone_process_denied",
            "clone3_returns_enosys", "x32_abi_denied", "old_x32_alias_denied", "execve_denied", "execveat_denied"}
        expected_checks |= {"host_path_hidden:" + path for path in invisible}
        expected_checks |= {"raw_socket_denied:" + name for name in ("ipv4", "ipv6", "unix")}
        expected_checks |= {"managed_socket_denied:" + name for name in ("InterNetwork", "InterNetworkV6", "Unix")}
        checks = native_report.get("checks", [])
        observed_checks = [check.get("name") for check in checks]
        sentinel_unchanged = (sentinel.exists() and digest(sentinel) == sentinel_hash
                              and stat.S_IMODE(sentinel.stat().st_mode) == sentinel_mode
                              and sorted(path.name for path in readonly.iterdir()) == sorted(["sentinel", *RESTORE_PACKAGES])
                              and all(stat.S_IMODE(ordinary(readonly / name).st_mode) == 0o400
                                      and (readonly / name).stat().st_size == expected["bytes"]
                                      and digest(readonly / name) == expected["sha256"]
                                      for name, expected in RESTORE_PACKAGES.items()))
        report["nativeReport"] = {"path": str(runtime / "control-report.json"), "sha256": digest(runtime / "control-report.json"),
                                  "missingChecks": sorted(expected_checks - set(observed_checks)),
                                  "duplicateChecks": len(observed_checks) != len(set(observed_checks)),
                                  "sentinelHostVerificationPassed": sentinel_unchanged}
        socket_diagnostic = runtime / "socket-diagnostics-before-guard.json"
        report["socketDiagnostic"] = {"path": str(socket_diagnostic), "present": socket_diagnostic.is_file(),
            "sha256": digest(socket_diagnostic) if socket_diagnostic.is_file() else None}
        report["legacyControlReportScope"] = ("The unchanged gameCodeLoaded=false field in control-report.json describes the original 47-control managed boundary only; original PCK/native extensions can load before that boundary")
        report["passed"] = bool(report["normalBoot"]["passed"] and phase["passed"] and native_report.get("passed") is True
            and native_report.get("schema") == "e003-linux-host-controls-v1"
            and native_report.get("processRunId") == native_id and native_report.get("gameCodeLoaded") is False
            and native_report.get("exitCode") == 0 and native_report.get("error") is None
            and expected_checks == set(observed_checks) and len(observed_checks) == len(set(observed_checks)) == 47
            and all(check.get("passed") is True for check in checks) and sentinel_unchanged)
        if not report["passed"]:
            raise RuntimeError("Normal game boot or original host-control acceptance failed")
    except BuildComplete:
        pass
    except BaseException as error:
        report["error"] = repr(error)
        report["traceback"] = traceback.format_exc()
    finally:
        if release_config is not None and runtime is not None:
            try:
                report["normalHostPayloadAfter"] = verify_normal_host_payload(runtime, release_config)
            except Exception as payload_error:
                report["normalHostPayloadAfter"] = {"passed": False, "error": repr(payload_error)}
                report["passed"] = False
        if game_root is not None and inputs_preparer is not None and game_contract is not None:
            try:
                report["normalGameImmutableAfter"] = inputs_preparer.verify_immutable_inputs(
                    GAME_BASELINE, game_root, game_contract, ordinary=ordinary, runtime=runtime, summary=normal_inputs)
            except Exception as game_error:
                report["normalGameImmutableAfter"] = {"passed": False, "error": repr(game_error)}
                report["passed"] = False
        try:
            verify_executable(executable, EXE_SHA256)
            verify_guard_provenance(sources)
            report["parentAndNativeGuardUnchanged"] = True
        except Exception as input_error:
            report["parentAndNativeGuardUnchanged"] = False
            report["passed"] = False
            report["inputVerificationError"] = repr(input_error)
        try:
            final_userland = verify_userland(userland, userland_manifest, args.userland_manifest_sha256)
            report["userlandHostVerification"] = {"passed": True, **userland_summary(final_userland)}
        except Exception as userland_error:
            report["userlandHostVerification"] = {"passed": False, "error": repr(userland_error)}
            report["passed"] = False
            if "error" not in report:
                report["error"] = "Private userland changed or cannot be verified"
        if identity is not None:
            report["identityHostVerification"] = verify_identity(evidence / "identity", identity)
            if not report["identityHostVerification"]["passed"]:
                report["passed"] = False
                if "error" not in report:
                    report["error"] = "Projected identity files changed or cannot be verified"
        report["finishedUtc"] = utc()
        write_json(evidence / "supervisor-report.json", report)
    print(json.dumps({"passed": report["passed"], "evidenceRoot": str(evidence), "error": report.get("error")}), flush=True)
    return 0 if report["passed"] else 51


def stop(number: int, _frame: object) -> None:
    global STOP_SIGNAL
    STOP_SIGNAL = number


if __name__ == "__main__":
    for handled in (signal.SIGINT, signal.SIGTERM):
        signal.signal(handled, stop)
    raise SystemExit(main())
