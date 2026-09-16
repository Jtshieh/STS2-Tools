using System.Reflection;

// Exact public members of the fixed sts2 PE. No setters, private fields, constructors or init methods.
internal static class NormalGameRead
{
    private sealed record Api(string Owner, string Name, bool Static);
    private static readonly Dictionary<int, Api> Allowed = new()
    {
        [0x06005a76] = new("Nodes.NGame", "get_Instance", true),
        [0x06005a78] = new("Nodes.NGame", "get_RootSceneContainer", false),
        [0x06005a7d] = new("Nodes.NGame", "get_CurrentRunNode", false),
        [0x06005a7f] = new("Nodes.NGame", "get_Transition", false),
        [0x06005a99] = new("Nodes.NGame", "get_GameStartupComplete", false),
        [0x06005b25] = new("Nodes.NSceneContainer", "get_CurrentScene", false),
        [0x06005b31] = new("Nodes.NTransition", "get_InTransition", false),
        [0x06008741] = new("Nodes.CommonUi.NModalContainer", "get_Instance", true),
        [0x06008743] = new("Nodes.CommonUi.NModalContainer", "get_OpenModal", false),
        [0x0600071d] = new("Saves.SaveManager", "get_Instance", true),
        [0x0600071f] = new("Saves.SaveManager", "get_SettingsSave", false),
        [0x06000720] = new("Saves.SaveManager", "get_PrefsSave", false),
        [0x06000721] = new("Saves.SaveManager", "get_IsPrefsLoaded", false),
        [0x06000722] = new("Saves.SaveManager", "get_Progress", false),
        [0x06000726] = new("Saves.SaveManager", "get_IsProfileInitialized", false),
        [0x06000727] = new("Saves.SaveManager", "get_CurrentProfileId", false),
        [0x06000735] = new("Saves.SaveManager", "GetProfileScopedPath", false),
        [0x060003ea] = new("TestSupport.TestMode", "get_IsOn", true),
        [0x06001037] = new("Platform.Steam.SteamInitializer", "get_Initialized", true),
        [0x0600101b] = new("Platform.PlatformUtil", "get_PrimaryPlatform", true),
        [0x0600101f] = new("Platform.PlatformUtil", "GetLocalPlayerId", true),
        [0x0600086b] = new("Saves.UserDataPathProvider", "get_IsRunningModded", true),
        [0x0600086f] = new("Saves.UserDataPathProvider", "GetAccountScopedBasePath", true),
        [0x06000a67] = new("Saves.Managers.ProfileSaveManager", "GetProfileSavePath", true),
        [0x06000a6e] = new("Saves.Managers.ProgressSaveManager", "GetProgressPathForProfile", true),
        [0x06000a61] = new("Saves.Managers.PrefsSaveManager", "GetPrefsPath", true),
        [0x06004957] = new("Modding.ModManager", "get_State", true),
        [0x06004978] = new("Modding.ModManager", "GetLoadedMods", true),
        [0x060006d9] = new("Saves.ProgressState", "ToSerializable", false),
        [0x060004cb] = new("Saves.JsonSerializationUtility", "ToJson", true)
    };
    private static readonly Dictionary<int, MethodInfo> Methods = new();

    private static MethodInfo Method(int token)
    {
        if (Methods.TryGetValue(token, out MethodInfo? method)) return method;
        if (!Allowed.TryGetValue(token, out Api? contract)) throw new InvalidOperationException("API not in the public observation allowlist");
        method = NormalGameLoader.GameAssembly.ManifestModule.ResolveMethod(token) as MethodInfo
            ?? throw new MissingMethodException("Original token is not a method: " + token.ToString("x8"));
        ManagedBootstrap.Require(method.IsPublic && method.IsStatic == contract.Static
            && method.Name == contract.Name && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core." + contract.Owner,
            "Pinned public API metadata differs: " + token.ToString("x8"));
        Methods.Add(token, method);
        return method;
    }

    internal static object? Call(int token, object? instance, params object?[] args)
    {
        MethodInfo method = Method(token);
        ManagedBootstrap.Require(method.IsStatic == (instance is null), "Public observation API instance/static mismatch");
        return method.Invoke(instance, args);
    }

    internal static string OfficialJson(object value)
    {
        MethodInfo method = Method(0x060004cb);
        ManagedBootstrap.Require(method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1,
            "Official JSON serializer generic signature changed");
        return method.MakeGenericMethod(value.GetType()).Invoke(null, new[] { value }) as string
            ?? throw new InvalidDataException("Official JSON serializer returned no string");
    }

    internal static object ObservedApiEvidence() => Methods.OrderBy(pair => pair.Key).Select(pair => new
    { token = "0x" + pair.Key.ToString("x8"), owner = pair.Value.DeclaringType?.FullName,
        pair.Value.Name, pair.Value.IsPublic, pair.Value.IsStatic }).ToArray();
}
