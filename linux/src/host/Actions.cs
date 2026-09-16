using System.Collections;
using System.Reflection;
using System.Text.Json;
using Godot;
using File = System.IO.File;

// Structured test driver. UI transport enters the original focus/mouse handlers;
// it never writes engine fields, fabricates options or supplies an alternate result.
public partial class Main
{
    private string actionStage = "boot";
    private int choiceIndex, eaCalls, embarkCalls;
    private string tutorials = "";
    private string startMode = "new";
    private bool menuReadyObserved;
    private double stageSince;
    private readonly List<object> actionJournal = new();
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    private static object Read(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, PublicInstance)
            ?? throw new MissingMemberException(instance.GetType().FullName, name);
        return property.GetValue(instance) ?? throw new InvalidDataException("Null " + name);
    }
    private static object? Invoke(int token, object instance, params object?[] args)
    {
        var method = (MethodInfo)NormalGameLoader.GameAssembly.ManifestModule.ResolveMethod(token)!;
        ManagedBootstrap.Require(method.DeclaringType!.IsInstanceOfType(instance), "Production callback owner mismatch");
        return method.Invoke(instance, args);
    }
    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node;
        foreach (Node child in node.GetChildren())
            foreach (Node descendant in Descendants(child)) yield return descendant;
    }
    private Node? VisibleType(string shortName) => Descendants(GetTree().Root).SingleOrDefault(n =>
        Valid(n) && n.IsNodeReady() && n.GetType().Name == shortName && n is Control c && c.IsVisibleInTree() && (shortName != "NPotionPopup" || !(bool)Read(n, "IsMarkedForRemoval")));
    private static bool Enabled(Node n) => Valid(n) && n.IsNodeReady() && n is Control c
        && c.IsVisibleInTree() && (bool)Read(n, "IsEnabled");
    private void Stage(string value)
    {
        actionStage = value; stageSince = elapsed.Elapsed.TotalSeconds;
        ManagedBootstrap.RecordEvent("action_stage", new { stage = value, elapsedSeconds = stageSince });
    }
    private void RecordAction(string kind, Node node, object? detail = null)
    {
        ManagedBootstrap.Require(actionJournal.Count < 1200, "Menu/action budget exhausted");
        var row = new { sequence = actionJournal.Count + 1, kind, stage = actionStage,
            elapsedSeconds = elapsed.Elapsed.TotalSeconds, target = NodeEvidence(node), detail };
        actionJournal.Add(row);
        File.AppendAllText("/work/actions.jsonl", JsonSerializer.Serialize(row, JournalJson) + "\n");
    }
    private void Click(Node node, string kind, object? detail = null)
    {
        ManagedBootstrap.Require(Enabled(node), "Action target is not live, visible and enabled: " + node.Name);
        var c = (Control)node;
        c.EmitSignal(Control.SignalName.MouseEntered);
        ManagedBootstrap.Require((bool)Invoke(0x060081c7, node)!, "Original hover handler did not focus target");
        RecordAction(kind, node, detail);
        // Original DebugPress/Release emit MousePressed/Released; original HandleMouse* enforce
        // enabled/visible/focused guards, then OnPress/OnRelease and the bound Released callback.
        Invoke(0x060081db, node);
        Invoke(0x060081dc, node);
        if (Valid(node)) c.EmitSignal(Control.SignalName.MouseExited);
    }
    private void InitializeActions()
    {
        using var cfg = JsonDocument.Parse(File.ReadAllBytes("/work/action-config.json"));
        choiceIndex = cfg.RootElement.GetProperty("choiceIndex").GetInt32();
        startMode = cfg.RootElement.GetProperty("start").GetString()!;
        ManagedBootstrap.Require(startMode is "new" or "continue", "Unknown start mode");
        tutorials = cfg.RootElement.GetProperty("tutorials").GetString()!;
        ManagedBootstrap.Require(choiceIndex >= 0 && tutorials is "yes" or "no", "Missing explicit action configuration");
        configuredSeed=cfg.RootElement.GetProperty("seed").GetString()!;
        decisionLimit=cfg.RootElement.GetProperty("decisionLimit").GetInt32();
        legacyPrefixLimit=cfg.RootElement.GetProperty("legacyPrefixLimit").GetInt32();
        Directory.CreateDirectory("/work/bridge");
        Stage("ea");
    }
    private void TickActions(NormalBootLogs.Snapshot capturedLogs)
    {
        var game = GetTree().CurrentScene!;
        var transition = NormalGameRead.Call(0x06005a7f, game)!;
        bool inTransition = (bool)NormalGameRead.Call(0x06005b31, transition)!;
        var modalContainer = NormalGameRead.Call(0x06008741, null)!;
        var modal = NormalGameRead.Call(0x06008743, modalContainer) as Node;
        lastState = new { stage = actionStage, inTransition, modal = NodeEvidence(modal),
            visibleControls = Descendants(game).OfType<Control>().Where(n => Valid(n) && n.IsVisibleInTree()
                && n.GetType().Assembly == NormalGameLoader.GameAssembly)
                .Select(n => new { path = n.GetPath().ToString(), type = n.GetType().Name }).Take(160).ToArray() };
        if (actionStage != "decision_wait" && elapsed.Elapsed.TotalSeconds - stageSince > 55)
            throw new TimeoutException("No completion in stage " + actionStage);
        if (inTransition) return;
        if (modal is not null && modal.GetType().Name != "NEarlyAccessDisclaimer"
            && modal.GetType().Name != "NAcceptTutorialsFtue")
            throw new InvalidDataException("Unsupported modal " + modal.GetType().FullName);
        switch (actionStage)
        {
            case "ea":
                if (modal is not null)
                {
                    var button = Descendants(modal).Single(n => n.GetType().Name == "NDisclaimerProceedButton");
                    Click(button, "ea_confirm"); eaCalls++; Stage("ea_wait"); return;
                }
                Stage("menu"); return;
            case "ea_wait":
                if (modal is not null || Descendants(game).Any(n => Valid(n) && n.GetType().Name == "NEarlyAccessDisclaimer")) return;
                Stage("menu"); return;
            case "menu":
                var menu = VisibleType("NMainMenu"); if (menu is null) return;
                if (startMode == "continue") {
                    var continueButton = (Node)Field(menu,"_continueButton");
                    ManagedBootstrap.Require(Enabled(continueButton), "Explicit archived run is not continuable");
                    menuReadyObserved=true;Click(continueButton,"resume_archived_gui_fixture");Stage("run_wait");return;
                }
                // New-run path intentionally unreachable for this scoped resume fixture.
#pragma warning disable CS0162
                var single = menu.GetNode("MainMenuTextButtons/SingleplayerButton");
                ManagedBootstrap.Require(Enabled(single), "Normal new-run button unavailable; never abandon/delete an existing run");
                menuReadyObserved = true;
                Click(single, "singleplayer"); Stage("singleplayer_wait"); return;
            case "singleplayer_wait":
                var submenu = VisibleType("NSingleplayerSubmenu");
                if (submenu is not null)
                {
                    var standard = Descendants(submenu).Single(n => n.Name == "StandardButton");
                    if (!Enabled(standard)) return;
                    Click(standard, "standard"); Stage("character_wait"); return;
                }
                if (VisibleType("NCharacterSelectScreen") is not null) Stage("character_wait");
                return;
            case "character_wait":
                var screen = VisibleType("NCharacterSelectScreen"); if (screen is null) return;
                var silent = Descendants(screen).Single(n => n.GetType().Name == "NCharacterSelectButton"
                    && Read(n, "Character").GetType().FullName == "MegaCrit.Sts2.Core.Models.Characters.Silent");
                if (!Enabled(silent)) return;
                ManagedBootstrap.Require(!(bool)Read(silent, "IsLocked"), "Original Silent is locked");
                RecordAction("select_silent_focus", silent);
                ((Control)silent).GrabFocus();
                ManagedBootstrap.Require(((Control)silent).HasFocus() && (bool)Read(silent, "IsSelected"), "Silent focus/selection failed");
                Stage("ascension"); return;
            case "ascension":
                screen = VisibleType("NCharacterSelectScreen")!;
                var panel = screen.GetNode("%AscensionPanel");
                int level = (int)Read(panel, "Ascension");
                if (level > 0)
                {
                    Click(panel.GetNode("HBoxContainer/LeftArrowContainer/LeftArrow"), "ascension_decrement", new { before = level });
                    ManagedBootstrap.Require((int)Read(panel, "Ascension") == level - 1, "Ascension callback did not decrement exactly one");
                    return;
                }
                var lobby = Read(screen, "Lobby");
                var local = Read(lobby, "LocalPlayer");
                var character = local.GetType().GetField("character")!.GetValue(local)!;
                ManagedBootstrap.Require(level == 0 && (int)Read(lobby, "Ascension") == 0
                    && character.GetType().FullName == "MegaCrit.Sts2.Core.Models.Characters.Silent"
                    && ((IEnumerable)Read(lobby, "Players")).Cast<object>().Count() == 1, "Silent/A0/singleplayer lobby mismatch");
                ManagedBootstrap.NewReport("production-lobby.json", new { character = character.GetType().FullName,
                    panelAscension = level, lobbyAscension = Read(lobby, "Ascension"), playerCount = 1,
                    source = "original InitializeSingleplayer + FocusEntered + left-arrow callbacks" });
                Stage("embark"); return;
            case "embark":
                screen = VisibleType("NCharacterSelectScreen")!;
                var confirm = screen.GetNode("ConfirmButton"); if (!Enabled(confirm)) return;
                Invoke(0x06005a96, game, configuredSeed);
                Trace("seed_input",new {seed=configuredSeed,source="original DebugSeedOverride, explicitly configured, not UI seed field"});
                Click(confirm, "embark"); embarkCalls++; Stage("run_wait"); return;
            case "run_wait":
                var ftue = VisibleType("NAcceptTutorialsFtue");
                if (ftue is not null)
                {
                    var popup = ftue.GetNode("VerticalPopup");
                    var button = (Node)Read(popup, tutorials == "yes" ? "YesButton" : "NoButton");
                    if (!Enabled(button)) return;
                    ManagedBootstrap.NewReport("tutorial-decision.json", new { options = new[] { "yes", "no" },
                        selected = tutorials, source = "explicit launch configuration", labels = VisibleText(popup) });
                    Click(button, "tutorial_preference", new { selected = tutorials });
                    Stage("new_run_wait"); return;
                }
                if (NormalGameRead.Call(0x06005a7d, game) is Node run && Valid(run) && run.IsNodeReady()) Stage("decision_wait");
                return;
            case "new_run_wait":
                if (VisibleType("NAcceptTutorialsFtue") is not null) return;
                if (NormalGameRead.Call(0x06005a7d, game) is Node run2 && Valid(run2) && run2.IsNodeReady()) Stage("decision_wait");
                return;
            case "decision_wait":
                TickBridge(capturedLogs); return;
        }
    }
    private static Node[] EventButtons(Node room) => Descendants(room).Where(n => Valid(n) && n.IsNodeReady()
        && n.GetType().Name == "NEventOptionButton" && n is Control c && c.IsVisibleInTree()).ToArray();
    private static object[] VisibleText(Node root) => Descendants(root).OfType<Control>()
        .Where(c => Valid(c) && c.IsVisibleInTree() && c.GetType().GetProperty("Text", PublicInstance)?.PropertyType == typeof(string))
        .Select(c => new { text = (string)Read(c, "Text") }).Where(x => x.text.Length > 0).Cast<object>().ToArray();
    private static object EventObservation(Node room) => new { schema = "sts2-player-event-v1", kind = "event",
        visibleText = VisibleText(room), options = EventButtons(room).Select(n => new { index = (int)Invoke(0x06008402, n)!,
            textKey = (string)Read(Read(n, "Option"), "TextKey"), text = VisibleText(n),
            legal = Enabled(n) && !(bool)Read(Read(n, "Option"), "IsLocked") }).ToArray() };
    private void ValidateNewRun()
    {
        object manager = ((MethodInfo)NormalGameLoader.GameAssembly.ManifestModule.ResolveMethod(0x06000bdf)!).Invoke(null, null)!;
        object state = Invoke(0x06000c30, manager)!;
        object[] players = ((IEnumerable)Read(state, "Players")).Cast<object>().ToArray();
        ManagedBootstrap.Require(players.Length == 1 && Read(players[0], "Character").GetType().Name == "Silent"
            && (int)Read(state, "AscensionLevel") == 0 && Read(state, "GameMode").ToString() == "Standard"
            && (bool)Read(manager, "IsInProgress") && (bool)Read(manager, "ShouldSave")
            && !((IEnumerable)Read(state, "Modifiers")).Cast<object>().Any(), "Actual new-run production state mismatch");
        string actualSeed=Read(Read(state,"Rng"),"StringSeed").ToString()!;
        ManagedBootstrap.Require(actualSeed==configuredSeed,"Original seed differs from explicit configured seed");
        ManagedBootstrap.NewReport("production-run.json", new { actualSeed, character = Read(players[0], "Character").GetType().FullName,
            ascension = Read(state, "AscensionLevel"), gameMode = Read(state, "GameMode").ToString(),
            players = players.Length, shouldSave = Read(manager, "ShouldSave"), modifiers = 0,
            seedSource = startMode == "continue" ? "archived run; seed validated" : "explicit DebugSeedOverride before Embark",
            productionEntry = startMode == "continue" ? "original Continue" : "original single-player Silent A0 Embark", diagnosticOnly = true });
    }
}
