using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

internal static class NormalBootConfig
{
    internal const string Work = "/work";
    internal const string Data = Work + "/native-host/data_E003LinuxHostControls_linuxbsd_x86_64";
    internal const string BridgeName = "E003LinuxHostControls";
    internal const string ObserverName = "E004NormalBootObserver";
    internal const string ObserverScript = "res://Main.cs";
    internal const string GameSha256 = "2b40d2df538db1ceb5fa48d958c80ab730ada1e07db88a870aff01a661768b9f";
    internal const string GodotSharpSha256 = "0e4897ecdfb31456a97c7d8028dfb8d7dbdc632e2f73fc9b438d7b266a139289";
    internal sealed record PinnedFile(string Path, string Sha256, long Bytes);
    internal sealed record ManagedFile(string Path, string Sha256, long Bytes, string FullName);
    internal sealed record Config(string Schema, string ProcessRunId, string ExpectedResourceRoot,
        string[] NativeArgs, string[] GodotArgs, string[] UserArgs, PinnedFile Pck, PinnedFile Override,
        PinnedFile OriginalProjectBinary, PinnedFile OriginalScriptMap,
        Dictionary<string, ManagedFile> GameAssemblies, PinnedFile SharedGodotSharp,
        Dictionary<string, PinnedFile> NativeLibraryAliases, Dictionary<string, PinnedFile> AllowedNativeFiles,
        string ExpectedUserDataPath, Dictionary<string, PinnedFile> InputFiles,
        string ObservedStdoutPath, string ObservedStderrPath, int ObserverTimeoutSeconds, int ProfileId);
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true
    };
    private static Config? current;
    internal static Config Current => current ?? throw new InvalidOperationException("Normal boot config not checked");
    internal static string ConfigSha256 { get; private set; } = "";
    internal static object? InputBefore { get; private set; }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) => ManagedBootstrap.Require(condition, message);

    internal static RawFiles.Result Inspect(PinnedFile file, bool immutable, bool retainBytes = false)
    {
        Require(file.Path.StartsWith('/') && Path.GetFullPath(file.Path) == file.Path
            && file.Sha256.Length == 64 && file.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && file.Bytes > 0, "Unpinned or noncanonical input path: " + file.Path);
        string parent = Path.GetDirectoryName(file.Path) ?? throw new InvalidDataException("Input has no directory");
        string cursor = parent;
        while (cursor != "/")
        {
            Require(new DirectoryInfo(cursor).LinkTarget is null, "Symlink ancestor in input path: " + cursor);
            cursor = Path.GetDirectoryName(cursor) ?? "/";
        }
        RawFiles.Result result = RawFiles.Inspect(parent, Path.GetFileName(file.Path), file.Bytes,
            null, immutable, false, retainBytes);
        Require(result.Stat.Size == file.Bytes && result.Sha256 == file.Sha256,
            "Input bytes/hash mismatch: " + file.Path);
        return result;
    }

    internal static Config LoadAndCheck()
    {
        Require(current is null, "Normal boot configuration may be checked only once");
        RawFiles.Result input = RawFiles.Inspect(Work, "normal-boot-config.json", 1048576,
            0x180, false, true, true);
        Config config = JsonSerializer.Deserialize<Config>(input.Bytes!, Json)
            ?? throw new InvalidDataException("Empty normal boot config");
        Require(config.Schema == "e004b-normal-boot-config-v1"
            && config.ProcessRunId == System.Environment.GetEnvironmentVariable("PROCESS_RUN_ID"),
            "Normal boot schema/process identity mismatch");
        Require(config.ExpectedResourceRoot is not null && Directory.GetCurrentDirectory() == Work + "/project",
            "Normal boot requires the declared working directory");
        Require(config.Pck.Path == "/game/SlayTheSpire2.pck" && config.Override.Path == "/game/override.cfg"
            && config.OriginalProjectBinary.Path == "/game/expected-project.binary"
            && config.OriginalScriptMap.Path == "/game/expected-script-map.json"
            && config.OriginalProjectBinary.Sha256 == "88bc2e4f6a2627204b20a714cb8746a269c009e48255dcfc7a0eaef9503d2227"
            && config.OriginalProjectBinary.Bytes == 24693,
            "Normal boot immutable project/PCK paths differ from the reviewed layout");
        Require(config.ExpectedUserDataPath == Work + "/xdg-data/SlayTheSpire2"
            && OS.GetUserDataDir().TrimEnd('/') == config.ExpectedUserDataPath
            && ProjectSettings.GlobalizePath("user://").TrimEnd('/') == config.ExpectedUserDataPath,
            "Original project user data path differs from its isolated expected path");
        Require(config.ObservedStdoutPath == Work + "/normal-game.stdout.log"
            && config.ObservedStderrPath == Work + "/normal-game.stderr.log"
            && config.ObserverTimeoutSeconds == 1150,
            "Only the explicit 150 second observation/log contract is accepted");
        Require(OS.GetCmdlineArgs().SequenceEqual(config.GodotArgs, StringComparer.Ordinal)
            && OS.GetCmdlineUserArgs().SequenceEqual(config.UserArgs, StringComparer.Ordinal),
            "Actual Godot argument vectors differ from the declared vectors");
        byte[] rawCommandLine = File.ReadAllBytes("/proc/self/cmdline");
        Require(rawCommandLine.Length is > 0 and <= 65536 && rawCommandLine[^1] == 0,
            "Unexpected native command line shape");
        string[] nativeArgs = new System.Text.UTF8Encoding(false, true).GetString(rawCommandLine)
            .Split('\0')[..^1];
        Require(nativeArgs.SequenceEqual(config.NativeArgs, StringComparer.Ordinal)
            && nativeArgs[0] == Work + "/native-host/SlayTheSpire2", "Actual native argv differs from the launch contract");
        int separator = Array.IndexOf(nativeArgs, "--"), mainPack = Array.IndexOf(nativeArgs, "--main-pack");
        int forceSteam = Array.IndexOf(nativeArgs, "--force-steam=off");
        Require(separator > 0 && mainPack > 0 && mainPack + 1 < separator
            && nativeArgs[mainPack + 1] == config.Pck.Path
            && nativeArgs.Count(arg => arg == "--main-pack") == 1
            && forceSteam > 0 && forceSteam < separator
            && config.GodotArgs.SequenceEqual(new[] { "--force-steam=off" }, StringComparer.Ordinal)
            && !config.GodotArgs.Any(arg => arg is "--script" or "-s" or "--editor" or "-e"
                || arg.StartsWith("--force-steam=", StringComparison.Ordinal) && arg != "--force-steam=off")
            && !config.UserArgs.Any(arg => arg.StartsWith("--force-steam", StringComparison.Ordinal))
            && config.UserArgs.Contains("--e003-linux-host-controls", StringComparer.Ordinal)
            && config.UserArgs.Contains("--config=/work/control-config.json", StringComparer.Ordinal),
            "The normal game/platform arguments are absent, duplicated or overridden");
        Inspect(config.Pck, true); Inspect(config.Override, true); Inspect(config.OriginalProjectBinary, true);
        Inspect(config.OriginalScriptMap, true); Inspect(config.SharedGodotSharp, true);
        Require(config.SharedGodotSharp.Path == "/game/data_sts2_linuxbsd_x86_64/GodotSharp.dll"
            && config.SharedGodotSharp.Sha256 == GodotSharpSha256,
            "Original game GodotSharp must be a pinned byte-identical reference, never a second loaded copy");
        Require(config.GameAssemblies.Count > 0 && config.GameAssemblies.ContainsKey("sts2")
            && !config.GameAssemblies.ContainsKey("GodotSharp")
            && !config.GameAssemblies.ContainsKey(BridgeName), "Game assembly allowlist has missing/conflicting identities");
        foreach (var item in config.GameAssemblies)
        {
            Require(item.Value.Path == "/game/data_sts2_linuxbsd_x86_64/" + item.Key + ".dll",
                "Game managed path is not the original fixed layout: " + item.Key);
            Inspect(new(item.Value.Path, item.Value.Sha256, item.Value.Bytes), true);
        }
        Require(config.GameAssemblies["sts2"].Sha256 == GameSha256
            && config.GameAssemblies["sts2"].Bytes == 9756160
            && config.GameAssemblies["sts2"].FullName == "sts2, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null",
            "Original sts2 identity changed");
        foreach (var pair in config.AllowedNativeFiles)
        {
            Require(pair.Key == pair.Value.Path && (pair.Key.StartsWith("/game/", StringComparison.Ordinal)
                || pair.Key.StartsWith(Work + "/native-host/", StringComparison.Ordinal)), "Native file is outside reviewed game roots");
            Inspect(pair.Value, true);
        }
        foreach (var pair in config.NativeLibraryAliases)
            Require(pair.Key.Length > 0 && config.AllowedNativeFiles.TryGetValue(pair.Value.Path, out PinnedFile? allowed)
                && pair.Value == allowed, "Native resolver alias lacks an exact allowed file: " + pair.Key);
        string account = config.ExpectedUserDataPath + "/default/1/";
        Require(config.ProfileId is >= 1 and <= 3, "Unsupported profile id");
        string prefix = $"profile{config.ProfileId}/saves/";
        var paths = new List<string> {"profile.save", prefix + "prefs.save", prefix + "progress.save"};
        if (config.InputFiles.ContainsKey(prefix + "current_run.save")) paths.Add(prefix + "current_run.save");
        Require(config.InputFiles.Count == paths.Count && new HashSet<string>(paths, StringComparer.Ordinal).SetEquals(config.InputFiles.Keys),
            "Exactly the three original profile2 inputs are required");
        var files = new List<object>();
        foreach (string path in paths)
        {
            PinnedFile file = config.InputFiles[path];
            Require(file.Path == account + path, "Original input path changed: " + path);
            RawFiles.Result proof = Inspect(file, false);
            files.Add(new { key = path, file, actual = proof.Stat, actualSha256 = proof.Sha256 });
        }
        Require(!File.Exists(account + "settings.save"), "Settings must initially be absent; no prefilled disclaimer flag");
        // Validate the existing selector only; do not call a profile initializer or synthesize unlocks.
        using var selector = JsonDocument.Parse(File.ReadAllBytes(account + "profile.save"));
        Require(selector.RootElement.GetProperty("last_profile_id").GetInt32() == config.ProfileId
            && selector.RootElement.GetProperty("schema_version").GetInt32() == 2,
            "The original profile selector must choose profile 2 automatically");
        InputBefore = new { files, settingsInitiallyAbsent = true, accountRoot = account };
        ConfigSha256 = input.Sha256;
        current = config;
        return config;
    }
}
