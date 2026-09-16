using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using E003.Linux;
using Godot;
using Native = E003.Linux.LinuxSeccompGuard;

internal sealed class HostControlSuite
{
    private const string Work = "/work";
    private const ulong BootstrapFileLimit = 2UL * 1024 * 1024 * 1024 * 1024;
    private const ulong RuntimeFileLimit = 256UL * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private sealed record Check(string Name, bool Passed, object? Detail);
    private sealed record Identity(string UserName, uint UserId, uint GroupId, string GroupName,
        string HomeDirectory, string Shell, Dictionary<string, string> FilesSha256);
    private sealed record Config(string ProcessRunId, string? ExpectedHome, string[] InvisiblePaths,
        string SentinelSha256, Dictionary<string, string> HostNamespaceIds, Identity Identity);
    private sealed record ThreadState(int Tid, Dictionary<string, string>? Fields, string? Error);
    private readonly List<Check> checks = [];

    private void Add(string name, bool passed, object? detail)
    {
        var check = new Check(name, passed, detail);
        checks.Add(check);
        File.AppendAllText(Work + "/control-events.jsonl", JsonSerializer.Serialize(check) + "\n");
        Console.WriteLine(JsonSerializer.Serialize(new { stage = name, passed }));
    }

    private static object Error(Exception error) => new
    { type = error.GetType().FullName, error.Message, hResult = error.HResult,
      hResultHex = "0x" + error.HResult.ToString("X8"),
      nativeErrorCode = error is Win32Exception win ? win.NativeErrorCode : (int?)null };

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Dictionary<string, string> Status(string path) => File.ReadAllLines(path)
        .Where(line => line.Contains(':')).Select(line => line.Split(':', 2))
        .ToDictionary(parts => parts[0], parts => parts[1].Trim());

    private static ThreadState[] ThreadStates() => Directory.GetDirectories("/proc/self/task").Select(path =>
    {
        int tid = int.Parse(Path.GetFileName(path));
        try { return new ThreadState(tid, Status(path + "/status"), null); }
        catch (Exception error) { return new ThreadState(tid, null, error.ToString()); }
    }).OrderBy(state => state.Tid).ToArray();

    private static Native.SyscallResult PathCall(long number, string path, ulong third = 0, ulong fourth = 0)
    {
        IntPtr nativePath = Marshal.StringToCoTaskMemUTF8(path);
        try { return Native.Call(number, unchecked((ulong)-100L), Native.Pointer(nativePath), third, fourth); }
        finally { Marshal.FreeCoTaskMem(nativePath); }
    }

    private static Native.SyscallResult SocketCall(int family)
    {
        Native.SyscallResult result = Native.Call(41, (ulong)family, 1, 0);
        if (result.ReturnValue >= 0) Native.Call(3, (ulong)result.ReturnValue);
        return result;
    }

    private static bool Errno(Native.SyscallResult result, int expected) => result.ReturnValue == -1 && result.Errno == expected;

