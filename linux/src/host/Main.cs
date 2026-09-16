using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Godot;
using File = System.IO.File;

// The sole new autoload is created by the original native autoload pass, after SentryBootstrap/FmodManager.
public partial class Main : Node
{
    private const string GameType = "MegaCrit.Sts2.Core.Nodes.NGame";
    private const string MenuType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string LogoType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NLogoAnimation";
    private const string EaType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NEarlyAccessDisclaimer";
    private readonly Stopwatch elapsed = new();
    private readonly NormalBootLogs logs = new();
    private static readonly JsonSerializerOptions JournalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private bool entered, ready, finished;
    private double untilSample;
    private int observationSequence;
    private int platformBranchObservationCount;
    private object? lastState, saveManager;
    private bool observedProfile2, observedMenu, observedStartupTask, observedKnownModal, observedTransitionComplete;
    private string? progressHash;

    public override void _EnterTree()
    {
        try
        {
            ManagedBootstrap.Require(!entered && Name.ToString() == NormalBootConfig.ObserverName,
                "Observer autoload identity/count is wrong");
            entered = true; elapsed.Start();
            // Only this observer continues to observe if an original modal pauses the normal tree.
            ProcessMode = ProcessModeEnum.Always;
            Node[] children = GetTree().Root.GetChildren().ToArray();
            WriteEntryTreeDiagnostic(children);
            string[] names = children.Select(node => node.Name.ToString()).ToArray();
            string[] expected = ["SentryBootstrap", "FmodManager", NormalBootConfig.ObserverName, "Game"];
            ManagedBootstrap.Require(names.SequenceEqual(expected, StringComparer.Ordinal),
                "Actual root child entry order differs from the three autoloads followed by the attached original Game");
            Node root = GetTree().Root;
            Node[] childrenWithInternal = root.GetChildren(includeInternal: true).ToArray();
            ManagedBootstrap.Require(childrenWithInternal.Length == children.Length
                && childrenWithInternal.Select((node, index) => ReferenceEquals(node, children[index])).All(same => same),
                "Internal or additional root children differ from the exact four-node entry contract");
            ManagedBootstrap.Require(children.All(node => Valid(node) && ReferenceEquals(node.GetParent(), root)),
                "A required entry node is invalid or is not a direct child of the actual root");
            ManagedBootstrap.Require(children[0].GetType().FullName == "MegaCrit.Sts2.Core.Nodes.SentryBootstrap"
                && ReferenceEquals(children[0].GetType().Assembly, NormalGameLoader.GameAssembly)
                && ReferenceEquals(children[2], this), "Original Sentry/observer script identity differs");
            ManagedBootstrap.Require(children[1].GetType() == typeof(Node)
                && ReferenceEquals(children[1].GetType().Assembly, typeof(Node).Assembly),
                "Original Fmod GDScript host is not the shared GodotSharp Node type");
            ManagedBootstrap.Require(children.Take(3).All(node => node.GetClass() == "Node"
                && node.IsInsideTree() && !node.IsNodeReady()),
                "The three autoloads must already be inside the tree and not yet ready at observer entry");
            Node originalMainScene = children[3];
            ManagedBootstrap.Require(ReferenceEquals(originalMainScene, GetTree().CurrentScene)
                && originalMainScene.GetType().FullName == GameType
                && ReferenceEquals(originalMainScene.GetType().Assembly, NormalGameLoader.GameAssembly)
                && originalMainScene.GetClass() == "Control"
                && !originalMainScene.IsInsideTree() && !originalMainScene.IsNodeReady(),
                "Fourth root child is not the attached original CurrentScene NGame before its entry/ready notifications");
            RequireEntryScript(children[0], "res://addons/sentry/SentryBootstrap.cs", "CSharpScript");
            RequireEntryScript(children[1], "res://addons/fmod/FmodManager.gd", "GDScript");
            RequireEntryScript(children[2], "res://Main.cs", "CSharpScript");
            RequireEntryScript(originalMainScene, "res://src/Core/Nodes/NGame.cs", "CSharpScript");
            ManagedBootstrap.RecordEvent("normal_observer_entered_tree", new
            {
                rootChildren = children.Select(NodeEvidence).ToArray(), currentScene = NodeEvidence(GetTree().CurrentScene),
                rootEntryDiagnosticSha256 = ManagedBootstrap.HashFile("/work/normal-root-entry-diagnostic.json"),
                rootChildContract = "original_autoloads_then_attached_ngame_v1",
                observedEntryOrder = names, originalAutoloadsBeforeObserver = true,
                constructionOrder = "not_directly_observed", exactOriginalEnterReadyCallbackOrder = "not_directly_observed",
                limitation = "Root child order and ready states are observed here; this does not fabricate timestamps for earlier original callbacks"
            });
        }
        catch (Exception error) { ManagedBootstrap.Abort("normal_observer_enter_tree", error, 125); }
    }

