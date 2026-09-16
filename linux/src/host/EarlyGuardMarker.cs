using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Native = E003.Linux.LinuxSeccompGuard;

internal static class EarlyGuardMarker
{
    private const string GuardPath = "/guard/libE004EarlyGuard.so";
    private const string ExecutablePath = "/work/native-host/SlayTheSpire2";
    private const string ParentExecutableHash = "0b0ae3859c4c26d352b6483dc1cb11601a07bbc9890d97bcebae0d5d0bdd99ff";
    private const string FilterHash = "0a0656c1ebc6e7f0051f19d358db2139b9ad8c659ebf5ded3bce9424fe8b65d8";
    private const ulong BootstrapFsize = 2199023255552;

    internal sealed record Proof(string MarkerSha256, int MarkerBytes, long ConstructorPid, long ConstructorTid,
        long CallbackTid, bool CallbackTidMatchesConstructor, Native.SyscallResult CurrentNoNewPrivileges,
        Native.SyscallResult CurrentSeccomp, Native.SyscallResult EarlyUnixSocketProbe,
        string FilterSha256, string GuardLibrarySha256, string DerivedExecutableSha256,
        string LineageManifestSha256, string GuardSourceManifestSha256, string[] GuardMappings,
        string ReadonlyGuardMount, string ReadonlyLineageMount, RuntimeElfProof RuntimeElf, string Scope);

    internal sealed record GuardProof(string Path, string LibrarySha256, string[] Mappings, string ReadonlyMount);
    internal sealed record RuntimeElfProof(Dictionary<string, ulong> AuxiliaryVector, string AddedReadonlyMapping);

    private static void Require(bool value, string message) => ManagedBootstrap.Require(value, message);
    private static bool HashSpelling(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()
        ?? throw new InvalidDataException("Null configuration field: " + key);

    private static void ExactFields(JsonElement value, params string[] keys)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Configuration root must be an object");
        string[] observed = value.EnumerateObject().Select(item => item.Name).ToArray();
        Require(observed.Length == keys.Length && observed.Distinct(StringComparer.Ordinal).Count() == keys.Length
            && new HashSet<string>(keys, StringComparer.Ordinal).SetEquals(observed), "Unknown, duplicate or missing configuration fields");
    }

    private static long PositiveInteger(string text)
    {
        Require(text.Length is > 0 and <= 19 && text[0] is >= '1' and <= '9'
            && text.All(c => c is >= '0' and <= '9'), "Noncanonical positive integer in marker");
        Require(long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0,
            "Marker integer exceeds positive Int64");
        return value;
    }

