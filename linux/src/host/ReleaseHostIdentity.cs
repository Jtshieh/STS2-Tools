using System.Reflection;
using System.Runtime.Loader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using File = System.IO.File;

internal static class ReleaseHostIdentity
{
    internal static readonly string[] ExpectedChecks =
    ["original_47_controls_completed", "identity_process_and_original_assembly", "native_version_numbers",
        "native_custom_version_tag", "release_feature_queries", "same_manifest_executable", "release_api_hash",
        "original_project_and_exact_two_key_override", "original_pack_and_no_alternate_publish", "complete_publish_hashes",
        "fixed_coreclr_runtime", "userland_libc_version", "userland_icu_globalization", "userland_required_dependencies",
        "userland_mapped_library_sources", "only_declared_bridge_game_assemblies", "original_game_actual_alc_and_shared_godot",
        "original_709_metadata_and_single_lookup", "original_profile2_input_provenance"];
    private const string Work = "/work";
    private const string Host = Work + "/native-host";
    private const string Data = Host + "/data_E003LinuxHostControls_linuxbsd_x86_64";
    private const string ParentExeHash = "0b0ae3859c4c26d352b6483dc1cb11601a07bbc9890d97bcebae0d5d0bdd99ff";
    private const string ApiHash = "0e4897ecdfb31456a97c7d8028dfb8d7dbdc632e2f73fc9b438d7b266a139289";
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private sealed record Check(string Name, bool Passed, object? Detail);
    private sealed record Config(string ProcessRunId, string AssemblyOriginalName,
        Dictionary<string, string> PublishedFilesSha256, string[] AllowedAssemblyNames, Userland Userland);
    private sealed record Dependency(string Path, string Sha256, long Bytes, int Mode, string ArchiveSource,
        string ResolvedVirtualPath, bool MaterializedAlias);
    private sealed record Userland(string ManifestSha256, Dictionary<string, string> FilesSha256,
        Dictionary<string, Dependency> RequiredDependencies, JsonElement Archives);
    private sealed record Loaded(string Name, string FullName, bool Dynamic, string Location, string? Sha256);
    // Report sequential map values, never object hashes. Hash collisions are
    // resolved by reference equality; held references last for this bounded process.
    private static readonly object ReferenceIdLock = new();
    private static readonly Dictionary<object, int> ReferenceIds = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private static int ReferenceId(object value)
    {
        lock (ReferenceIdLock)
        {
            if (ReferenceIds.TryGetValue(value, out int id)) return id;
            id = checked(ReferenceIds.Count + 1);
            ReferenceIds.Add(value, id);
            return id;
        }
    }
    private static AssemblyLoadContext LoadContext(Assembly assembly) => AssemblyLoadContext.GetLoadContext(assembly)
        ?? throw new InvalidDataException("Assembly has no observable load context: " + assembly.FullName);
    private static object CaptureAssemblyContexts(Assembly[] assemblies, Loaded[] loaded, int[] snapshotIndices, string phase)
    {
        if (assemblies.Length != loaded.Length || assemblies.Length != snapshotIndices.Length)
            throw new InvalidDataException("Assembly context snapshot correspondence mismatch");
        Assembly bridgeAssembly = typeof(Main).Assembly;
        Assembly gameAssembly = NormalGameLoader.GameAssembly;
        Assembly godotAssembly = typeof(GodotObject).Assembly;
        AssemblyLoadContext bridgeContext = LoadContext(bridgeAssembly);
        AssemblyLoadContext gameContext = LoadContext(gameAssembly);
        AssemblyLoadContext godotContext = LoadContext(godotAssembly);
        var anchors = new
        {
            bridge = new { assemblyReferenceId = ReferenceId(bridgeAssembly), loadContextReferenceId = ReferenceId(bridgeContext) },
            game = new { assemblyReferenceId = ReferenceId(gameAssembly), loadContextReferenceId = ReferenceId(gameContext) },
            godot = new { assemblyReferenceId = ReferenceId(godotAssembly), loadContextReferenceId = ReferenceId(godotContext) }
        };
        var rows = assemblies.Select((assembly, index) =>
        {
            AssemblyLoadContext context = LoadContext(assembly);
            return new
            {
                loadedAssemblyIndex = index, snapshotIndex = snapshotIndices[index],
                assemblyReferenceId = ReferenceId(assembly), loadContextReferenceId = ReferenceId(context),
                loadContextName = context.Name, loadContextType = context.GetType().FullName,
                isDefault = ReferenceEquals(context, AssemblyLoadContext.Default), isCollectible = context.IsCollectible,
                isBridgeContext = ReferenceEquals(context, bridgeContext), isGameContext = ReferenceEquals(context, gameContext),
                isGodotContext = ReferenceEquals(context, godotContext), loadedAssembly = loaded[index]
            };
        }).ToArray();
        return new
        {
            schema = "e004b-managed-assembly-context-snapshot-v1", phase, processId = System.Environment.ProcessId,
            referenceIdScope = "One process; reference equality IDs remain stable across report phases; not portable handles",
            sameCapturedSnapshot = true,
            contextEnumerationScope = "Only contexts attached to captured assemblies; not AssemblyLoadContext.All",
            anchors, rows
        };
    }
    // These eight complete file identities alone may have one object in each
    // actual C/D context. The table is not a presence requirement or a prefix rule.
    private static readonly Loaded[] FixedFrameworkPairs =
    [
        new("System.Collections.Immutable", "System.Collections.Immutable, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Collections.Immutable.dll", "e34438bd85c4e9dfa2f01ec465e3663252c0ae0b4081b84c23947459890cde75"),
        new("System.Diagnostics.StackTrace", "System.Diagnostics.StackTrace, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Diagnostics.StackTrace.dll", "047650b255b10f26c40468bbcd41b39b4fcb29ed809efb47201fc9b5a0d1e26b"),
        new("System.Memory", "System.Memory, Version=9.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51", false,
            Data + "/System.Memory.dll", "b9c2d68fdd794ecb145a80d55d10d27d8319141e939a6ee0b37efb2429133f8c"),
        new("System.Reflection.Metadata", "System.Reflection.Metadata, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Reflection.Metadata.dll", "3bda9d372f5faa7eea984fda8135251077dfd4e8f9c38e6888195f40b3c3d43d"),
        new("System.Runtime", "System.Runtime, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Runtime.dll", "08df8cbc02d0230f47961b1aedba6d9d49ab17ebc759879287c466a6504fdaa5"),
        new("System.Runtime.InteropServices", "System.Runtime.InteropServices, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Runtime.InteropServices.dll", "fbed1ce11993edb1ccb023df5bfbff0251ebb0332091cd0d5821887b7716a8e2"),
        new("System.Text.Encoding.Extensions", "System.Text.Encoding.Extensions, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Text.Encoding.Extensions.dll", "9801eb0e16f775ba5d8ed87b43438337c7184fbe662450db159ed37e1460b458"),
        new("System.Threading", "System.Threading, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", false,
            Data + "/System.Threading.dll", "8de8a2b4ce2a7c70a39dc0027fcc40450b6e39c3f108865463f2f9297b9fb084")
    ];
    private static (string[] ApprovedDuplicateNames, string[] Violations) InspectAssemblyContract(
        Assembly[] assemblies, Loaded[] loaded, NormalBootConfig.Config normal, Func<Loaded, bool> isAllowed)
    {
        if (assemblies.Length != loaded.Length)
            throw new InvalidDataException("Assembly contract snapshot correspondence mismatch");
        var violations = new List<string>();
        var approved = new List<string>();
        Assembly bridgeAssembly = typeof(Main).Assembly;
        AssemblyLoadContext component = LoadContext(bridgeAssembly);
        AssemblyLoadContext defaults = AssemblyLoadContext.Default;
        bool componentIdentity = !ReferenceEquals(component, defaults) && !component.IsCollectible
            && component.Name == "IsolatedComponentLoadContext(" + Data + "/E003LinuxHostControls.dll)"
            && component.GetType().FullName == "Internal.Runtime.InteropServices.IsolatedComponentLoadContext";
        bool defaultIdentity = !defaults.IsCollectible && defaults.Name == "Default"
            && defaults.GetType().FullName == "System.Runtime.Loader.DefaultAssemblyLoadContext";
        if (!componentIdentity) violations.Add("component_context_contract");
        if (!defaultIdentity) violations.Add("default_context_contract");
        AssemblyLoadContext[] contexts = assemblies.Select(LoadContext).ToArray();
        void CheckAnchor(string role, string name, Assembly expected, AssemblyLoadContext context)
        {
            int[] matches = Enumerable.Range(0, loaded.Length).Where(index => loaded[index].Name == name).ToArray();
            if (matches.Length != 1 || !ReferenceEquals(assemblies[matches[0]], expected)
                || !ReferenceEquals(contexts[matches[0]], context))
                violations.Add("anchor_contract:" + role);
        }
        CheckAnchor("bridge", "E003LinuxHostControls", bridgeAssembly, component);
        CheckAnchor("godot", "GodotSharp", typeof(GodotObject).Assembly, component);
        CheckAnchor("game", "sts2", NormalGameLoader.GameAssembly, component);
        CheckAnchor("corelib", "System.Private.CoreLib", typeof(object).Assembly, defaults);

        var seenAssemblies = new HashSet<Assembly>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var contextNames = new Dictionary<AssemblyLoadContext, HashSet<string>>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        for (int index = 0; index < assemblies.Length; index++)
        {
            Loaded item = loaded[index];
            AssemblyLoadContext context = contexts[index];
            if (!seenAssemblies.Add(assemblies[index])) violations.Add("repeated_assembly_reference:" + index);
            if (!contextNames.TryGetValue(context, out HashSet<string>? names))
                contextNames.Add(context, names = new HashSet<string>(StringComparer.Ordinal));
            if (!names.Add(item.Name)) violations.Add("duplicate_name_within_context:" + index);
            if ((!ReferenceEquals(context, component) && !ReferenceEquals(context, defaults)) || context.IsCollectible)
                violations.Add("row_context_contract:" + index);
            if (!isAllowed(item)) violations.Add("undeclared_assembly:" + index);
            if (normal.GameAssemblies.ContainsKey(item.Name) && !ReferenceEquals(context, component))
                violations.Add("game_outside_component:" + index);
            Loaded? fixedIdentity = FixedFrameworkPairs.FirstOrDefault(expected => expected.Name == item.Name);
            if (fixedIdentity is not null && item != fixedIdentity)
                violations.Add("fixed_framework_identity:" + index);
        }
        foreach (var group in Enumerable.Range(0, loaded.Length).GroupBy(index => loaded[index].Name, StringComparer.Ordinal))
        {
            int[] indices = group.ToArray();
            Loaded? fixedIdentity = FixedFrameworkPairs.FirstOrDefault(expected => expected.Name == group.Key);
            if (fixedIdentity is null)
            {
                if (indices.Length > 1) violations.Add("unapproved_duplicate_name:" + group.Key);
                continue;
            }
            if (indices.Length > 2) violations.Add("fixed_framework_multiplicity:" + group.Key);
            else if (indices.Length == 2)
            {
                int first = indices[0], second = indices[1];
                bool exactPair = componentIdentity && defaultIdentity
                    && !ReferenceEquals(assemblies[first], assemblies[second])
                    && ((ReferenceEquals(contexts[first], component) && ReferenceEquals(contexts[second], defaults))
                        || (ReferenceEquals(contexts[first], defaults) && ReferenceEquals(contexts[second], component)))
                    && indices.All(index => loaded[index] == fixedIdentity && isAllowed(loaded[index]));
                if (exactPair) approved.Add(group.Key);
                else violations.Add("invalid_framework_pair:" + group.Key);
            }
        }
        return (approved.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            violations.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
    [DllImport("libc.so.6", EntryPoint = "gnu_get_libc_version")]
    private static extern IntPtr GetLibcVersion();
    [DllImport("/usr/lib/x86_64-linux-gnu/libicuuc.so.70", EntryPoint = "u_getVersion_70")]
    private static extern void GetIcuVersion([Out] byte[] result);
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string CanonicalSystemPath(string path)
    {
        foreach (string prefix in new[] { "/bin/", "/sbin/", "/lib/", "/lib64/" })
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return "/usr" + path;
        return path;
    }

    internal static int Finish(int controlsExitCode, string reportLeaf = "normal-boot-identity.json")
    {
        var checks = new List<Check>();
        var observations = new Dictionary<string, object?>();
        string? error = null;
        string? processRunId = null;
        bool? gameAssemblyObserved = null;
        bool? packFileObserved = null;
        void Add(string name, bool passed, object? detail) => checks.Add(new(name, passed, detail));
        try
        {
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Work + "/release-host-config.json"), Json)
                ?? throw new InvalidDataException("Missing release host config");
            if (reportLeaf is not "normal-boot-identity.json" and not "normal-boot-final-identity.json")
                throw new InvalidDataException("Unknown normal identity report phase");
            NormalBootConfig.Config normal = NormalBootConfig.Current;
            processRunId = config.ProcessRunId;
            Add("original_47_controls_completed", controlsExitCode == 0, new { controlsExitCode });
            Add("identity_process_and_original_assembly", processRunId == System.Environment.GetEnvironmentVariable("PROCESS_RUN_ID")
                && config.AssemblyOriginalName == "E003LinuxHostControls"
                && typeof(Main).Assembly.GetName().Name == config.AssemblyOriginalName,
                new { config.AssemblyOriginalName, actual = typeof(Main).Assembly.GetName().Name });

            var version = Engine.GetVersionInfo();
            var versionValues = version.ToDictionary(pair => pair.Key.ToString(), pair => new
                { type = pair.Value.VariantType.ToString(), value = pair.Value.ToString() });
            observations["engineVersionInfo"] = versionValues;
            string[] expectedKeys = ["major", "minor", "patch", "hex", "status", "build", "hash", "timestamp", "string"];
            bool versionShape = expectedKeys.All(key => version.ContainsKey(key));
            Add("native_version_numbers", versionShape && (int)version["major"] == 4
                && (int)version["minor"] == 5 && (int)version["patch"] == 1
                && (int)version["hex"] == 0x040501, versionValues);
            // The fixed ELF full-build spelling and upstream 4.5.1 version.h /
            // Engine::get_version_info define different display formats. This is
            // an explicit contract to validate, not proof of the custom build's source.
            Add("native_custom_version_tag", versionShape
                && version["status"].AsString() == "m.14" && version["build"].AsString() == "custom_build"
                && version["string"].AsString() == "4.5.1-m.14 (custom_build)",
                new { actual = versionShape ? version["string"].AsString() : null,
                    expectedStaticElfTag = "4.5.1.m.14.mono.custom_build",
                    expectedApiVersionString = "4.5.1-m.14 (custom_build)",
                    unknownOrDifferentTagMeansFailure = true });
            var expectedFeatures = new Dictionary<string, bool>
            { ["linux"] = true, ["x86_64"] = true, ["64"] = true, ["template"] = true,
                ["template_release"] = true, ["release"] = true, ["dotnet"] = true,
                ["editor"] = false, ["debug"] = false, ["template_debug"] = false,
                ["windows"] = false, ["macos"] = false, ["arm64"] = false };
            var features = expectedFeatures.Keys.ToDictionary(name => name, name => OS.HasFeature(name));
            observations["queriedFeatureTags"] = features;
            observations["featureQueryScope"] = "Predeclared query set; not an enumeration of every possible feature tag";
            Add("release_feature_queries", expectedFeatures.All(pair => features[pair.Key] == pair.Value),
                new { expected = expectedFeatures, observed = features });
            string executable = OS.GetExecutablePath();
            EarlyGuardMarker.Proof early = ManagedBootstrap.ProofAtReady();
            Add("same_manifest_executable", executable == Host + "/SlayTheSpire2"
                && new FileInfo("/proc/self/exe").LinkTarget == executable
                && Hash(executable) == early.DerivedExecutableSha256,
                new { executable, parentSha256 = ParentExeHash, expectedSha256 = early.DerivedExecutableSha256,
                    early.LineageManifestSha256 });
            Add("release_api_hash", typeof(GodotObject).Assembly.Location == Data + "/GodotSharp.dll"
                && Hash(typeof(GodotObject).Assembly.Location) == ApiHash,
                new { location = typeof(GodotObject).Assembly.Location, expectedSha256 = ApiHash });
            string resourceRoot = ProjectSettings.GlobalizePath("res://").TrimEnd('/');
            string currentDirectory = Directory.GetCurrentDirectory();
            object projectProof = ManagedBootstrap.ProjectSettingsProof
                ?? throw new InvalidDataException("Pre-game project contract proof missing");
            using var project = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(projectProof));
            Add("original_project_and_exact_two_key_override", resourceRoot == normal.ExpectedResourceRoot
                && currentDirectory == Work + "/project" && project.RootElement.GetProperty("passed").GetBoolean()
                && ProjectSettings.GetSetting("dotnet/project/assembly_name").AsString() == config.AssemblyOriginalName
                && ProjectSettings.GetSetting("application/run/main_scene").AsString() == "res://scenes/game.tscn"
                && ProjectSettings.GetSetting("autoload/SentryBootstrap").AsString() == "*res://addons/sentry/SentryBootstrap.cs"
                && ProjectSettings.GetSetting("autoload/FmodManager").AsString() == "*res://addons/fmod/FmodManager.gd"
                && ProjectSettings.GetSetting("autoload/" + NormalBootConfig.ObserverName).AsString() == "*" + NormalBootConfig.ObserverScript,
                new { resourceRoot, expectedResourceRoot = normal.ExpectedResourceRoot, currentDirectory,
                    preGameFullTypedComparison = projectProof,
                    comparisonScope = "171 original frames: 169 unchanged public settings, one changed assembly setting, one native-consumed feature frame; not an exact enumeration of private native feature state" });
            string[] forbiddenPaths = [Work + "/project/.godot/mono/publish", Work + "/project/.godot/mono/temp",
                Host + "/SlayTheSpire2.pck", Host + "/data.pck", Host + "/project.godot", "/game/project.godot"];
            string[] presentForbidden = forbiddenPaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
            packFileObserved = File.Exists(normal.Pck.Path);
            Add("original_pack_and_no_alternate_publish", presentForbidden.Length == 0 && packFileObserved is true
                && Hash(normal.Override.Path) == normal.Override.Sha256
                && Hash(normal.OriginalProjectBinary.Path) == normal.OriginalProjectBinary.Sha256
                && new FileInfo(normal.Pck.Path).Length == normal.Pck.Bytes,
                new { presentForbidden, originalPck = normal.Pck, originalOverride = normal.Override,
                    fullPckHashPhase = "NormalBootConfig.LoadAndCheck after 47 and before sts2 managed load",
                    sourcePackReadonly = true, normal.NativeArgs, normal.GodotArgs, normal.UserArgs,
                    embeddedPackProof = "Fixed derived executable plus accepted lineage/ELF checks" });
            string[] published = Directory.GetFiles(Data, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Data, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            bool publishedMatch = published.SequenceEqual(config.PublishedFilesSha256.Keys.OrderBy(path => path, StringComparer.Ordinal))
                && config.PublishedFilesSha256.All(pair => Hash(Path.Combine(Data, pair.Key)) == pair.Value);
            Add("complete_publish_hashes", publishedMatch, new { fileCount = published.Length, published });

            observations["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            Add("fixed_coreclr_runtime", System.Environment.Version.ToString() == "9.0.7", new { actual = System.Environment.Version.ToString(), expected = "9.0.7" });

            var userland = config.Userland;
            observations["userlandManifestSha256"] = userland.ManifestSha256;
            observations["userlandArchives"] = userland.Archives;
            string? libcVersion = Marshal.PtrToStringAnsi(GetLibcVersion());
            Add("userland_libc_version", libcVersion == "2.35", new { actual = libcVersion, expected = "2.35" });
            byte[] icuVersion = new byte[4];
            GetIcuVersion(icuVersion);
            string decimalSeparator = CultureInfo.GetCultureInfo("fr-FR").NumberFormat.NumberDecimalSeparator;
            bool invariantSwitchSpecified = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariantSwitch);
            string? invariantEnvironment = System.Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
            Add("userland_icu_globalization", icuVersion[0] == 70 && icuVersion[1] == 1
                && decimalSeparator == "," && !invariantSwitch
                && invariantEnvironment is not "1" && !string.Equals(invariantEnvironment, "true", StringComparison.OrdinalIgnoreCase),
                new { icuVersion = icuVersion.Select(value => (int)value).ToArray(), culture = "fr-FR",
                    decimalSeparator, invariantSwitchSpecified, invariantSwitch, invariantEnvironment });
            var dependencyFiles = userland.RequiredDependencies.Select(pair => new
            {
                path = pair.Key, expected = pair.Value, present = File.Exists(pair.Key),
                actualSha256 = File.Exists(pair.Key) ? Hash(pair.Key) : null,
                actualBytes = File.Exists(pair.Key) ? new FileInfo(pair.Key).Length : -1,
                actualMode = File.Exists(pair.Key) && OperatingSystem.IsLinux() ? (int)File.GetUnixFileMode(pair.Key) : -1
            }).ToArray();
            Add("userland_required_dependencies", dependencyFiles.Length == 32 && dependencyFiles.All(item => item.present
                && item.actualSha256 == item.expected.Sha256 && item.actualBytes == item.expected.Bytes
                && item.actualMode == item.expected.Mode), dependencyFiles);
            string[] mappedPaths = File.ReadAllLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length == 6 && fields[5].StartsWith('/'))
                .Select(fields => fields[5]).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            observations["mappedPaths"] = mappedPaths;
            var systemMappings = mappedPaths.Where(path => CanonicalSystemPath(path).StartsWith("/usr/", StringComparison.Ordinal))
                .Select(path => new { path, canonical = CanonicalSystemPath(path), actualSha256 = Hash(path) }).ToArray();
            EarlyGuardMarker.GuardProof guard = EarlyGuardMarker.InspectGuardAtReady(early);
            string[] unexpectedLibraries = mappedPaths.Where(path => (path.Contains(".so", StringComparison.Ordinal)
                || path.Contains("ld-linux", StringComparison.Ordinal))
                && !CanonicalSystemPath(path).StartsWith("/usr/", StringComparison.Ordinal)
                && !path.StartsWith(Data + "/", StringComparison.Ordinal)
                && path != guard.Path && !normal.AllowedNativeFiles.ContainsKey(path)).ToArray();
            var gameNativeMappings = mappedPaths.Where(normal.AllowedNativeFiles.ContainsKey).Select(path => new
            { path, actualSha256 = Hash(path), expected = normal.AllowedNativeFiles[path] }).ToArray();
            bool mappedSources = gameNativeMappings.All(item => item.actualSha256 == item.expected.Sha256)
                && systemMappings.All(item => userland.FilesSha256.TryGetValue(item.canonical, out string? expected)
                && item.actualSha256 == expected) && unexpectedLibraries.Length == 0;
            bool loaderMapped = systemMappings.Any(item => item.canonical == "/usr/lib64/ld-linux-x86-64.so.2"
                || item.canonical == "/usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2");
            bool libcMapped = systemMappings.Any(item => item.canonical == "/usr/lib/x86_64-linux-gnu/libc.so.6");
            bool icuMapped = systemMappings.Any(item => item.canonical == "/usr/lib/x86_64-linux-gnu/libicuuc.so.70");
            Add("userland_mapped_library_sources", mappedSources && loaderMapped && libcMapped && icuMapped,
                new { systemMappings, gameNativeMappings, unexpectedLibraries, loaderMapped, libcMapped, icuMapped, guard });

            Assembly[] assemblySnapshot = AppDomain.CurrentDomain.GetAssemblies();
            var assemblyRows = assemblySnapshot.Select((assembly, snapshotIndex) => new
                {
                    Assembly = assembly, SnapshotIndex = snapshotIndex,
                    Loaded = new Loaded(assembly.GetName().Name ?? "", assembly.FullName ?? "", assembly.IsDynamic,
                        assembly.IsDynamic ? "" : assembly.Location,
                        assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) ? null : Hash(assembly.Location))
                }).OrderBy(row => row.Loaded.Name, StringComparer.Ordinal).ToArray();
            Loaded[] loaded = assemblyRows.Select(row => row.Loaded).ToArray();
            observations["loadedAssemblies"] = loaded;
            observations["loadedAssemblyContexts"] = CaptureAssemblyContexts(
                assemblyRows.Select(row => row.Assembly).ToArray(), loaded,
                assemblyRows.Select(row => row.SnapshotIndex).ToArray(), reportLeaf);
            gameAssemblyObserved = loaded.Any(item => item.Name.Equals("sts2", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("MegaCrit", StringComparison.OrdinalIgnoreCase));
            bool IsAllowed(Loaded item)
            {
                if (item.Dynamic || item.Sha256 is null) return false;
                if (normal.GameAssemblies.TryGetValue(item.Name, out NormalBootConfig.ManagedFile? gameFile))
                    return item.Location == gameFile.Path && item.Sha256 == gameFile.Sha256 && item.FullName == gameFile.FullName;
                return config.AllowedAssemblyNames.Contains(item.Name, StringComparer.Ordinal)
                    && item.Location.StartsWith(Data + "/", StringComparison.Ordinal)
                    && config.PublishedFilesSha256.TryGetValue(Path.GetRelativePath(Data, item.Location), out string? expected)
                    && item.Sha256 == expected;
            }
            Loaded[] unknown = loaded.Where(item => !IsAllowed(item)).ToArray();
            string[] duplicateNames = loaded.GroupBy(item => item.Name, StringComparer.Ordinal).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToArray();
            var assemblyContract = InspectAssemblyContract(assemblyRows.Select(row => row.Assembly).ToArray(), loaded, normal, IsAllowed);
            Add("only_declared_bridge_game_assemblies", unknown.Length == 0 && assemblyContract.Violations.Length == 0
                && gameAssemblyObserved is true, new { unknown, duplicateNames,
                    contract = "fixed_eight_framework_default_component_pairs_v1",
                    approvedDuplicateNames = assemblyContract.ApprovedDuplicateNames, violations = assemblyContract.Violations,
                    allowedBridgeNames = config.AllowedAssemblyNames, originalGameNames = normal.GameAssemblies.Keys });
            object gameProof = NormalGameLoader.Snapshot();
            Add("original_game_actual_alc_and_shared_godot", ReferenceEquals(
                AssemblyLoadContext.GetLoadContext(NormalGameLoader.GameAssembly), AssemblyLoadContext.GetLoadContext(typeof(Main).Assembly))
                && loaded.Count(item => item.Name == "GodotSharp") == 1, gameProof);
            Add("original_709_metadata_and_single_lookup", NormalGameLoader.LookupCount == 1,
                new { originalScriptMetadataCount = 709, originalLookupInvocationCount = NormalGameLoader.LookupCount,
                    detailedMetadataEvidence = "/work/normal-game-loader.json", nativeScriptBehaviorScope = "observed original nodes only" });
            Add("original_profile2_input_provenance", NormalBootConfig.InputBefore is not null
                && NormalBootConfig.Current.InputFiles.Count == 4,
                new { NormalBootConfig.InputBefore, startupReadSaveResult = "not_directly_observed",
                    fullProgressMatch = "HQ_comparison_pending", observationPhase = reportLeaf });
        }
        catch (Exception exception)
        { error = exception.ToString(); }
        bool passed = error is null && checks.Count == ExpectedChecks.Length
            && new HashSet<string>(ExpectedChecks, StringComparer.Ordinal).SetEquals(checks.Select(c => c.Name)) && checks.All(check => check.Passed);
        int exitCode = controlsExitCode != 0 ? controlsExitCode : passed ? 0 : 46;
        try
        {
            var report = new { schema = "e004b-normal-boot-identity-v1", processRunId, passed, exitCode,
                originalControlsExitCode = controlsExitCode, gameAssemblyObserved, packFileObserved,
                observations, checks, error, phase = reportLeaf, nativeGameCodeMayHaveLoaded = true,
                newNormalBootContract = true, inheritedNoGameAcceptance = false };
            using var stream = new FileStream(Work + "/" + reportLeaf, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, report, Json);
            stream.Flush(true);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "normal_boot_identity", phase = reportLeaf, passed, exitCode }));
        }
        catch (Exception exception)
        { Console.Error.WriteLine(exception); return controlsExitCode != 0 ? controlsExitCode : 47; }
        return exitCode;
    }
}
