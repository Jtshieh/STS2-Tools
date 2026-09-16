using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Godot;
using Godot.Bridge;

// Only metadata/load routing lives here. No original initializer, lifecycle, ModelDb or save init call.
internal static class NormalGameLoader
{
    private static AssemblyLoadContext? bridgeContext;
    private static Assembly? gameAssembly;
    private static int installationCount, gameLookupCount;
    private static readonly object Sync = new();
    private static readonly List<object> resolutions = new();
    private static object? metadataProof;
    internal static Assembly GameAssembly => gameAssembly ?? throw new InvalidOperationException("Game assembly not loaded");
    internal static int LookupCount => gameLookupCount;
    internal static bool LoadAttempted { get; private set; }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) => ManagedBootstrap.Require(condition, message);
    private static void Record(object value)
    {
        lock (Sync) resolutions.Add(value);
        ManagedBootstrap.RecordEvent("normal_dependency_resolution", value);
    }

    internal static object LoadAndRegister(NormalBootConfig.Config config)
    {
        Require(Interlocked.Increment(ref installationCount) == 1 && gameAssembly is null && gameLookupCount == 0,
            "Normal game loader installation repeated");
        Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "sts2"),
            "sts2 managed assembly was loaded before the complete 47-control boundary");
        Assembly bridge = typeof(GodotPlugins.Game.Main).Assembly;
        Assembly sharedGodot = typeof(GodotObject).Assembly;
        bridgeContext = AssemblyLoadContext.GetLoadContext(bridge)
            ?? throw new InvalidDataException("Bridge has no actual AssemblyLoadContext");
        Require(!config.GameAssemblies.Keys.Any(name => bridgeContext.Assemblies.Any(a => a.GetName().Name == name)),
            "An original game dependency was already loaded before the game loader");
        Require(sharedGodot.Location == NormalBootConfig.Data + "/GodotSharp.dll"
            && ManagedBootstrap.HashFile(sharedGodot.Location) == NormalBootConfig.GodotSharpSha256,
            "Bridge GodotSharp location/hash differs from the accepted release API");
        // Static fields and these event delegates remain rooted for the whole process. Never dispose/unsubscribe.
        bridgeContext.Resolving += ResolveManaged;
        bridgeContext.ResolvingUnmanagedDll += ResolveNative;
        ManagedBootstrap.RecordEvent("normal_game_resolvers_installed", new
        {
            bridgeAssembly = bridge.FullName, bridgePath = bridge.Location,
            bridgeContext = Context(bridgeContext), sharedGodotAssembly = sharedGodot.FullName,
            sharedGodotPath = sharedGodot.Location, resolverLifetime = "whole_process",
            gameManagedAssemblyLoaded = false, nativeGameCodeMayHaveLoaded = true,
            sentryModuleInitializerTiming = "unknown_not_forced"
        });
        LoadAttempted = true;
        ManagedBootstrap.RecordEvent("normal_game_managed_load_starting", new
        { gameManagedAssemblyLoadingAttempted = true, file = config.GameAssemblies["sts2"] });
        Assembly loaded = LoadPinned("sts2", config.GameAssemblies["sts2"]);
        gameAssembly = loaded;
        Require(ReferenceEquals(AssemblyLoadContext.GetLoadContext(loaded), bridgeContext),
            "Original game was not loaded into the actual bridge context");
        // Assembly bytes/names alone do not prove Godot type identity. Walk the actual NGame base chain.
        Type gameType = loaded.GetType("MegaCrit.Sts2.Core.Nodes.NGame", throwOnError: true)!;
        Type? baseType = gameType;
        while (baseType is not null && baseType.FullName != typeof(GodotObject).FullName) baseType = baseType.BaseType;
        Require(baseType is not null && baseType == typeof(GodotObject) && ReferenceEquals(baseType.Assembly, sharedGodot),
            "Original NGame does not share the bridge's actual GodotObject/GodotSharp object");
        Assembly[] allGodot = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "GodotSharp").ToArray();
        Require(allGodot.Length == 1 && ReferenceEquals(allGodot[0], sharedGodot), "A second GodotSharp Assembly object exists");

        metadataProof = CheckOriginalScriptMetadata(loaded, config.OriginalScriptMap);
        Require(gameLookupCount == 0, "Original game script lookup was already attempted");
        Interlocked.Increment(ref gameLookupCount); // Count attempts too; failure cannot be retried.
        ScriptManagerBridge.LookupScriptsInAssembly(loaded);
        object report = Snapshot();
        ManagedBootstrap.NewReport("normal-game-loader.json", new
        {
            schema = "e004b-normal-game-loader-v1", processRunId = config.ProcessRunId,
            gameManagedAssemblyLoaded = true, nativeGameCodeMayHaveLoaded = true,
            originalLookupReturned = true, originalScriptMetadataCount = 709,
            originalLookupInvocationCount = gameLookupCount, report,
            limitation = "Lookup returned once after all 709 original path/type pairs matched metadata. Actual native use is separately checked on real original nodes; this is not 709 behavior tests."
        });
        return report;
    }

    private static object Context(AssemblyLoadContext context) => new
    { context.Name, context.IsCollectible, isDefault = ReferenceEquals(context, AssemblyLoadContext.Default) };

    private static Assembly LoadPinned(string simpleName, NormalBootConfig.ManagedFile file)
    {
        AssemblyLoadContext context = bridgeContext ?? throw new InvalidOperationException("Resolver context not installed");
        NormalBootConfig.Inspect(new(file.Path, file.Sha256, file.Bytes), true);
        AssemblyName diskName = AssemblyName.GetAssemblyName(file.Path); // Metadata identity read, no module ctor.
        Require(diskName.Name == simpleName && diskName.FullName == file.FullName,
            "Game dependency manifest identity does not match its actual metadata: " + simpleName);
        Assembly assembly = context.LoadFromAssemblyPath(file.Path);
        Require(assembly.FullName == file.FullName && assembly.Location == file.Path
            && ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), context),
            "A game dependency unified to an unexpected file/context: " + simpleName);
        return assembly;
    }

    private static Assembly? ResolveManaged(AssemblyLoadContext context, AssemblyName requested)
    {
        try
        {
            Require(ReferenceEquals(context, bridgeContext), "Managed resolver called for another context");
            string name = requested.Name ?? throw new InvalidDataException("Unnamed managed dependency");
            if (name == "GodotSharp")
            {
                Assembly shared = typeof(GodotObject).Assembly;
                Require(AssemblyName.ReferenceMatchesDefinition(requested, shared.GetName()),
                    "Original game requested a different GodotSharp identity");
                Record(new { kind = "managed_shared_godot", requested = requested.FullName, actual = shared.FullName, shared.Location });
                return shared;
            }
            Require(NormalBootConfig.Current.GameAssemblies.TryGetValue(name, out NormalBootConfig.ManagedFile? file),
                "Unresolved managed dependency absent from the original game manifest: " + requested.FullName);
            Require(new AssemblyName(file!.FullName).FullName == requested.FullName,
                "Original game requested a different dependency identity: " + requested.FullName);
            Assembly loaded = LoadPinned(name, file);
            Record(new { kind = "managed_game", requested = requested.FullName, loaded.FullName, loaded.Location });
            return loaded;
        }
        catch (Exception error)
        {
            ManagedBootstrap.RecordEvent("normal_managed_resolution_failed", new { requested = requested.FullName, error = error.ToString() });
            throw;
        }
    }

    private static IntPtr ResolveNative(Assembly requestingAssembly, string name)
    {
        try
        {
            Require(ReferenceEquals(AssemblyLoadContext.GetLoadContext(requestingAssembly), bridgeContext),
                "Native resolver called for an assembly in another context");
            Require(NormalBootConfig.Current.NativeLibraryAliases.TryGetValue(name, out NormalBootConfig.PinnedFile? file),
                "Unresolved native dependency absent from the reviewed alias map: " + name);
            NormalBootConfig.Inspect(file!, true);
            IntPtr handle = NativeLibrary.Load(file!.Path);
            Require(handle != IntPtr.Zero, "Pinned native load returned no handle: " + name);
            Record(new { kind = "native_last_resolver", requested = name, requestingAssembly = requestingAssembly.FullName, file });
            return handle;
        }
        catch (Exception error)
        {
            ManagedBootstrap.RecordEvent("normal_native_resolution_failed", new { requested = name,
                requestingAssembly = requestingAssembly.FullName, error = error.ToString() });
            throw;
        }
    }

    private static object CheckOriginalScriptMetadata(Assembly assembly, NormalBootConfig.PinnedFile mapFile)
    {
        RawFiles.Result map = NormalBootConfig.Inspect(mapFile, true, true);
        using var document = JsonDocument.Parse(map.Bytes!);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("schema").GetString() == "e004b-original-script-map-v1"
            && root.GetProperty("gameSha256").GetString() == NormalBootConfig.GameSha256,
            "Original script map is for another game identity");
        var expected = root.GetProperty("scripts").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? throw new InvalidDataException("Null script path"), StringComparer.Ordinal);
        CustomAttributeData[] assemblyAttributes = assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Godot.AssemblyHasScriptsAttribute").ToArray();
        Require(assemblyAttributes.Length == 1 && assemblyAttributes[0].ConstructorArguments.Count == 1
            && assemblyAttributes[0].ConstructorArguments[0].Value is IEnumerable<CustomAttributeTypedArgument>,
            "Original AssemblyHasScripts metadata does not contain the fixed explicit type array");
        var arguments = (IEnumerable<CustomAttributeTypedArgument>)assemblyAttributes[0].ConstructorArguments[0].Value!;
        Type[] types = arguments.Select(a => a.Value as Type ?? throw new InvalidDataException("Script metadata item is not a Type")).ToArray();
        Require(types.Length == 709 && expected.Count == 709 && types.Distinct().Count() == 709
            && expected.Values.Distinct(StringComparer.Ordinal).Count() == 709, "Original 709 script mapping is incomplete or duplicate");
        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Type type in types)
        {
            Require(ReferenceEquals(type.Assembly, assembly), "Original script type points outside original sts2");
            string name = type.FullName ?? throw new InvalidDataException("Unnamed original script type");
            CustomAttributeData[] paths = type.GetCustomAttributesData()
                .Where(a => a.AttributeType.FullName == "Godot.ScriptPathAttribute").ToArray();
            Require(paths.Length == 1 && paths[0].ConstructorArguments.Count == 1
                && paths[0].ConstructorArguments[0].Value is string, "Original script lacks one exact path: " + name);
            string path = (string)paths[0].ConstructorArguments[0].Value!;
            Require(expected.GetValueOrDefault(name) == path && path != NormalBootConfig.ObserverScript,
                "Original path/type differs or conflicts with observer: " + name);
            observed.Add(name, path);
        }
        CustomAttributeData[] ownPaths = typeof(Main).GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Godot.ScriptPathAttribute").ToArray();
        Require(ownPaths.Length == 1 && ownPaths[0].ConstructorArguments.Count == 1
            && ownPaths[0].ConstructorArguments[0].Value as string == NormalBootConfig.ObserverScript,
            "Generated observer ScriptPath differs from the sole permitted new autoload path");
        return new { expectedCount = 709, observedCount = observed.Count, map.Sha256,
            observerPath = NormalBootConfig.ObserverScript, exactOriginalMapMatched = true,
            attributeConstructorsInvokedForValidation = false, scripts = observed };
    }

    internal static object Snapshot()
    {
        Assembly assembly = GameAssembly;
        AssemblyLoadContext context = bridgeContext ?? throw new InvalidOperationException("Missing bridge context");
        object[] resolutionSnapshot;
        lock (Sync) resolutionSnapshot = resolutions.ToArray();
        return new
        {
            assembly.FullName, assembly.Location, sha256 = ManagedBootstrap.HashFile(assembly.Location),
            mvid = assembly.ManifestModule.ModuleVersionId, context = Context(context),
            sameBridgeContext = ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly),
                AssemblyLoadContext.GetLoadContext(typeof(GodotPlugins.Game.Main).Assembly)),
            godotSharpAssemblyObjectCount = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "GodotSharp"),
            installationCount, gameLookupCount, LoadAttempted, resolverLifetime = "whole_process",
            sentryModuleInitializerTiming = "unknown_not_forced", metadataProof, resolutions = resolutionSnapshot
        };
    }
}