    private static void RequireEntryScript(Node node, string expectedPath, string expectedNativeClass)
    {
        using Variant attachedScript = node.GetScript();
        ManagedBootstrap.Require(attachedScript.VariantType == Variant.Type.Object,
            "Required entry script is absent or has an unexpected Variant type: " + node.Name);
        GodotObject? scriptObject = attachedScript.AsGodotObject();
        ManagedBootstrap.Require(scriptObject is Resource && GodotObject.IsInstanceValid(scriptObject),
            "Required entry script is not a valid native resource: " + node.Name);
        Resource resource = (Resource)scriptObject;
        ManagedBootstrap.Require(resource.ResourcePath == expectedPath && resource.GetClass() == expectedNativeClass,
            "Required entry script path/native class differs: " + node.Name);
    }

    private void WriteEntryTreeDiagnostic(Node[] childrenWithoutInternal)
    {
        Node root = GetTree().Root;
        Node[] childrenWithInternal = root.GetChildren(includeInternal: true).ToArray();
        // Retain the exact non-internal array used by the entry assertion below.
        // Capture/write exceptions reach the original _EnterTree Abort; no fallback or overwrite.
        ManagedBootstrap.NewReport("normal-root-entry-diagnostic.json", new
        {
            schema = "e004b-normal-root-entry-diagnostic-v1",
            processRunId = NormalBootConfig.Current.ProcessRunId, pid = System.Environment.ProcessId,
            utc = DateTimeOffset.UtcNow.ToString("O"),
            boundary = "normal_observer_enter_tree_before_root_order_assertion",
            instanceIdEncoding = "uint64_invariant_decimal_string",
            root = EntryTreeNodeEvidence(root), observer = EntryTreeNodeEvidence(this),
            currentScene = EntryTreeNodeEvidence(GetTree().CurrentScene),
            orderedDirectChildren = new[]
            {
                new { includeInternal = false, children = childrenWithoutInternal.Select((node, index) =>
                    new { index, node = EntryTreeNodeEvidence(node) }).ToArray() },
                new { includeInternal = true, children = childrenWithInternal.Select((node, index) =>
                    new { index, node = EntryTreeNodeEvidence(node) }).ToArray() }
            },
            interpretation = "Diagnostic only; both arrays are sequential observations, not callback timestamps. All current entry assertions execute after this diagnostic."
        });
    }

