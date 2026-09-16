using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot.Bridge;
using Godot.NativeInterop;
using Native = E003.Linux.LinuxSeccompGuard;

internal static unsafe class ManagedBootstrap
{
    private const string Work = "/work";
    private const string Journal = Work + "/managed-bootstrap-events.jsonl";
    private const int CallbackFields = 37;
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly object JournalLock = new();
    private static delegate* unmanaged<godot_bool, void> originalCoreApiCallback;
    private static int entryCount, tablePublished, callbackCount, sdkReturned, registrationCount;
    private static int completed, readyCount, sequence;
    private static string runId = "", phase = "not_entered", controlsHash = "", markerHash = "";
    private static string registeredSentinelHash = "";
    private static EarlyGuardMarker.Proof? earlyGuardProof;

    internal static void Require([DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string HashFile(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    private static void Event(string stage, object detail)
    {
        lock (JournalLock)
        {
            int next = checked(sequence + 1);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            { schema = "e004b-normal-boot-bootstrap-event-v1", sequence = next, processRunId = runId,
                pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow.ToString("O"), stage, detail }, Json);
            using var stream = new FileStream(Journal, next == 1 ? FileMode.CreateNew : FileMode.Open,
                FileAccess.Write, FileShare.Read);
            stream.Seek(0, SeekOrigin.End);
            stream.Write(bytes); stream.WriteByte((byte)'\n'); stream.Flush(true);
            sequence = next; phase = stage;
        }
    }

    internal static void NewReport(string leaf, object value)
    {
        using var stream = new FileStream(Work + "/" + leaf, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, Json); stream.WriteByte((byte)'\n'); stream.Flush(true);
    }

    internal static object? ProjectSettingsProof { get; private set; }
    internal static void RecordEvent(string stage, object detail) => Event(stage, detail);

    internal static void BeginEntry()
    {
        Require(Interlocked.Increment(ref entryCount) == 1, "Initializer entered more than once");
        Require(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            && BitConverter.IsLittleEndian && IntPtr.Size == 8, "Pinned Linux x86_64 ABI is required");
        runId = Environment.GetEnvironmentVariable("PROCESS_RUN_ID") ?? "";
        Require(runId.Length is >= 1 and <= 128 && runId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'),
            "Missing or malformed PROCESS_RUN_ID");
        foreach (string leaf in new[] { "control-report.json", "normal-boot-identity.json", "normal-boot-final-identity.json",
            "normal-game-loader.json", "normal-boot-result.json", "official-progress.json",
            "managed-bootstrap-registered.json", "ready-sentinel.json", "ready-identity-result.json",
            "managed-bootstrap-failure.json" })
            Require(!File.Exists(Work + "/" + leaf), "Refuse an existing bootstrap result: " + leaf);
        Event("initializer_entered", new { controlsExecuted = false, ownScriptsRegistered = false });
    }

    internal static ManagedCallbacks InstallCallback(ManagedCallbacks original)
    {
        Require(entryCount == 1 && sequence == 1 && tablePublished == 0 && callbackCount == 0,
            "SDK callback installation did not immediately follow initializer setup");
        Require(sizeof(ManagedCallbacks) == CallbackFields * IntPtr.Size,
            "ManagedCallbacks ABI differs from the inspected 37-pointer SDK table");
        ReadOnlySpan<nint> originalPointers = new(&original, CallbackFields);
        foreach (nint pointer in originalPointers) Require(pointer != 0, "The SDK supplied a null callback");
        originalCoreApiCallback = original.GD_OnCoreApiAssemblyLoaded;
        Require(originalCoreApiCallback != null, "Original SDK core API callback is missing");

        ManagedCallbacks wrapped = original;
        wrapped.GD_OnCoreApiAssemblyLoaded = &OnCoreApiAssemblyLoaded;
        ManagedCallbacks restored = wrapped;
        restored.GD_OnCoreApiAssemblyLoaded = originalCoreApiCallback;
        ReadOnlySpan<byte> originalBytes = new(&original, sizeof(ManagedCallbacks));
        ReadOnlySpan<byte> restoredBytes = new(&restored, sizeof(ManagedCallbacks));
        Require(originalBytes.SequenceEqual(restoredBytes), "A non-target callback byte changed");
        ReadOnlySpan<nint> wrappedPointers = new(&wrapped, CallbackFields);
        int changedPointers = 0;
        for (int i = 0; i < CallbackFields; i++)
            if (originalPointers[i] != wrappedPointers[i]) changedPointers++;
        Require(changedPointers == 1, "Exactly one callback pointer must change");
        Event("callback_table_prepared", new { callbackFields = CallbackFields, tableBytes = sizeof(ManagedCallbacks),
            wrappedField = "GD_OnCoreApiAssemblyLoaded", changedPointers, allOtherBytesEqual = true,
            originalTableSha256 = Sha256(originalBytes),
            wrappedTableSha256 = Sha256(new ReadOnlySpan<byte>(&wrapped, sizeof(ManagedCallbacks))),
            controlsExecuted = false, ownScriptsRegistered = false });
        return wrapped;
    }

    internal static void EntryTablePublished()
    {
        Require(entryCount == 1 && Interlocked.Exchange(ref tablePublished, 1) == 0,
            "Callback table publication is out of order or repeated");
        Event("initializer_returning_table_only", new { controlsExecuted = false, ownScriptsRegistered = false });
    }

    [UnmanagedCallersOnly]
    private static void OnCoreApiAssemblyLoaded(godot_bool isDebug)
    {
        try
        {
            Require(entryCount == 1 && Volatile.Read(ref tablePublished) == 1
                && Interlocked.Increment(ref callbackCount) == 1, "Core API callback order or count failed");
            Require(originalCoreApiCallback != null, "Original SDK callback pointer was lost");
            // First payload action: retain the SDK scheduler, synchronization context and debug setup.
            originalCoreApiCallback(isDebug);
            Volatile.Write(ref sdkReturned, 1);
            string? contextType = SynchronizationContext.Current?.GetType().FullName;
            Require(contextType == "Godot.GodotSynchronizationContext",
                "SDK callback did not establish the inspected Godot synchronization context");
            Event("original_sdk_callback_returned", new { contextType, originalArgument = (int)isDebug,
                limitation = "The SDK callback returns void and catches its own exceptions; this checks its required context postcondition, not a new SDK result code." });

            // This uses ordinary managed/native checks and runs before the managed filter re-install.
            EarlyGuardMarker.Proof marker = EarlyGuardMarker.Verify(runId);
            earlyGuardProof = marker;
            markerHash = marker.MarkerSha256;
            Event("early_guard_independently_checked", marker);

            Event("full_47_controls_starting", new { ordinaryManagedControlClass = "HostControlSuite",
                constructedGodotNode = false, ownScriptsRegistered = false });
            int code = new HostControlSuite().Run();
            Require(code == 0, "Original 47-control suite returned " + code);
            ControlsProof controls = CheckControls();
            controlsHash = controls.ReportSha256;
            Event("full_47_controls_completed", controls);

            // Native extensions from the original PCK may already have executed behind the early guard.
            // The unchanged legacy gameCodeLoaded=false field describes no sts2 MANAGED assembly at the 47 phase only.
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "sts2"),
                "The game managed assembly appeared before all 47 controls completed");
            NormalBootConfig.Config normal = NormalBootConfig.LoadAndCheck();
            ProjectSettingsProof = ProjectSettingsContract.Verify(normal.OriginalProjectBinary.Path,
                NormalBootConfig.BridgeName, NormalBootConfig.ObserverName, NormalBootConfig.ObserverScript);
            NewReport("normal-boot-inputs-before.json", new
            { schema = "e004b-normal-boot-inputs-before-v1", processRunId = runId,
                NormalBootConfig.ConfigSha256, NormalBootConfig.InputBefore,
                projectSettings = ProjectSettingsProof, phase = "after_47_before_game_managed_load",
                gameManagedAssemblyLoaded = false, nativeGameCodeMayHaveLoaded = true });
            Event("normal_project_and_inputs_checked", new { NormalBootConfig.ConfigSha256,
                gameManagedAssemblyLoaded = false, nativeGameCodeMayHaveLoaded = true });
            // Preserve the SDK's own-assembly registration and retain all original generators other than the initializer.
            ScriptManagerBridge.LookupScriptsInAssembly(typeof(global::GodotPlugins.Game.Main).Assembly);
            Require(Interlocked.Increment(ref registrationCount) == 1, "Own script registration repeated");
            Event("own_scripts_registered_after_47", new { assembly = typeof(global::GodotPlugins.Game.Main).Assembly.GetName().Name,
                entryCount, callbackCount, sdkReturned, registrationCount, controlsHash, markerHash });
            _ = NormalGameLoader.LoadAndRegister(normal);
            Event("original_game_lookup_returned_once", new { gameManagedAssemblyLoaded = true,
                originalScriptMetadataCount = 709, gameLookupCount = NormalGameLoader.LookupCount });
            NewReport("managed-bootstrap-registered.json", new
            { schema = "e004b-normal-boot-bootstrap-registered-v1", processRunId = runId, pid = Environment.ProcessId,
                completedUtc = DateTimeOffset.UtcNow.ToString("O"), entryCount, callbackCount, sdkReturned,
                registrationCount, originalGameLookupCount = NormalGameLoader.LookupCount,
                originalScriptMetadataCount = 709, gameManagedAssemblyLoaded = true, nativeGameCodeMayHaveLoaded = true,
                controlsCount = 47, controlsReportSha256 = controlsHash,
                markerSha256 = markerHash, eventJournalSha256 = HashFile(Journal), passed = true,
                scope = "Original game registered after 47 controls; native autoload/main scene and full progress comparison still pending" });
            registeredSentinelHash = HashFile(Work + "/managed-bootstrap-registered.json");
            Volatile.Write(ref completed, 1);
        }
        catch (Exception error)
        {
            Abort("core_api_callback", error, 122);
        }
    }