    internal static Proof Verify(string runId)
    {
        Require(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            && BitConverter.IsLittleEndian, "Early marker ABI requires Linux little-endian x86_64");
        RawFiles.Result marker = RawFiles.Inspect("/work", "e004b-early-guard.marker", 4095, 0x180, false, true, true);
        byte[] markerBytes = marker.Bytes!;
        Require(markerBytes.Length > 0 && markerBytes.All(item => item is >= 0x20 and <= 0x7e or 0x0a),
            "Marker must be printable ASCII and LF only, without BOM, CR or NUL");
        string[] rows = Encoding.ASCII.GetString(markerBytes).Split('\n');
        string[] keys = ["schema", "PROCESS_RUN_ID", "pid", "tid", "nnp_raw", "nnp_errno", "tsync_raw", "tsync_errno",
            "nnp_get_raw", "nnp_get_errno", "seccomp_get_raw", "seccomp_get_errno", "fsize_soft", "fsize_hard",
            "filter_instruction_count", "filter_bytes", "filter_hex", "end"];
        Require(rows.Length == keys.Length + 1 && rows[^1] == "", "Marker must have exactly 18 LF-terminated rows");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Length; i++)
        {
            string prefix = keys[i] + "=";
            Require(rows[i].StartsWith(prefix, StringComparison.Ordinal), "Marker field order or key failed at " + keys[i]);
            string value = rows[i][prefix.Length..];
            Require(value.Length > 0 && !value.Contains('='), "Empty or malformed marker value at " + keys[i]);
            fields.Add(keys[i], value);
        }
        var literals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema"] = "e004b-early-guard-v1", ["end"] = "e004b-early-guard-v1", ["PROCESS_RUN_ID"] = runId,
            ["nnp_raw"] = "0", ["nnp_errno"] = "0", ["tsync_raw"] = "0", ["tsync_errno"] = "0",
            ["nnp_get_raw"] = "1", ["nnp_get_errno"] = "0", ["seccomp_get_raw"] = "2", ["seccomp_get_errno"] = "0",
            ["fsize_soft"] = "2199023255552", ["fsize_hard"] = "2199023255552",
            ["filter_instruction_count"] = "93", ["filter_bytes"] = "744"
        };
        foreach (var item in literals) Require(fields[item.Key] == item.Value, "Marker success value failed: " + item.Key);
        long pid = PositiveInteger(fields["pid"]), tid = PositiveInteger(fields["tid"]);
        Native.SyscallResult currentPid = Native.Call(39), currentTid = Native.Call(186);
        Require(currentPid.ReturnValue == Environment.ProcessId && currentPid.ReturnValue == pid && currentTid.ReturnValue > 0,
            "Marker PID differs from this process: " + currentPid + "; tid=" + currentTid);
        string encodedFilter = fields["filter_hex"];
        Require(encodedFilter.Length == 1488 && encodedFilter.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Marker filter must be exactly 1488 lowercase hex characters");
        byte[] observedFilter = Convert.FromHexString(encodedFilter);
        Native.Instruction[] expectedFilter = Native.BuildFilter();
        byte[] expectedBytes = Native.FilterBytes(expectedFilter);
        Require(observedFilter.Length == 744 && expectedFilter.Length == 93 && expectedBytes.Length == 744
            && ManagedBootstrap.Sha256(observedFilter) == FilterHash && expectedBytes.AsSpan().SequenceEqual(observedFilter),
            "Marker actual-memory filter differs from the independent unchanged 93-instruction C# filter");

        RawFiles.Result configFile = RawFiles.Inspect("/work", "managed-bootstrap-config.json", 65536, 0x180, false, true, true);
        using var configDocument = JsonDocument.Parse(configFile.Bytes!);
        JsonElement config = configDocument.RootElement;
        ExactFields(config, "schema", "processRunId", "parentExecutableSha256", "derivedExecutableSha256",
            "guardLibraryPath", "guardLibrarySha256", "lineageManifestPath", "lineageManifestSha256");
        string derivedHash = Text(config, "derivedExecutableSha256"), guardHash = Text(config, "guardLibrarySha256");
        string lineageHash = Text(config, "lineageManifestSha256");
        Require(Text(config, "schema") == "e004b-managed-bootstrap-config-v2" && Text(config, "processRunId") == runId
            && Text(config, "parentExecutableSha256") == ParentExecutableHash && Text(config, "guardLibraryPath") == GuardPath
            && Text(config, "lineageManifestPath") == "/guard/e004b-bootstrap-lineage.json"
            && HashSpelling(derivedHash) && HashSpelling(guardHash) && HashSpelling(lineageHash)
            && derivedHash != ParentExecutableHash, "Bootstrap launch identities are missing, unpinned or inconsistent");
        RawFiles.Result lineageFile = RawFiles.Inspect("/guard", "e004b-bootstrap-lineage.json", 65536, 0x100, true, true, true);
        string readonlyLineageMount = ReadonlyMountFor("/guard/e004b-bootstrap-lineage.json");
        Require(lineageFile.Sha256 == lineageHash, "Trusted outer lineage manifest hash mismatch");
        using var lineageDocument = JsonDocument.Parse(lineageFile.Bytes!);
        JsonElement lineage = lineageDocument.RootElement;
        ExactFields(lineage, "schema", "processRunId", "parentExecutableSha256", "derivedExecutableSha256",
            "guardLibrarySha256", "guardSourceManifestSha256", "filterSha256", "operation");
        string guardSourceHash = Text(lineage, "guardSourceManifestSha256");
        Require(Text(lineage, "schema") == "e004b-bootstrap-lineage-v1" && Text(lineage, "processRunId") == runId
            && Text(lineage, "parentExecutableSha256") == ParentExecutableHash
            && Text(lineage, "derivedExecutableSha256") == derivedHash && Text(lineage, "guardLibrarySha256") == guardHash
            && Text(lineage, "filterSha256") == FilterHash && HashSpelling(guardSourceHash)
            && Text(lineage, "operation") == "ordinary-copy-plus-one-DT_NEEDED", "Outer source/copy lineage does not match this launch");

        GuardProof helper = InspectGuard(guardHash);
        Require(new FileInfo("/proc/self/exe").LinkTarget == ExecutablePath,
            "Current /proc/self/exe is not the declared derived no-game host path");
        RawFiles.Result executable = RawFiles.Inspect("/work/native-host", "SlayTheSpire2", 536870912, null, true, false, false);
        Require(executable.Sha256 == derivedHash, "Current executable differs from the independently derived ELF hash");
        RuntimeElfProof runtimeElf = InspectRuntimeElf(executable);

        Native.SyscallResult nnp = Native.Call(157, 39), seccomp = Native.Call(157, 21);
        Require(nnp.ReturnValue == 1 && seccomp.ReturnValue == 2, "Early NNP/seccomp query failed: " + nnp + "; " + seccomp);
        IntPtr limits = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(limits, 0); Marshal.WriteInt64(limits, 8, 0);
            Native.SyscallResult limitRead = Native.Call(302, 0, 1, 0, Native.Pointer(limits));
            ulong soft = unchecked((ulong)Marshal.ReadInt64(limits)), hard = unchecked((ulong)Marshal.ReadInt64(limits, 8));
            Require(limitRead.ReturnValue == 0 && soft == BootstrapFsize && hard == BootstrapFsize,
                "Early guard changed startup FSIZE or it drifted before the original reduction: " + limitRead + "; soft=" + soft + "; hard=" + hard);
        }
        finally { Marshal.FreeHGlobal(limits); }

        // Direct native AF_UNIX probe, before any LinuxSeccompGuard.Install call in this process.
        Native.SyscallResult socket = Native.Call(41, 1, 1 | 0x80000, 0);
        Native.SyscallResult? unexpectedClose = socket.ReturnValue >= 0 ? Native.Call(3, (ulong)socket.ReturnValue) : null;
        Require(socket.ReturnValue == -1 && socket.Errno == 1,
            "Early raw AF_UNIX socket must fail with EPERM before managed re-install: " + socket + "; unexpectedClose=" + unexpectedClose);
        return new(marker.Sha256, markerBytes.Length, pid, tid, currentTid.ReturnValue, currentTid.ReturnValue == tid,
            nnp, seccomp, socket, FilterHash, helper.LibrarySha256, executable.Sha256, lineageFile.Sha256, guardSourceHash,
            helper.Mappings, helper.ReadonlyMount, readonlyLineageMount, runtimeElf,
            "Marker bytes describe the helper's actual filter memory, not a kernel filter dump. Outer HQ must independently validate the native constructor and ordinary-copy/DT_NEEDED lineage before launch.");
    }

    internal static GuardProof InspectGuardAtReady(Proof early)
    {
        RawFiles.Result lineage = RawFiles.Inspect("/guard", "e004b-bootstrap-lineage.json", 65536, 0x100, true, true, true);
        ReadonlyMountFor("/guard/e004b-bootstrap-lineage.json");
        Require(lineage.Sha256 == early.LineageManifestSha256, "Lineage bytes changed between early callback and Ready");
        return InspectGuard(early.GuardLibrarySha256);
    }

    private static RuntimeElfProof InspectRuntimeElf(RawFiles.Result executable)
    {
        byte[] bytes = new byte[4097];
        int length = 0;
        using (var stream = File.OpenRead("/proc/self/auxv"))
        {
            while (length < bytes.Length)
            {
                int read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
        }
        Require(length is >= 16 and <= 4096 && length % 16 == 0,
            "Unexpected Linux x86_64 auxiliary-vector shape");
        var values = new Dictionary<ulong, ulong>();
        bool terminated = false;
        for (int i = 0; i < length; i += 16)
        {
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i, 8));
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i + 8, 8));
            if (key == 0)
            {
                Require(value == 0 && i + 16 == length, "Malformed auxiliary-vector terminator");
                terminated = true;
                break;
            }
            Require(values.TryAdd(key, value), "Duplicate auxiliary-vector key");
        }
        Require(terminated && values.GetValueOrDefault(3UL) == 0x400040
            && values.GetValueOrDefault(4UL) == 56 && values.GetValueOrDefault(5UL) == 13
            && values.GetValueOrDefault(6UL) == 4096, "Actual PHDR/PHENT/PHNUM/PAGESZ differs from the fixed ELF derivation");
        string[] added = File.ReadAllLines("/proc/self/maps").Where(line =>
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6 || fields[5] != ExecutablePath) return false;
            string[] interval = fields[0].Split('-');
            return interval.Length == 2 && ulong.Parse(interval[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == 0x48f5000
                && ulong.Parse(interval[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == 0x48f7000;
        }).ToArray();
        Require(added.Length == 1, "Exactly one mapping must cover the two new ELF pages");
        string[] columns = added[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] device = columns[3].Split(':');
        Require(columns[1] == "r--p" && ulong.Parse(columns[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == 0x4318000
            && ulong.Parse(columns[4], CultureInfo.InvariantCulture) == executable.Stat.Inode && device.Length == 2
            && ulong.Parse(device[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == executable.Stat.DeviceMajor
            && ulong.Parse(device[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == executable.Stat.DeviceMinor,
            "New ELF pages are not the expected read-only non-executable mapping of this file");
        return new(new() { ["AT_PHDR"] = values[3], ["AT_PHENT"] = values[4],
            ["AT_PHNUM"] = values[5], ["AT_PAGESZ"] = values[6] }, added[0]);
    }

    private static GuardProof InspectGuard(string guardHash)
    {
        RawFiles.Result helper = RawFiles.Inspect("/guard", "libE004EarlyGuard.so", 8388608, null, true, false, false);
        Require(helper.Sha256 == guardHash, "Actually mounted guard library hash mismatch");
        string readonlyMount = ReadonlyMountFor(GuardPath);
        string[] mappings = File.ReadAllLines("/proc/self/maps").Where(line =>
        {
            string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return columns.Length >= 6 && columns[5] == GuardPath;
        }).ToArray();
        Require(mappings.Length > 0 && mappings.All(line =>
        {
            string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] device = columns[3].Split(':');
            bool deviceMatches = device.Length == 2 && ulong.Parse(device[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == helper.Stat.DeviceMajor
                && ulong.Parse(device[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) == helper.Stat.DeviceMinor;
            return columns.Length == 6 && deviceMatches
                && ulong.Parse(columns[4], CultureInfo.InvariantCulture) == helper.Stat.Inode
                && !(columns[1].Contains('w') && columns[1].Contains('x'));
        }) && mappings.Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Contains('x')),
            "Guard is absent, deleted, from a different inode/device, or mapped writable and executable");
        return new(GuardPath, helper.Sha256, mappings, readonlyMount);
    }

    private static string ReadonlyMountFor(string targetPath)
    {
        var enclosing = File.ReadAllLines("/proc/self/mountinfo").Select(line => new
        { line, fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries) })
            .Where(item => item.fields.Length > 6 && (targetPath == item.fields[4]
                || targetPath.StartsWith(item.fields[4].TrimEnd('/') + "/", StringComparison.Ordinal)))
            .OrderByDescending(item => item.fields[4].Length).ToArray();
        Require(enclosing.Length > 0 && enclosing[0].fields[5].Split(',').Contains("ro"),
            "The actual guard evidence path is not on a read-only mount: " + targetPath);
        return enclosing[0].line;
    }
}

// Linux x86_64 descriptor reads. O_NONBLOCK is set before fstat, so a hostile FIFO cannot block open.
// The fixed directory and leaf are opened separately with O_NOFOLLOW; only a regular, singly linked
// file is read. fstat identity/size/timestamps are checked both before and after EOF.
internal static class RawFiles
{
    private const ulong ReadFlags = 0x20000 | 0x80000 | 0x800; // NOFOLLOW | CLOEXEC | NONBLOCK; RDONLY=0
    internal sealed record Stat(ulong Device, ulong Inode, ulong Links, uint Mode, uint UserId, long Size,
        long MtimeSeconds, long MtimeNanoseconds, long CtimeSeconds, long CtimeNanoseconds)
    {
        internal ulong DeviceMajor => ((Device >> 8) & 0xfff) | ((Device >> 32) & ~0xfffUL);
        internal ulong DeviceMinor => (Device & 0xff) | ((Device >> 12) & ~0xffUL);
    }
    internal sealed record Result(Stat Stat, string Sha256, byte[]? Bytes);
    private static void Require(bool value, string message) => ManagedBootstrap.Require(value, message);
    private static void Close(long fd)
    {
        Native.SyscallResult result = Native.Call(3, checked((ulong)fd));
        Require(result.ReturnValue == 0, "Native close failed for descriptor " + fd + ": " + result);
    }
    private static Stat GetStat(long fd)
    {
        IntPtr buffer = Marshal.AllocHGlobal(144);
        try
        {
            Native.SyscallResult result = Native.Call(5, checked((ulong)fd), Native.Pointer(buffer));
            Require(result.ReturnValue == 0, "Native fstat failed: " + result);
            return new(unchecked((ulong)Marshal.ReadInt64(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 16)), unchecked((uint)Marshal.ReadInt32(buffer, 24)),
                unchecked((uint)Marshal.ReadInt32(buffer, 28)), Marshal.ReadInt64(buffer, 48),
                Marshal.ReadInt64(buffer, 88), Marshal.ReadInt64(buffer, 96), Marshal.ReadInt64(buffer, 104), Marshal.ReadInt64(buffer, 112));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static Result Inspect(string directory, string leaf, long maximumBytes, uint? exactMode,
        bool requireNoWriteBits, bool requireCurrentOwner, bool retainBytes)
    {
        Require(leaf.Length > 0 && !leaf.Contains('/') && leaf is not "." and not "..", "Invalid fixed evidence leaf");
        IntPtr directoryPointer = Marshal.StringToCoTaskMemUTF8(directory), leafPointer = Marshal.StringToCoTaskMemUTF8(leaf);
        long directoryFd = -1, fd = -1;
        try
        {
            Native.SyscallResult openedDirectory = Native.Call(257, unchecked((ulong)-100L), Native.Pointer(directoryPointer), ReadFlags | 0x10000);
            Require(openedDirectory.ReturnValue >= 0, "No-follow evidence directory open failed: " + directory + ": " + openedDirectory);
            directoryFd = openedDirectory.ReturnValue;
            Native.SyscallResult openedFile = Native.Call(257, (ulong)directoryFd, Native.Pointer(leafPointer), ReadFlags);
            Require(openedFile.ReturnValue >= 0, "No-follow nonblocking evidence open failed: " + leaf + ": " + openedFile);
            fd = openedFile.ReturnValue;
            Stat before = GetStat(fd);
            Require((before.Mode & 0xf000) == 0x8000 && before.Links == 1 && before.Size is > 0
                && before.Size <= maximumBytes, "Evidence is not a bounded, nonempty, singly linked regular file: " + leaf);
            Require(exactMode is null || (before.Mode & 0xfff) == exactMode.Value, "Wrong evidence file mode: " + leaf);
            Require(!requireNoWriteBits || (before.Mode & 0x92) == 0, "Evidence file is writable: " + leaf); // 0222
            if (requireCurrentOwner)
            {
                Native.SyscallResult euid = Native.Call(107);
                Require(euid.ReturnValue >= 0 && euid.ReturnValue == before.UserId, "Evidence file owner differs from current euid: " + leaf);
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var captured = retainBytes ? new MemoryStream(checked((int)before.Size)) : null;
            const int Capacity = 65536;
            IntPtr buffer = Marshal.AllocHGlobal(Capacity);
            long total = 0; int interruptedReads = 0;
            try
            {
                byte[] bytes = new byte[Capacity];
                while (true)
                {
                    Native.SyscallResult read = Native.Call(0, (ulong)fd, Native.Pointer(buffer), Capacity);
                    if (read.ReturnValue == -1 && read.Errno == 4 && ++interruptedReads <= 8) continue;
                    Require(read.ReturnValue >= 0 && read.ReturnValue <= Capacity, "Native evidence read failed: " + leaf + ": " + read);
                    if (read.ReturnValue == 0) break;
                    total = checked(total + read.ReturnValue);
                    Require(total <= maximumBytes && total <= before.Size, "Evidence grew beyond its stated bound: " + leaf);
                    int count = checked((int)read.ReturnValue);
                    Marshal.Copy(buffer, bytes, 0, count); hash.AppendData(bytes, 0, count); captured?.Write(bytes, 0, count);
                }
                Require(total == before.Size && before == GetStat(fd), "Evidence identity/size/timestamps changed during its descriptor read: " + leaf);
                string digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                return new(before, digest, captured?.ToArray());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            try { if (fd >= 0) Close(fd); }
            finally
            {
                try { if (directoryFd >= 0) Close(directoryFd); }
                finally { Marshal.FreeCoTaskMem(directoryPointer); Marshal.FreeCoTaskMem(leafPointer); }
            }
        }
    }
}