    private static object? EntryTreeNodeEvidence(Node? node)
    {
        if (node is null) return null;
        ManagedBootstrap.Require(GodotObject.IsInstanceValid(node), "Invalid node during root-entry diagnosis");
        Node? parent = node.GetParent();
        ManagedBootstrap.Require(parent is null || GodotObject.IsInstanceValid(parent),
            "Invalid parent during root-entry diagnosis");
        bool insideTree = node.IsInsideTree();
        // Read the native attached-script resource, never a script-defined property or getter.
        using Variant attachedScript = node.GetScript();
        GodotObject? scriptObject = null;
        Resource? scriptResource = null;
        if (attachedScript.VariantType != Variant.Type.Nil)
        {
            ManagedBootstrap.Require(attachedScript.VariantType == Variant.Type.Object,
                "Attached script has an unexpected Variant type during root-entry diagnosis");
            scriptObject = attachedScript.AsGodotObject();
            // An Object(null) Variant also means no script; retain its type without inventing a resource.
            if (scriptObject is not null)
            {
                ManagedBootstrap.Require(scriptObject is Resource && GodotObject.IsInstanceValid(scriptObject),
                    "Attached script is not a valid resource during root-entry diagnosis");
                scriptResource = (Resource)scriptObject;
            }
        }
        return new
        {
            name = node.Name.ToString(), nativeClass = node.GetClass(),
            managedType = node.GetType().FullName, assembly = node.GetType().Assembly.FullName,
            path = insideTree ? node.GetPath().ToString() : null,
            instanceId = node.GetInstanceId().ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent = parent is null ? null : new
            {
                name = parent.Name.ToString(), nativeClass = parent.GetClass(),
                instanceId = parent.GetInstanceId().ToString(System.Globalization.CultureInfo.InvariantCulture),
                path = parent.IsInsideTree() ? parent.GetPath().ToString() : null
            },
            insideTree, ready = node.IsNodeReady(), queuedForDeletion = node.IsQueuedForDeletion(),
            scriptVariantType = attachedScript.VariantType.ToString(),
            scriptObjectIsNull = scriptObject is null,
            scriptResourcePath = scriptResource?.ResourcePath, scriptNativeClass = scriptResource?.GetClass()
        };
    }

    public override void _Ready()
    {
        try
        {
            ManagedBootstrap.Require(entered && !ready, "Observer Ready count/order failed");
            ready = true;
            ManagedBootstrap.RequireCompleted47AtReady();
            int code = ReleaseHostIdentity.Finish(0);
            ManagedBootstrap.RecordReadyIdentityResult(code);
            ManagedBootstrap.RecordEvent("normal_observer_waiting_for_original_scene", new
            { timeoutSeconds = NormalBootConfig.Current.ObserverTimeoutSeconds,
                gameInitializationInvokedByObserver = false, originalTreeLoopContinues = true });
            SetProcess(true);
        }
        catch (Exception error) { ManagedBootstrap.Abort("normal_observer_ready", error, 125); }
    }

    private bool actionsStarted;

    public override void _Process(double delta)
    {
        if (finished || !ready) return;
        untilSample -= delta;
        if (untilSample > 0) return;
        untilSample = 0.25;
        try
        {
            NormalBootLogs.Snapshot capturedLogs = logs.Read();
            if (actionsStarted)
            {
                TickActions(capturedLogs);
                AppendObservation(capturedLogs);
                if (!finished && elapsed.Elapsed.TotalSeconds >= NormalBootConfig.Current.ObserverTimeoutSeconds)
                {
                    Directory.CreateDirectory("/work/bridge");
                    Atomic("/work/bridge/terminal.json",new {outcome="budget_truncated",reason="game_wall_clock_budget",submittedCount,lastPlayerObservation,lastPlayerSnapshot,waiting=waitingController?"controller":"game"});
                    Trace("budget_truncated",new {reason="game_wall_clock_budget",submittedCount,lastPlayerObservation});
                    Finish("continuous_budget_truncated",null,0,capturedLogs);
                }
                return;
            }
            ObserveOriginalScene(capturedLogs.EssentialObserved);
            AppendObservation(capturedLogs);
            if (observedMenu && observedStartupTask && observedProfile2 && observedTransitionComplete
                && capturedLogs.EssentialObserved && capturedLogs.CompleteObserved)
            {
                CaptureOfficialSnapshots("before-");
                actionsStarted = true;
                InitializeActions();
                return;
            }
            if (elapsed.Elapsed.TotalSeconds >= NormalBootConfig.Current.ObserverTimeoutSeconds)
                Finish("observation_timeout", new TimeoutException("Original menu/deferred/profile evidence did not all arrive within 45 seconds"), 148, capturedLogs);
        }
        catch (Exception error)
        {
            Finish("observation_failed", error, 147, null);
        }
    }