    private sealed record ControlsProof(string ReportSha256, int Count, string[] Names);

    private static ControlsProof CheckControls()
    {
        using var config = JsonDocument.Parse(File.ReadAllBytes(Work + "/control-config.json"));
        Require(config.RootElement.GetProperty("processRunId").GetString() == runId, "Control config run ID changed");
        string[] hidden = config.RootElement.GetProperty("invisiblePaths").EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidDataException("Null invisible path")).ToArray();
        Require(hidden.Length == 10 && hidden.Distinct(StringComparer.Ordinal).Count() == 10
            && hidden.All(path => path.StartsWith('/') && !path.Contains('\0')), "The accepted 47-control path set requires 10 unique absolute paths");
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "fsize_lowered_and_verified", "dynamic_jit_after_limit_and_guard", "doublemapper_wx_after_jit",
            "socket_diagnostics_recorded", "guard_install", "existing_thread_tsync", "new_managed_thread",
            "all_observed_threads_guarded", "no_socket_file_descriptors", "headless_dummy_userdata",
            "home_value_preserved", "new_namespaces", "nss_current_user_identity", "nss_identity_files_readonly",
            "sparse_writable_mounts", "no_effective_capabilities", "private_write", "readonly_sentinel_read",
            "readonly_create_denied", "readonly_unlink_denied", "readonly_chmod_denied", "managed_readonly_write_denied",
            "readonly_sentinel_unchanged", "process_start_denied", "fork_denied", "clone_process_denied",
            "clone3_returns_enosys", "x32_abi_denied", "old_x32_alias_denied", "execve_denied", "execveat_denied"
        };
        expected.UnionWith(hidden.Select(path => "host_path_hidden:" + path));
        expected.UnionWith(new[] { "ipv4", "ipv6", "unix" }.Select(name => "raw_socket_denied:" + name));
        expected.UnionWith(new[] { "InterNetwork", "InterNetworkV6", "Unix" }.Select(name => "managed_socket_denied:" + name));
        byte[] bytes = File.ReadAllBytes(Work + "/control-report.json");
        Require(bytes.Length is > 0 and <= 16777216, "Control report size is invalid");
        using var report = JsonDocument.Parse(bytes);
        JsonElement root = report.RootElement;
        Require(root.GetProperty("schema").GetString() == "e003-linux-host-controls-v1"
            && root.GetProperty("processRunId").GetString() == runId
            && root.GetProperty("passed").ValueKind == JsonValueKind.True
            && root.GetProperty("exitCode").GetInt32() == 0
            && root.GetProperty("error").ValueKind == JsonValueKind.Null
            && root.GetProperty("gameCodeLoaded").ValueKind == JsonValueKind.False,
            "Original control report header does not prove successful completion for this run");
        JsonElement[] checks = root.GetProperty("checks").EnumerateArray().ToArray();
        string[] names = checks.Select(item => item.GetProperty("name").GetString()
            ?? throw new InvalidDataException("Null control name")).ToArray();
        Require(expected.Count == 47 && checks.Length == 47 && names.Distinct(StringComparer.Ordinal).Count() == 47
            && expected.SetEquals(names) && checks.All(item => item.GetProperty("passed").ValueKind == JsonValueKind.True),
            "Missing, extra, duplicate or failed checks in the required full 47-control set");
        return new(Sha256(bytes), checks.Length, names);
    }

    internal static void RequireCompleted47AtReady()
    {
        Require(Volatile.Read(ref completed) == 1 && entryCount == 1 && tablePublished == 1
            && callbackCount == 1 && sdkReturned == 1 && registrationCount == 1 && NormalGameLoader.LookupCount == 1
            && Interlocked.Increment(ref readyCount) == 1, "Ready reached without exactly one completed bootstrap");
        ControlsProof controls = CheckControls();
        Require(controls.ReportSha256 == controlsHash && registeredSentinelHash.Length == 64
            && HashFile(Work + "/managed-bootstrap-registered.json") == registeredSentinelHash
            && HashFile(Work + "/e004b-early-guard.marker") == markerHash,
            "Bootstrap evidence changed before Ready");
        Event("observer_ready_sentinel_passed_before_normal_identity", new { readyCount, controls });
        NewReport("ready-sentinel.json", new
        { schema = "e004b-normal-boot-ready-sentinel-v1", processRunId = runId, pid = Environment.ProcessId,
            entryCount, tablePublished, callbackCount, sdkReturned, registrationCount, readyCount,
            controlsCount = controls.Count, controlsReportSha256 = controlsHash, markerSha256 = markerHash,
            registeredSentinelSha256 = registeredSentinelHash, passed = true, normalIdentityStillPending = true, gameManagedAssemblyLoaded = true, nativeGameCodeMayHaveLoaded = true });
    }

    internal static EarlyGuardMarker.Proof ProofAtReady()
    {
        Require(Volatile.Read(ref completed) == 1 && readyCount == 1 && earlyGuardProof is not null,
            "Release identity requested the early proof before successful bootstrap and Ready");
        return earlyGuardProof!;
    }

    internal static void RecordReadyIdentityResult(int code)
    {
        Require(readyCount == 1 && completed == 1, "Identity result arrived without the Ready sentinel");
        Event("ready_identity_returned", new { code });
        Require(code == 0, "Release identity checks returned " + code);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Work + "/normal-boot-identity.json"));
        JsonElement root = document.RootElement;
        JsonElement[] checks = root.GetProperty("checks").EnumerateArray().ToArray();
        string[] identityNames = checks.Select(item => item.GetProperty("name").GetString()
            ?? throw new InvalidDataException("Null release identity check name")).ToArray();
        string[] expectedIdentityNames = ReleaseHostIdentity.ExpectedChecks;
        Require(root.GetProperty("schema").GetString() == "e004b-normal-boot-identity-v1"
            && root.GetProperty("processRunId").GetString() == runId
            && root.GetProperty("passed").ValueKind == JsonValueKind.True
            && root.GetProperty("exitCode").GetInt32() == 0
            && root.GetProperty("originalControlsExitCode").GetInt32() == 0
            && root.GetProperty("error").ValueKind == JsonValueKind.Null
            && root.GetProperty("gameAssemblyObserved").ValueKind == JsonValueKind.True
            && root.GetProperty("packFileObserved").ValueKind == JsonValueKind.True
            && checks.Length == expectedIdentityNames.Length
            && identityNames.Distinct(StringComparer.Ordinal).Count() == expectedIdentityNames.Length
            && new HashSet<string>(expectedIdentityNames, StringComparer.Ordinal).SetEquals(identityNames)
            && checks.All(item => item.GetProperty("passed").ValueKind == JsonValueKind.True),
            "The new normal-boot identity contract did not pass every declared check");
        NewReport("ready-identity-result.json", new
        { schema = "e004b-normal-boot-ready-identity-v1", processRunId = runId, pid = Environment.ProcessId,
            readyCount, controlsCount = 47, identityCount = expectedIdentityNames.Length, passed = true,
            identityReportSha256 = HashFile(Work + "/normal-boot-identity.json"),
            bootstrapReportSha256 = registeredSentinelHash, controlsReportSha256 = controlsHash,
            eventJournalSha256 = HashFile(Journal) });
    }

    [DoesNotReturn]
    internal static void Abort(string boundary, Exception error, int code)
    {
        try
        {
            string? stderrFailure = null;
            try { Console.Error.WriteLine("E004B_MANAGED_BOOTSTRAP_FATAL boundary=" + boundary + " phase=" + phase + " code=" + code + "\n" + error); }
            catch (Exception loggingError) { stderrFailure = loggingError.ToString(); }
            NewReport("managed-bootstrap-failure.json", new
            { schema = "e004b-normal-boot-bootstrap-failure-v1", processRunId = runId, pid = Environment.ProcessId,
                utc = DateTimeOffset.UtcNow.ToString("O"), boundary, phase, exitCode = code,
                entryCount, tablePublished, callbackCount, sdkReturned, registrationCount, completed, readyCount,
                controlsHash, markerHash, error = error.ToString(), stderrFailure, passed = false,
                gameManagedAssemblyLoadingAttempted = NormalGameLoader.LoadAttempted,
                originalGameLookupCount = NormalGameLoader.LookupCount, nativeGameCodeMayHaveLoaded = true,
                limitation = "If this CreateNew write fails, the original/partial files and stderr remain; no overwrite or retry." });
        }
        catch (Exception evidenceError)
        {
            try { Console.Error.WriteLine("E004B_BOOTSTRAP_FAILURE_EVIDENCE_ERROR\n" + evidenceError); }
            catch { /* A broken stderr cannot turn a fatal bootstrap into success. */ }
        }
        finally
        {
            // Do not unwind an exception across the unmanaged callback or continue native startup.
            try { Native.ExitForkedChild(code); }
            finally { Environment.FailFast("E004B _exit unexpectedly returned or threw", error); }
        }
        Environment.FailFast("E004B Abort reached terminal fallthrough", error);
    }
}