    private static object SocketOption(ulong fd, string name, int option)
    {
        IntPtr value = Marshal.AllocHGlobal(4), length = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(value, 0); Marshal.WriteInt32(length, 4);
            Native.SyscallResult result = Native.Call(55, fd, 1, (ulong)option, Native.Pointer(value), Native.Pointer(length));
            int actualLength = Marshal.ReadInt32(length);
            return new { name, level = 1, option, result, actualLength,
                value = result.ReturnValue == 0 && actualLength == 4 ? (int?)Marshal.ReadInt32(value) : null };
        }
        finally { Marshal.FreeHGlobal(value); Marshal.FreeHGlobal(length); }
    }

    private static object SocketAddress(ulong fd, bool peer)
    {
        const int Capacity = 128;
        IntPtr address = Marshal.AllocHGlobal(Capacity), length = Marshal.AllocHGlobal(4);
        try
        {
            byte[] bytes = new byte[Capacity];
            Marshal.Copy(bytes, 0, address, Capacity); Marshal.WriteInt32(length, Capacity);
            Native.SyscallResult result = Native.Call(peer ? 52 : 51, fd, Native.Pointer(address), Native.Pointer(length));
            int actualLength = Marshal.ReadInt32(length);
            int captured = result.ReturnValue == 0 ? Math.Clamp(actualLength, 0, Capacity) : 0;
            Marshal.Copy(address, bytes, 0, captured);
            int? family = captured >= 2 ? BitConverter.ToUInt16(bytes, 0) : null;
            return new { syscall = peer ? "getpeername" : "getsockname", result, actualLength,
                capacity = Capacity, truncated = actualLength > Capacity, family,
                bytesHex = Convert.ToHexString(bytes.AsSpan(0, captured)).ToLowerInvariant(),
                netlink = family == 16 && captured >= 12
                    ? new { portId = BitConverter.ToUInt32(bytes, 4), groups = BitConverter.ToUInt32(bytes, 8) } : null };
        }
        finally { Marshal.FreeHGlobal(address); Marshal.FreeHGlobal(length); }
    }

    private void SocketDiagnosticsBeforeGuard()
    {
        // Read-only metadata only: no socket creation, send/receive, connect, close or filter relaxation.
        var sockets = new List<object>();
        foreach (string path in Directory.GetFileSystemEntries("/proc/self/fd").OrderBy(path => path))
        {
            string? target = new FileInfo(path).LinkTarget;
            if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal) || !target.EndsWith(']')) continue;
            string inode = target[8..^1];
            ulong fd = ulong.Parse(Path.GetFileName(path));
            object? fdInfo;
            try { fdInfo = File.ReadAllText("/proc/self/fdinfo/" + fd); }
            catch (Exception error) { fdInfo = Error(error); }
            var tables = new List<object>();
            foreach (string table in new[] { "unix", "netlink", "tcp", "tcp6", "udp", "udp6", "raw", "raw6" })
            {
                string tablePath = "/proc/self/net/" + table;
                try
                {
                    string[] lines = File.ReadAllLines(tablePath);
                    string[] matches = lines.Skip(1).Where(line => line.Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries).Contains(inode)).ToArray();
                    tables.Add(new { path = tablePath, header = lines.FirstOrDefault(), inodeTokenMatches = matches });
                }
                catch (Exception error) { tables.Add(new { path = tablePath, error = Error(error) }); }
            }
            object[] options = [SocketOption(fd, "SO_DOMAIN", 39), SocketOption(fd, "SO_TYPE", 3), SocketOption(fd, "SO_PROTOCOL", 38)];
            object local = SocketAddress(fd, false), peer = SocketAddress(fd, true);
            string? finalTarget = new FileInfo(path).LinkTarget;
            sockets.Add(new { fd, target, inode, fdInfo, options, local, peer, tables,
                finalTarget, descriptorIdentityStable = finalTarget == target });
        }
        const string reportPath = Work + "/socket-diagnostics-before-guard.json";
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { schema = "e003-socket-diagnostic-v1",
            capturedUtc = DateTimeOffset.UtcNow.ToString("O"), processRunId = System.Environment.GetEnvironmentVariable("PROCESS_RUN_ID"),
            phase = "before_managed_seccomp_with_early_native_guard_no_game", namespaceId = new FileInfo("/proc/self/ns/net").LinkTarget,
            scope = "Metadata only; inode matches are from the current net namespace and do not identify a creator by themselves.",
            sockets }, Json));
        Add("socket_diagnostics_recorded", true, new { path = reportPath, sha256 = Hash(reportPath), socketCount = sockets.Count });
    }

    private sealed record FileLimits(ulong Soft, ulong Hard);
    private sealed record MapperRegion(string Permissions, string Device, string Inode, ulong FileStart, ulong FileEnd);
    private static string[] DoubleMapperMaps() => File.ReadAllLines("/proc/self/maps")
        .Where(line => line.Contains("memfd:doublemapper", StringComparison.Ordinal)).ToArray();
    private void LowerFileSizeLimit()
    {
        // Linux x86_64 prlimit64(pid=0, RLIMIT_FSIZE=1, new, old), two uint64 fields.
        // CoreCLR has already created its 2 TiB logical memfd; lowering does not truncate it.
        IntPtr before = Marshal.AllocHGlobal(16), requested = Marshal.AllocHGlobal(16), after = Marshal.AllocHGlobal(16);
        static FileLimits Read(IntPtr value) => new(unchecked((ulong)Marshal.ReadInt64(value)),
            unchecked((ulong)Marshal.ReadInt64(value, 8)));
        try
        {
            Marshal.WriteInt64(before, 0); Marshal.WriteInt64(before, 8, 0);
            Marshal.WriteInt64(after, 0); Marshal.WriteInt64(after, 8, 0);
            Marshal.WriteInt64(requested, checked((long)RuntimeFileLimit));
            Marshal.WriteInt64(requested, 8, checked((long)RuntimeFileLimit));
            Native.SyscallResult getBefore = Native.Call(302, 0, 1, 0, Native.Pointer(before));
            FileLimits old = Read(before);
            Native.SyscallResult set = Native.Call(302, 0, 1, Native.Pointer(requested), 0);
            Native.SyscallResult getAfter = Native.Call(302, 0, 1, 0, Native.Pointer(after));
            FileLimits current = Read(after);
            bool passed = getBefore.ReturnValue == 0 && old.Soft == BootstrapFileLimit && old.Hard == BootstrapFileLimit
                && set.ReturnValue == 0 && getAfter.ReturnValue == 0
                && current.Soft == RuntimeFileLimit && current.Hard == RuntimeFileLimit;
            File.WriteAllText(Work + "/process-limits.txt", File.ReadAllText("/proc/self/limits"));
            File.WriteAllLines(Work + "/doublemapper-after-limit-maps.txt", DoubleMapperMaps());
            Add("fsize_lowered_and_verified", passed, new { getBefore, before = old, set, getAfter, after = current,
                expectedBootstrapBytes = BootstrapFileLimit, expectedRuntimeBytes = RuntimeFileLimit,
                phase = "owned_core_api_callback_before_managed_guard_or_game_loading" });
            if (!passed) throw new InvalidOperationException("File-size limit transition or readback failed");
        }
        finally { Marshal.FreeHGlobal(before); Marshal.FreeHGlobal(requested); Marshal.FreeHGlobal(after); }
    }

    private void LateJitControl()
    {
        // This method and delegate are created only after both the hard limit and guard are active.
        var method = new DynamicMethod("E003_AfterLimitsAndGuard", typeof(int), [typeof(int)], typeof(Main).Module);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, 17); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldc_I4, 23); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        var compiled = (Func<int, int>)method.CreateDelegate(typeof(Func<int, int>));
        int actual = compiled(37);
        Add("dynamic_jit_after_limit_and_guard", actual == 652
            && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
            && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled,
            new { actual, expected = 652, createdAfterHardLimitReduction = true, createdAfterGuard = true });

        string[] maps = DoubleMapperMaps();
        File.WriteAllLines(Work + "/doublemapper-maps.txt", maps);
        var regions = maps.Select(line =>
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || fields[5] != "/memfd:doublemapper")
                throw new InvalidDataException("Unexpected doublemapper /proc/maps layout");
            static ulong Hex(string text) => ulong.Parse(text, System.Globalization.NumberStyles.HexNumber);
            string[] addresses = fields[0].Split('-');
            ulong length = checked(Hex(addresses[1]) - Hex(addresses[0]));
            ulong offset = Hex(fields[2]);
            return new MapperRegion(fields[1], fields[3], fields[4], offset, checked(offset + length));
        }).ToArray();
        MapperRegion[] rx = regions.Where(region => region.Permissions == "r-xs").ToArray();
        MapperRegion[] rw = regions.Where(region => region.Permissions == "rw-s").ToArray();
        bool paired = rx.Any(executable => rw.Any(writable => executable.Device == writable.Device
            && executable.Inode == writable.Inode && executable.FileStart < writable.FileEnd && writable.FileStart < executable.FileEnd));
        Add("doublemapper_wx_after_jit", paired
            && regions.All(region => !(region.Permissions.Contains('w') && region.Permissions.Contains('x')))
            && System.Environment.GetEnvironmentVariable("DOTNET_EnableWriteXorExecute") == "1",
            new { regions, rxCount = rx.Length, rwCount = rw.Length, sameBackingOverlappingPair = paired,
                mapCount = maps.Length, explicitlyEnabled = "DOTNET_EnableWriteXorExecute=1",
                limitation = "Observed doublemapper regions after this JIT; not a proof about all future mappings" });
    }

    private sealed class ThreadProbe : IDisposable
    {
        private readonly ManualResetEventSlim ready = new(), go = new(), done = new(), finish = new();
        private readonly Thread thread;
        private volatile bool cancelled;
        public int Tid { get; private set; }
        public Native.SyscallResult? Socket { get; private set; }
        public Dictionary<string, string>? ThreadStatus { get; private set; }
        public string? Failure { get; private set; }
        public ThreadProbe(string name)
        {
            thread = new Thread(() =>
            {
                try
                {
                    Tid = checked((int)Native.Call(186).ReturnValue);
                    ready.Set();
                    go.Wait();
                    if (cancelled) return;
                    Socket = SocketCall(2);
                    ThreadStatus = Status("/proc/self/task/" + Tid + "/status");
                }
                catch (Exception error) { Failure = error.ToString(); }
                finally { ready.Set(); done.Set(); finish.Wait(); }
            }) { IsBackground = true, Name = name };
            thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Probe thread did not start");
        }
        public void Trigger()
        {
            go.Set();
            if (!done.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Probe thread did not finish control");
        }
        public object Evidence() => new { tid = Tid, socket = Socket, threadStatus = ThreadStatus, error = Failure };
        public bool Passed => Failure is null && Socket is not null && Errno(Socket, 1)
            && ThreadStatus?.GetValueOrDefault("Seccomp") == "2"
            && ThreadStatus?.GetValueOrDefault("NoNewPrivs") == "1";
        public void Dispose()
        {
            cancelled = true; go.Set(); finish.Set();
            if (!thread.Join(TimeSpan.FromSeconds(3))) throw new TimeoutException("Probe thread did not join");
            ready.Dispose(); go.Dispose(); done.Dispose(); finish.Dispose();
        }
    }

    internal int Run()
    {
        string startedUtc = DateTimeOffset.UtcNow.ToString("O");
        Config? config = null;
        Native.Installation? guard = null;
        object? environment = null;
        string? failure = null;
        int exitCode = 43;
        ThreadProbe? beforeThread = null, afterThread = null;
        try
        {
            string[] args = OS.GetCmdlineUserArgs();
            if (!args.Contains("--e003-linux-host-controls") || !args.Contains("--config=/work/control-config.json"))
                throw new InvalidOperationException("Explicit no-game control arguments are required");
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("This control host requires Linux x86_64");
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Work + "/control-config.json"), Json)
                ?? throw new InvalidDataException("Missing control config");
            if (config.ProcessRunId != System.Environment.GetEnvironmentVariable("PROCESS_RUN_ID"))
                throw new InvalidDataException("Process identity mismatch");
            if (File.Exists(Work + "/control-report.json")) throw new IOException("Refuse an existing control report");
            LowerFileSizeLimit();
            environment = new { processRunId = config.ProcessRunId, startedUtc,
                framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                os = RuntimeInformation.OSDescription, godot = Engine.GetVersionInfo()["string"].AsString(),
                display = DisplayServer.GetName(), audio = AudioServer.GetDriverName(), userData = OS.GetUserDataDir(),
                home = System.Environment.GetEnvironmentVariable("HOME"), namespaces = config.HostNamespaceIds };

            // No game references, PCK mounts or reflection loads exist in this project.
            beforeThread = new ThreadProbe("e003-created-before-guard");
            ThreadState[] beforeStates = ThreadStates();
            SocketDiagnosticsBeforeGuard();
            guard = Native.Install();
            Add("guard_install", guard.Passed, new { guard, beforeStates });
            if (!guard.Passed) { exitCode = 42; throw new InvalidOperationException("Guard failed; skip all native negative controls"); }

            // read/write on a socket opened before seccomp would evade a socket-syscall-only rule.
            // The supervisor closes inherited fds and diagnostics are off; verify the actual host too.
            var descriptors = Directory.GetFileSystemEntries("/proc/self/fd").Select(path =>
                new { path, target = new FileInfo(path).LinkTarget }).Where(item => item.target is not null).ToArray();
            bool noSocketDescriptors = descriptors.All(item => !item.target!.StartsWith("socket:[", StringComparison.Ordinal));
            Add("no_socket_file_descriptors", noSocketDescriptors, descriptors);
            if (!noSocketDescriptors) throw new InvalidOperationException("A preexisting socket descriptor invalidates this guard control");

            beforeThread.Trigger();
            Add("existing_thread_tsync", beforeThread.Passed, beforeThread.Evidence());
            afterThread = new ThreadProbe("e003-created-after-guard");
            afterThread.Trigger();
            Add("new_managed_thread", afterThread.Passed && afterThread.Tid != beforeThread.Tid, afterThread.Evidence());
            ThreadState[] afterStates = ThreadStates();
            Add("all_observed_threads_guarded", afterStates.Length >= 3 && afterStates.All(state => state.Error is null
                && state.Fields?.GetValueOrDefault("Seccomp") == "2" && state.Fields?.GetValueOrDefault("NoNewPrivs") == "1"), afterStates);
            LateJitControl();

            Add("headless_dummy_userdata", DisplayServer.GetName() == "headless" && AudioServer.GetDriverName() == "Dummy"
                && Path.GetFullPath(OS.GetUserDataDir()).StartsWith(Work + "/", StringComparison.Ordinal), environment);
            Add("home_value_preserved", System.Environment.GetEnvironmentVariable("HOME") == config.ExpectedHome,
                new { expected = config.ExpectedHome, actual = System.Environment.GetEnvironmentVariable("HOME") });
            var namespaces = config.HostNamespaceIds.Keys.ToDictionary(name => name,
                name => new FileInfo("/proc/self/ns/" + name).LinkTarget);
            Add("new_namespaces", namespaces.All(pair => pair.Value is not null && pair.Value != config.HostNamespaceIds[pair.Key]), namespaces);

            string mountInfo = File.ReadAllText("/proc/self/mountinfo");
            File.WriteAllText(Work + "/mountinfo.txt", mountInfo);
            var mounts = mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' '))
                .Select(parts => new { path = parts[4], root = parts[3], device = parts[2], options = parts[5],
                    fileSystem = parts[Array.IndexOf(parts, "-") + 1] }).ToArray();
            Identity identity = config.Identity ?? throw new InvalidDataException("Missing actual identity projection");
            string userName = System.Environment.UserName; // Exercises libc passwd lookup after the guard is active.
            Native.SyscallResult uid = Native.Call(102), euid = Native.Call(107), gid = Native.Call(104), egid = Native.Call(108);
            Add("nss_current_user_identity", userName == identity.UserName
                && uid.ReturnValue == identity.UserId && euid.ReturnValue == identity.UserId
                && gid.ReturnValue == identity.GroupId && egid.ReturnValue == identity.GroupId,
                new { userName, uid, euid, gid, egid, expected = identity });
            var expectedIdentityFiles = new Dictionary<string, string>
            {
                ["passwd"] = $"{identity.UserName}:*:{identity.UserId}:{identity.GroupId}::{identity.HomeDirectory}:{identity.Shell}\n",
                ["group"] = $"{identity.GroupName}:*:{identity.GroupId}:\n",
                ["nsswitch.conf"] = "passwd: files\ngroup: files\ninitgroups: files\n"
            };
            var identityFiles = expectedIdentityFiles.Select(pair => new
            {
                name = pair.Key, sha256 = Hash("/etc/" + pair.Key), contentMatches = File.ReadAllText("/etc/" + pair.Key) == pair.Value,
                readOnlyMount = mounts.Count(m => m.path == "/etc/" + pair.Key) == 1
                    && mounts.Any(m => m.path == "/etc/" + pair.Key && m.options.Split(',').Contains("ro"))
            }).ToArray();
            Add("nss_identity_files_readonly", identity.FilesSha256.Count == expectedIdentityFiles.Count
                && identityFiles.All(item => item.contentMatches && item.readOnlyMount
                    && item.sha256 == identity.FilesSha256.GetValueOrDefault(item.name)), identityFiles);
            string[] deviceMounts = ["/dev/null", "/dev/zero", "/dev/full", "/dev/random", "/dev/urandom", "/dev/tty"];
            var workMount = mounts.SingleOrDefault(m => m.path == Work);
            Add("sparse_writable_mounts", mounts.Any(m => m.path == "/" && m.options.Split(',').Contains("ro"))
                && workMount is not null && workMount.options.Split(',').Contains("rw")
                && mounts.Any(m => m.path == "/tmp" && m.options.Split(',').Contains("rw")
                    && m.device == workMount.device && m.root == workMount.root.TrimEnd('/') + "/tmp")
                && mounts.Any(m => m.path == "/dev/shm" && m.options.Split(',').Contains("rw")
                    && m.device == workMount.device && m.root == workMount.root.TrimEnd('/') + "/shm")
                && mounts.Where(m => m.options.Split(',').Contains("rw")).All(m => m.path == Work || m.path == "/tmp"
                    || m.path == "/dev/shm" || deviceMounts.Contains(m.path)), mounts);
            Dictionary<string, string> processStatus = Status("/proc/self/status");
            Add("no_effective_capabilities", processStatus.GetValueOrDefault("CapEff") == "0000000000000000", processStatus);

            using (var allowed = new FileStream(Work + "/allowed.txt", FileMode.CreateNew, System.IO.FileAccess.Write))
            { allowed.Write("E003 allowed write\n"u8); allowed.Flush(true); }
            Add("private_write", File.ReadAllText(Work + "/allowed.txt") == "E003 allowed write\n", new { path = Work + "/allowed.txt" });
            Add("readonly_sentinel_read", Hash("/readonly/sentinel") == config.SentinelSha256, new { actualHash = Hash("/readonly/sentinel") });
            Native.SyscallResult create = PathCall(257, "/readonly/must-not-exist", 1 | 64 | 128 | 524288, 0x180);
            if (create.ReturnValue >= 0) Native.Call(3, (ulong)create.ReturnValue);
            Add("readonly_create_denied", Errno(create, 30), create);
            Native.SyscallResult unlink = PathCall(263, "/readonly/sentinel");
            Add("readonly_unlink_denied", Errno(unlink, 30), unlink);
            Native.SyscallResult chmod = PathCall(268, "/readonly/sentinel", 0x1FF);
            Add("readonly_chmod_denied", Errno(chmod, 30), chmod);
            try { File.WriteAllText("/readonly/managed-attempt", "must be denied"); Add("managed_readonly_write_denied", false, null); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Add("managed_readonly_write_denied", !File.Exists("/readonly/managed-attempt"), Error(error)); }
            Add("readonly_sentinel_unchanged", File.Exists("/readonly/sentinel") && Hash("/readonly/sentinel") == config.SentinelSha256, null);

            foreach (string path in config.InvisiblePaths)
            {
                Native.SyscallResult opened = PathCall(257, path, 0x200000 | 524288); // O_PATH | O_CLOEXEC
                if (opened.ReturnValue >= 0) Native.Call(3, (ulong)opened.ReturnValue);
                Add("host_path_hidden:" + path, Errno(opened, 2), new { path, result = opened });
            }
            foreach ((int family, string name) in new[] { (2, "ipv4"), (10, "ipv6"), (1, "unix") })
            {
                Native.SyscallResult socketResult = SocketCall(family);
                Add("raw_socket_denied:" + name, Errno(socketResult, 1), new { family, result = socketResult });
            }
            foreach (AddressFamily family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6, AddressFamily.Unix })
            {
                try { using var socket = new Socket(family, SocketType.Stream, ProtocolType.Unspecified); Add("managed_socket_denied:" + family, false, null); }
                catch (SocketException error)
                {
                    Add("managed_socket_denied:" + family, error.NativeErrorCode is 1 or 13
                        && error.SocketErrorCode == SocketError.AccessDenied,
                        new { error = Error(error), socketErrorCode = error.SocketErrorCode.ToString() });
                }
            }

            if (!File.Exists("/usr/bin/true")) throw new FileNotFoundException("Negative exec target must actually exist", "/usr/bin/true");
            try
            {
                using Process? child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false, WorkingDirectory = Work });
                child?.WaitForExit(1000);
                Add("process_start_denied", false, new { unexpectedChildId = child?.Id, exited = child?.HasExited });
            }
            catch (Win32Exception error) { Add("process_start_denied", error.NativeErrorCode == 1, Error(error)); }
            ForkControl("fork_denied", 57);
            ForkControl("clone_process_denied", 56, 17);
            Native.SyscallResult clone3 = Native.Call(435, 0, 0);
            Add("clone3_returns_enosys", Errno(clone3, 38), clone3);
            Native.SyscallResult x32 = Native.Call(0x40000000 | 39);
            Add("x32_abi_denied", Errno(x32, 1), x32);
            Native.SyscallResult oldX32 = Native.Call(512);
            Add("old_x32_alias_denied", Errno(oldX32, 1), oldX32);
            ExecControl("execve_denied", false);
            ExecControl("execveat_denied", true);
            exitCode = checks.All(check => check.Passed) ? 0 : 43;
        }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine("E003_CONTROL_FAILURE\n" + error); }
        finally
        {
            foreach (ThreadProbe? thread in new[] { afterThread, beforeThread })
            {
                try { thread?.Dispose(); }
                catch (Exception error) { failure = (failure ?? "") + "\nTHREAD_CLEANUP: " + error; exitCode = 43; }
            }
            try
            {
                using var output = new FileStream(Work + "/control-report.json", FileMode.CreateNew, System.IO.FileAccess.Write);
                JsonSerializer.Serialize(output, new { schema = "e003-linux-host-controls-v1", processRunId = config?.ProcessRunId,
                    startedUtc, finishedUtc = DateTimeOffset.UtcNow.ToString("O"), environment, guard, checks,
                    exitCode, passed = exitCode == 0 && failure is null, error = failure, gameCodeLoaded = false }, Json);
                output.Flush(true);
            }
            catch (Exception error) { Console.Error.WriteLine("E003_REPORT_FAILURE\n" + error); exitCode = 44; }
        }
        return exitCode;

    }

    private void ForkControl(string name, long syscall, ulong flags = 0)
    {
        Native.SyscallResult result = Native.Call(syscall, flags);
        if (result.ReturnValue == 0) Native.ExitForkedChild(71);
        Native.SyscallResult? killed = null, waited = null;
        if (result.ReturnValue > 0)
        {
            killed = Native.Call(62, (ulong)result.ReturnValue, 9);
            waited = Native.Call(61, (ulong)result.ReturnValue, 0, 0, 0);
        }
        Add(name, Errno(result, 1), new { result, unexpectedChildKill = killed, unexpectedChildWait = waited });
    }

    private void ExecControl(string name, bool at)
    {
        IntPtr path = Marshal.StringToCoTaskMemUTF8("/usr/bin/true");
        IntPtr argv = Marshal.AllocHGlobal(16), env = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteIntPtr(argv, 0, path); Marshal.WriteIntPtr(argv, 8, IntPtr.Zero); Marshal.WriteIntPtr(env, IntPtr.Zero);
            Native.SyscallResult result = at
                ? Native.Call(322, unchecked((ulong)-100L), Native.Pointer(path), Native.Pointer(argv), Native.Pointer(env), 0)
                : Native.Call(59, Native.Pointer(path), Native.Pointer(argv), Native.Pointer(env));
            Add(name, Errno(result, 1), result);
        }
        finally { Marshal.FreeCoTaskMem(path); Marshal.FreeHGlobal(argv); Marshal.FreeHGlobal(env); }
    }
}