    private void ObserveOriginalScene(bool essentialLogObserved)
    {
        observedProfile2 = observedMenu = observedStartupTask = observedKnownModal = observedTransitionComplete = false;
        Node? game = GetTree().CurrentScene;
        lastState = new { stage = "sampling_original_scene", currentScene = NodeEvidence(game) };
        if (!Valid(game))
        {
            lastState = new { stage = "original_main_scene_not_yet_available", currentScene = NodeEvidence(game) };
            return;
        }
        ManagedBootstrap.Require(game!.GetType().FullName == GameType
            && ReferenceEquals(game.GetType().Assembly, NormalGameLoader.GameAssembly),
            "Actual main scene is not the original registered NGame");
        bool gameReady = game.IsNodeReady();
        if (!gameReady)
        {
            lastState = new { stage = "original_ngame_not_yet_ready", currentScene = NodeEvidence(game) };
            return;
        }
        ManagedBootstrap.Require(ReferenceEquals(NormalGameRead.Call(0x06005a76, null), game),
            "Normal NGame.Instance does not refer to the actual engine-created current scene");
        object container = NormalGameRead.Call(0x06005a78, game)
            ?? throw new InvalidDataException("Original NGame RootSceneContainer missing");
        Node? scene = NormalGameRead.Call(0x06005b25, container) as Node;
        Task startup = NormalGameRead.Call(0x06005a99, game) as Task
            ?? throw new InvalidDataException("Original startup Task is unavailable");
        bool menu = Valid(scene) && scene!.GetType().FullName == MenuType && scene.IsNodeReady() && scene.IsInsideTree();
        string? sceneType = scene?.GetType().FullName;
        if (Valid(scene) && sceneType is not MenuType and not LogoType)
            throw new InvalidDataException("Unknown original root scene during bounded boot: " + sceneType);
        ManagedBootstrap.Require(NormalGameRead.Call(0x06005a7d, game) is null,
            "A run node exists although this probe authorizes only the normal main menu");
        object transition = NormalGameRead.Call(0x06005a7f, game)
            ?? throw new InvalidDataException("Original NGame transition node missing");
        bool inTransition = RequireValue<bool>(NormalGameRead.Call(0x06005b31, transition), "NTransition.InTransition");
        object modalContainer = NormalGameRead.Call(0x06008741, null)
            ?? throw new InvalidDataException("Original modal container was not initialized by the normal scene");
        object? modal = NormalGameRead.Call(0x06008743, modalContainer);
        string? modalType = modal?.GetType().FullName;
        if (modal is not null && modalType != EaType)
        {
            lastState = new { stage = "unknown_modal", currentScene = NodeEvidence(game), rootScene = NodeEvidence(scene), modalType };
            throw new InvalidDataException("Unknown or error modal during boot: " + modalType);
        }
        if (modal is not null)
            ManagedBootstrap.Require(modal is Node modalNode && Valid(modalNode)
                && ReferenceEquals(modal.GetType().Assembly, NormalGameLoader.GameAssembly), "EA modal is not the original live node");
        observedMenu = menu;
        observedStartupTask = startup.IsCompleted;
        observedKnownModal = modal is not null;
        observedTransitionComplete = !inTransition;
        object? profile = null;
        // The public SaveManager getter lazily constructs a manager if absent. Only read it after
        // original NGame has actually produced its menu and finished its own startup task.
        if (menu && startup.IsCompleted && essentialLogObserved)
        {
            ManagedBootstrap.Require(!startup.IsCanceled && !startup.IsFaulted,
                "Original startup Task is cancelled or faulted: " + startup.Exception);
            profile = ObserveInitializedProfile();
        }
        lastState = new
        {
            stage = menu ? modal is null ? "original_menu_observed" : "menu_loaded_with_initial_modal" : "normal_logo_or_menu_pending",
            currentScene = NodeEvidence(game), rootContainer = NodeEvidence(container as Node), rootScene = NodeEvidence(scene),
            bootstrapTaskCompleted = startup.IsCompleted, bootstrapTaskStatus = startup.Status.ToString(),
            taskCompletionIsNotAcceptance = true, menuSceneLoaded = menu, inTransition,
            modalType, knownEarlyAccessModalObserved = observedKnownModal, modalClosedByObserver = false, profile
        };
    }

    private object ObserveInitializedProfile()
    {
        bool testMode = RequireValue<bool>(NormalGameRead.Call(0x060003ea, null), "TestMode.IsOn");
        bool steam = RequireValue<bool>(NormalGameRead.Call(0x06001037, null), "SteamInitializer.Initialized");
        object platform = NormalGameRead.Call(0x0600101b, null) ?? throw new InvalidDataException("Null platform enum");
        ulong player = RequireValue<ulong>(NormalGameRead.Call(0x0600101f, null, platform), "PlatformUtil.GetLocalPlayerId");
        bool modded = RequireValue<bool>(NormalGameRead.Call(0x0600086b, null), "UserDataPathProvider.IsRunningModded");
        Type platformType = platform.GetType();
        bool platformIsEnum = platformType.IsEnum;
        Type? platformUnderlyingType = platformIsEnum ? Enum.GetUnderlyingType(platformType) : null;
        object? platformNumeric = platformUnderlyingType is null ? null
            : Convert.ChangeType(platform, platformUnderlyingType, System.Globalization.CultureInfo.InvariantCulture);
        string? platformName = platformIsEnum ? ((Enum)platform).ToString() : null;
        bool platformIsGameAssembly = ReferenceEquals(platformType.Assembly, NormalGameLoader.GameAssembly);
        var platformEvidence = new
        {
            fullName = platformType.FullName, assemblyFullName = platformType.Assembly.FullName,
            isGameAssembly = platformIsGameAssembly, isEnum = platformIsEnum,
            underlyingType = platformUnderlyingType?.FullName, numeric = platformNumeric, name = platformName
        };
        int platformBranchSequence = AppendPlatformBranchObservation(testMode, steam, platformName, player, modded, platformEvidence);
        ManagedBootstrap.Require(!testMode && !steam && platformIsGameAssembly && platformIsEnum
            && platformType.FullName == "MegaCrit.Sts2.Core.Platform.PlatformType" && platformUnderlyingType == typeof(int)
            && platformNumeric is int platformNumber && platformNumber == 0 && platformName == "None" && player == 1 && !modded,
            "Unexpected TestMode/Steam/platform/player/modded branch in normal boot");
        object mods = NormalGameRead.Call(0x06004978, null) ?? throw new InvalidDataException("Loaded-mod collection missing");
        ManagedBootstrap.Require(mods is IEnumerable, "Loaded-mod value is not an enumerable");
        object?[] loadedMods = ((IEnumerable)mods).Cast<object?>().ToArray();
        ManagedBootstrap.Require(loadedMods.Length == 0, "Unexpected loaded mods in the original normal startup");
        string? modState = NormalGameRead.Call(0x06004957, null)?.ToString();
        object manager = NormalGameRead.Call(0x0600071d, null) ?? throw new InvalidDataException("Normal SaveManager.Instance missing");
        if (saveManager is not null) ManagedBootstrap.Require(ReferenceEquals(saveManager, manager), "Normal SaveManager instance changed while observing");
        saveManager = manager;
        bool initialized = RequireValue<bool>(NormalGameRead.Call(0x06000726, manager), "SaveManager.IsProfileInitialized");
        int profileId = RequireValue<int>(NormalGameRead.Call(0x06000727, manager), "SaveManager.CurrentProfileId");
        bool prefsLoaded = RequireValue<bool>(NormalGameRead.Call(0x06000721, manager), "SaveManager.IsPrefsLoaded");
        ManagedBootstrap.Require(initialized && profileId == NormalBootConfig.Current.ProfileId && prefsLoaded, "Normal profile 2 and original prefs did not initialize");
        string account = RequireValue<string>(NormalGameRead.Call(0x0600086f, null, null, null, null), "GetAccountScopedBasePath");
        string selector = RequireValue<string>(NormalGameRead.Call(0x06000a67, null, new object?[] { null }), "GetProfileSavePath");
        string progress = RequireValue<string>(NormalGameRead.Call(0x06000a6e, null, NormalBootConfig.Current.ProfileId, null), "GetProgressPathForProfile");
        string prefs = RequireValue<string>(NormalGameRead.Call(0x06000a61, null, NormalBootConfig.Current.ProfileId, null), "GetPrefsPath");
        string actualProgress = RequireValue<string>(NormalGameRead.Call(0x06000735, manager, "saves/progress.save"), "actual progress store path");
        string actualPrefs = RequireValue<string>(NormalGameRead.Call(0x06000735, manager, "saves/prefs.save"), "actual prefs store path");
        ManagedBootstrap.Require(account == "user://default/1" && selector == "profile.save"
            && progress == $"profile{NormalBootConfig.Current.ProfileId}/saves/progress.save" && prefs == $"profile{NormalBootConfig.Current.ProfileId}/saves/prefs.save"
            && actualProgress == account + "/" + progress && actualPrefs == account + "/" + prefs
            && ProjectSettings.GlobalizePath(actualProgress) == NormalBootConfig.Current.InputFiles[progress].Path
            && ProjectSettings.GlobalizePath(actualPrefs) == NormalBootConfig.Current.InputFiles[prefs].Path,
            "Actual normal save-store/helper paths do not resolve to the three isolated original inputs");
        observedProfile2 = true;
        return new { testMode, steamInitialized = steam, platform = platformName, localPlayerId = player,
            platformEvidence, platformBranchObservationSequence = platformBranchSequence,
            modded, modState, loadedModCount = loadedMods.Length, initialized, profileId, prefsLoaded,
            account, selector, progress, prefs, actualProgress, actualPrefs,
            startupReadSaveResult = "not_directly_observed", fullProgressMatch = "HQ_comparison_pending" };
    }

    private int AppendPlatformBranchObservation(bool testMode, bool steam, string? platformName, ulong player,
        bool modded, object platformEvidence)
    {
        ManagedBootstrap.Require(platformBranchObservationCount < 512, "Normal platform branch observation limit exceeded (512 lines)");
        int next = platformBranchObservationCount + 1;
        using (var stream = new FileStream("/work/normal-platform-branch-observations.jsonl",
            next == 1 ? FileMode.CreateNew : FileMode.Append, System.IO.FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(stream, new
            {
                schema = "e004b-normal-platform-branch-observation-v1", sequence = next,
                processRunId = NormalBootConfig.Current.ProcessRunId, pid = System.Environment.ProcessId,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, utc = DateTimeOffset.UtcNow.ToString("O"),
                boundary = "five_public_reads_before_branch_assertion", testMode, steamInitialized = steam,
                platform = platformName, localPlayerId = player, modded, platformEvidence
            }, JournalJson);
            stream.WriteByte((byte)'\n'); stream.Flush(true);
        }
        platformBranchObservationCount = next;
        return next;
    }

    private void CaptureOfficialSnapshots(string prefix = "")
    {
        if (saveManager is null || !observedProfile2) return;
        object progress = NormalGameRead.Call(0x06000722, saveManager)
            ?? throw new InvalidDataException("Initialized normal Progress is null");
        object dto = NormalGameRead.Call(0x060006d9, progress)
            ?? throw new InvalidDataException("Official ToSerializable returned null");
        WriteOfficial(prefix + "official-progress.json", NormalGameRead.OfficialJson(dto));
        progressHash = ManagedBootstrap.HashFile("/work/" + prefix + "official-progress.json");
        object prefs = NormalGameRead.Call(0x06000720, saveManager) ?? throw new InvalidDataException("Normal prefs object missing");
        object settings = NormalGameRead.Call(0x0600071f, saveManager) ?? throw new InvalidDataException("Normal settings object missing");
        WriteOfficial(prefix + "official-prefs.json", NormalGameRead.OfficialJson(prefs));
        WriteOfficial(prefix + "official-settings.json", NormalGameRead.OfficialJson(settings));
        ManagedBootstrap.RecordEvent("normal_official_progress_captured", new { progressSha256 = progressHash,
            fullOfficialSnapshot = true, saveMethodsInvoked = false, startupReadSaveResult = "not_directly_observed",
            comparison = "HQ must compare all fields with E003 plus original input, retaining D026 concrete differences" });
    }

    private static void WriteOfficial(string leaf, string text)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
        ManagedBootstrap.Require(bytes.Length is > 0 and <= 16777216, "Official snapshot size exceeded its 16 MiB bound");
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        ManagedBootstrap.Require(parsed.RootElement.ValueKind == JsonValueKind.Object, "Official snapshot must be a full JSON object");
        using var stream = new FileStream("/work/" + leaf, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(true);
    }

    private void AppendObservation(NormalBootLogs.Snapshot capturedLogs)
    {
        int next = ++observationSequence;
        using var stream = new FileStream("/work/normal-boot-observations.jsonl", next == 1 ? FileMode.CreateNew : FileMode.Append,
            System.IO.FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, new { schema = "e004b-normal-boot-observation-v1", sequence = next,
            processRunId = NormalBootConfig.Current.ProcessRunId, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            utc = DateTimeOffset.UtcNow.ToString("O"), state = lastState,
            logs = new { capturedLogs.EssentialObserved, capturedLogs.CompleteObserved, capturedLogs.ErrorCount,
                stdoutBytes = capturedLogs.Stdout.CapturedBytes, stderrBytes = capturedLogs.Stderr.CapturedBytes } }, JournalJson);
        stream.WriteByte((byte)'\n'); stream.Flush(true);
    }

    private void Finish(string outcome, Exception? error, int exitCode, NormalBootLogs.Snapshot? capturedLogs)
    {
        if (finished) return;
        if(error!=null) { Directory.CreateDirectory("/work/bridge"); Atomic("/work/bridge/failure.json", new {outcome,error=error.ToString(),decisionId,submittedCount,lastPlayerObservation,lastPlayerSnapshot,lastState,elapsedSeconds=elapsed.Elapsed.TotalSeconds});Trace("failure",new {outcome,error=error.ToString(),lastPlayerObservation}); }
        finished = true;
        string? finishFailure = null;
        int? finalIdentityCode = null;
        try
        {
            CaptureOfficialSnapshots();
            capturedLogs ??= logs.Read();
            ManagedBootstrap.NewReport("normal-userdata-after.json", UserDataInventory());
            finalIdentityCode = ReleaseHostIdentity.Finish(0, "normal-boot-final-identity.json");
            if (finalIdentityCode != 0)
            {
                exitCode = finalIdentityCode.Value;
                error ??= new InvalidDataException("Normal game final assembly/native identity checks failed");
                outcome = "final_identity_failed";
            }
        }
        catch (Exception finishingError)
        {
            finishFailure = finishingError.ToString(); exitCode = 149;
            outcome = "evidence_capture_failed";
        }
        try
        {
            ManagedBootstrap.NewReport("normal-boot-result.json", new
            {
                schema = "e004b-normal-boot-result-v1", processRunId = NormalBootConfig.Current.ProcessRunId,
                pid = System.Environment.ProcessId, outcome, exitCode, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                probeCompleted = error is null && finishFailure is null && exitCode == 0,
                normalBootAccepted = false, acceptanceStatus = "HQ_full_progress_and_raw_log_validation_required",
                menuSceneLoaded = observedMenu, bootstrapTaskCompleted = observedStartupTask,
                profile2Observed = observedProfile2, profile2ProgressMatches = (bool?)null,
                deferredCompleteLogObserved = capturedLogs?.CompleteObserved ?? false,
                deferredAssetsAccepted = false, transitionCompleteObserved = observedTransitionComplete,
                menuLoadedWithInitialModal = observedMenu && observedKnownModal, menuReady = menuReadyObserved,
                menuReadyReason = "Recorded after original EA callback and complete modal/transition removal",
                startupReadSaveResult = "not_directly_observed", progressSnapshotSha256 = progressHash,
                originalInput = NormalBootConfig.InputBefore, lastState, observationSequence, platformBranchObservationCount,
                errors = new { observation = error?.ToString(), evidenceCapture = finishFailure },
                logs = capturedLogs is null ? null : NormalBootLogs.Evidence(capturedLogs), finalIdentityCode,
                apiEvidence = NormalGameRead.ObservedApiEvidence(),
                inputActions = actionJournal, runStartCalls = embarkCalls, modalCloseCalls = eaCalls, actionStage,
                sentryModuleInitializerTiming = "unknown_not_forced", gameManagedAssemblyLoaded = true,
                nativeGameCodeMayHaveLoaded = true, scope = "Bounded continuous original production decisions; fidelity and training separately verified"
            });
            ManagedBootstrap.RecordEvent("normal_observation_finished", new { outcome, exitCode,
                resultSha256 = ManagedBootstrap.HashFile("/work/normal-boot-result.json"),
                normalBootAccepted = false, hqValidationRequired = true });
        }
        catch (Exception reportError) { ManagedBootstrap.Abort("normal_observer_result_write", reportError, 150); }
        GetTree().Quit(exitCode);
    }

    public override void _ExitTree()
    {
        if (finished || !entered) return;
        finished = true;
        // Original startup can request Quit(0) even on an error. Preserve that as an incomplete observation.
        try
        {
            ManagedBootstrap.NewReport("normal-boot-result.json", new { schema = "e004b-normal-boot-result-v1",
                processRunId = NormalBootConfig.Current.ProcessRunId, pid = System.Environment.ProcessId,
                outcome = "original_tree_exited_before_observation_completed", probeCompleted = false,
                normalBootAccepted = false, originalProcessExitCode = "outer_runner_must_record",
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, lastState, observationSequence, platformBranchObservationCount,
                startupReadSaveResult = "not_directly_observed", rawLogsRequired = true });
            ManagedBootstrap.RecordEvent("normal_original_tree_exited_early", new { normalBootAccepted = false, probeCompleted = false });
        }
        catch (Exception error) { Console.Error.WriteLine("E004B_NORMAL_EARLY_EXIT_EVIDENCE_FAILED\n" + error); }
    }

    private static object UserDataInventory()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Normal user-data inventory requires Linux");
        string root = NormalBootConfig.Current.ExpectedUserDataPath;
        var pending = new Queue<string>(); pending.Enqueue(root);
        var files = new List<object>(); var directories = new List<string>(); long total = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Dequeue();
            ManagedBootstrap.Require(new DirectoryInfo(directory).LinkTarget is null, "Symlink generated under user data");
            directories.Add(Path.GetRelativePath(root, directory));
            ManagedBootstrap.Require(directories.Count <= 2000, "User-data directory inventory bound exceeded");
            foreach (string path in Directory.GetFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                ManagedBootstrap.Require((attributes & FileAttributes.ReparsePoint) == 0, "Linked output under user data: " + path);
                if ((attributes & FileAttributes.Directory) != 0) { pending.Enqueue(path); continue; }
                var info = new FileInfo(path); total = checked(total + info.Length);
                ManagedBootstrap.Require(files.Count < 2000 && total <= 134217728, "User-data inventory exceeded 2000 files or 128 MiB");
                files.Add(new { path = Path.GetRelativePath(root, path), bytes = info.Length,
                    sha256 = ManagedBootstrap.HashFile(path), mode = (int)File.GetUnixFileMode(path) });
            }
        }
        return new { schema = "e004b-normal-userdata-inventory-v1", processRunId = NormalBootConfig.Current.ProcessRunId,
            root, files, directories, totalBytes = total, beforeInputs = NormalBootConfig.InputBefore,
            comparison = "All output bytes retained by outer runner; HQ classifies normal Settings generation and every original input change" };
    }

    private static bool Valid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Node? node) => node is not null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();
    private static object? NodeEvidence(Node? node) => !Valid(node) ? null : new
    { name = node!.Name.ToString(), managedType = node.GetType().FullName, assembly = node.GetType().Assembly.FullName,
        path = node.IsInsideTree() ? node.GetPath().ToString() : null, insideTree = node.IsInsideTree(), ready = node.IsNodeReady() };
    private static T RequireValue<T>(object? value, string label) => value is T typed ? typed
        : throw new InvalidDataException("Unexpected public observation value for " + label);
}
