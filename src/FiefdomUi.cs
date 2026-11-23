using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;

namespace Fiefdom;

/// <summary>
/// Fiefdom's interface. One line on the outpost panel: "Make a town" on an outpost that is not one, "Town window" on one that is, which opens the
/// Fiefdom window with five tabs: the town's buildings and storage, its settler settings and steward, the races it lets in, its residents,
/// groups at the gate and news, and its work posts. On the panel of a selected building in a Fiefdom town, two buttons switch its rules
/// (residents live here; every box inside is town stock); on a storage box's panel, one button switches it between town stock and the player's
/// own. A notice at the top of the screen asks about a group waiting at a town while the
/// window is closed. Right-clicking one of the player's characters in a town offers Make steward (or Stop being steward), and a resident offers
/// Evict, which asks first. Everything here runs on the interface's thread: it reads only <see cref="FiefdomSystem.View"/> and the contexts it is
/// given, and changes the world only by queueing commands.
/// </summary>
sealed class FiefdomUi : IUiExtension
{
    #region Open windows

    /// <summary>
    /// The open Fiefdom window: the town it shows.
    /// </summary>
    sealed record Window(int Town);

    /// <summary>
    /// The eviction waiting for the player's yes: the resident, their name and their town's.
    /// </summary>
    sealed record Confirm(CharacterId Character, string Name, string Town);

    /// <summary>
    /// The open window and its tab, and the eviction asked about; null when closed. Set and read on the interface's thread.
    /// </summary>
    static Window? s_window;
    static int s_tab;
    static Confirm? s_confirm;

    /// <summary>
    /// The tabs' names.
    /// </summary>
    static readonly string[] Tabs = ["Town", "Settlers", "Races", "Residents", "Work", "Policies"];

    /// <summary>
    /// The Races tab's page, and how many rows of it fit on one page.
    /// </summary>
    static int s_racePage;
    const int RaceRowsPerPage = 12;

    /// <summary>
    /// The Work tab's page, how many lines fit on one, whether it shows only lines with places open, the line whose people are shown (null: the
    /// list), and whether the people view is listing residents to add.
    /// </summary>
    static int s_workPage;
    const int LinesPerPage = 10;
    static bool s_openOnly;
    static string? s_line;
    static bool s_picking;

    /// <summary>
    /// The Residents tab's page of people, and how many fit on one.
    /// </summary>
    static int s_peoplePage;
    const int PeoplePerPage = 12;

    /// <summary>
    /// The Policies tab's page and how many races a page shows.
    /// </summary>
    static int s_policyPage;
    const int PoliciesPerPage = 12;

    /// <summary>
    /// The extension's id (its registrations' prefix).
    /// </summary>
    public string Id => FiefdomSystem.ModId;

    #endregion

    #region Registration

    /// <summary>
    /// The text colours: the game's panel ink, a dimmer one for notes, and the lit button's.
    /// </summary>
    static readonly Vector4 Ink = new(0.86f, 0.82f, 0.71f, 1), Dim = new(0.62f, 0.59f, 0.52f, 1), Lit = new(1f, 0.85f, 0.45f, 1);

    /// <summary>
    /// The style and action keys, and the node ids.
    /// </summary>
    sealed record Keys(string Panel, string Title, string Text, string Note, string Button, string Chosen, string Small, string SmallChosen,
        string Backdrop,
        string Do, Func<string, string> Node);

    /// <summary>
    /// Registers the panel line, the building buttons, the right-click rows, the styles, the actions and the overlay's windows.
    /// </summary>
    public void Register(UiRegistration registration)
    {
        registration.AddTownPanelLines(TownLines);
        registration.AddBuildingActions(BuildingButtons);
        registration.AddCharacterMenuVerbs(MenuRows);

        var buttonStyle = new UiStyle { Skin = "Kenshi_Button1Skin", Text = Ink, TextAlign = "Center", FontName = "Kenshi_PaintedTextFont_Medium" };
        var keys = new Keys(
            Panel: registration.RegisterStyle("panel", new UiStyle { Skin = "Kenshi_FloatingPanelSkin", Background = new(0.1f, 0.09f, 0.08f, 0.94f), Text = Ink }),
            Title: registration.RegisterStyle("title", new UiStyle { Text = Ink, TextAlign = "Center", FontName = "Kenshi_StandardFont_Medium" }),
            Text: registration.RegisterStyle("text", new UiStyle { Text = Ink }),
            Note: registration.RegisterStyle("note", new UiStyle { Text = Dim }),
            Button: registration.RegisterStyle("button", buttonStyle),
            Chosen: registration.RegisterStyle("chosen", buttonStyle with { Text = Lit }),
            Small: registration.RegisterStyle("small", buttonStyle with { FontName = "Kenshi_StandardFont_Medium", TextScale = 0.85f }),
            SmallChosen: registration.RegisterStyle("small-chosen", buttonStyle with { FontName = "Kenshi_StandardFont_Medium", TextScale = 0.85f, Text = Lit }),
            Backdrop: registration.RegisterStyle("backdrop", new UiStyle()),
            Do: registration.RegisterAction("do", c => Do(c.Argument)),
            Node: registration.Key);

        registration.Contribute(UiSlots.Overlay, frame => BuildConfirm(frame, keys), order: 1003);
        registration.Contribute(UiSlots.Overlay, frame => BuildWindow(frame, keys), order: 1000);
        registration.Contribute(UiSlots.Overlay, frame => BuildNotice(frame, keys), order: 999);
    }

    /// <summary>
    /// Queues a command for the world at the tick now.
    /// </summary>
    static void Send(SimCommand command)
    {
        if (FiefdomSystem.Context is { } context) context.Enqueue(command with { Tick = context.Tick });
    }

    #endregion

    #region The outpost panel line

    /// <summary>
    /// The outpost panel's line: one line for every outpost of the player's, always the same shape.
    /// </summary>
    static IEnumerable<UiTownLine> TownLines(UiTownContext context)
    {
        if (!context.Owned) return [];

        int town = context.Town.Number;
        if (!FiefdomSystem.View.Towns.Contains(town))
        {
            const string makeTip = "Turn this outpost into a Fiefdom town. Nothing changes until you let residents live in a building and open it to settlers.";
            return [new UiTownLine("Fiefdom", "Make a town", makeTip, () => context.Enqueue(new SetTown(town, true) { Tick = context.Tick }))];
        }

        const string openTip = "Open the Fiefdom window: the town's buildings, its settlers, its residents and its work posts.";
        return [new UiTownLine("Fiefdom", "Town window", openTip, () => Open(town))];
    }

    /// <summary>
    /// Opens the window on a town, on its first tab.
    /// </summary>
    static void Open(int town)
    {
        s_window = new Window(town);
        s_tab = 0;
    }

    #endregion

    #region The building buttons

    /// <summary>
    /// The selected building's buttons: its two rules for a building that can take rules, the storage mark for a storage box; nothing for anything
    /// else or anything outside a Fiefdom town.
    /// </summary>
    static IEnumerable<UiBuildingAction> BuildingButtons(UiBuildingContext context)
    {
        var building = context.Building;
        var view = FiefdomSystem.View;
        if (!building.Owned || building.SiteId is not { } site || !view.SiteTown.ContainsKey(site)) return [];

        if (view.Marks.TryGetValue(site, out var mark)) return [MarkButton(context, site, mark)];
        if (building.Record is not { } record || !FiefdomSystem.RuleRecords.Contains(record)) return [];

        var rules = view.Rules.GetValueOrDefault(site);
        bool home = (rules & BuildingRule.Residents) != 0, guards = (rules & BuildingRule.Guards) != 0, stock = (rules & BuildingRule.TownStock) != 0;
        var living = guards ? Living.Guards : home ? Living.Residents : Living.Nobody;
        var next = (Living)(((int)living + 1) % 3);
        string tip = living switch
        {
            Living.Residents => "Settlers move in here, one per bed. Press to make it a guardhouse instead (once nobody lives here).",
            Living.Guards => "The town's guards live here, one per bed: choose who in the Fiefdom window's Work tab. Press to house nobody (once the guards are off).",
            _ => "Nobody the town places lives here. Press to let settlers move in, one per bed.",
        };
        return
        [
            new UiBuildingAction($"Lives here: {(living == Living.Guards ? "Guards" : living == Living.Residents ? "Residents" : "Nobody")}", tip,
                () => context.Enqueue(new SetLiving(site, next) { Tick = context.Tick })),
            new UiBuildingAction(stock ? "Boxes inside: All town stock" : "Boxes inside: As marked",
                stock ? "Every storage box inside is town stock, whatever its own mark. Press to go by each box's own mark."
                    : "Each storage box inside keeps its own mark. Press to make every box inside town stock.",
                () => context.Enqueue(new SetRule(site, BuildingRule.TownStock, !stock) { Tick = context.Tick })),
        ];
    }

    /// <summary>
    /// The storage button: shows the box's mark and switches it, unless it stands in a building whose boxes are all town stock.
    /// </summary>
    static UiBuildingAction MarkButton(UiBuildingContext context, string site, StorageMark mark)
    {
        if (FiefdomSystem.View.InStorehouse.Contains(site))
            return new UiBuildingAction("Storage: Town stock (building)", "Every box in this building is town stock: change it on the building's own panel.", static () => { });

        var next = mark == StorageMark.TownStock ? StorageMark.KeepForMe : StorageMark.TownStock;
        string tip = mark == StorageMark.TownStock
            ? "Town stock: townspeople eat from it, fetch their machines' goods from it and put what they make in it; merchants stock their counters from it. Press to keep it for yourself."
            : "Kept for you: townspeople and guards never take from it or put anything in it. Press to make it town stock.";
        return new UiBuildingAction($"Storage: {Names.Of(mark)}", tip, () => context.Enqueue(new SetMark(site, next) { Tick = context.Tick }));
    }

    #endregion

    #region Right-click rows

    /// <summary>
    /// The rows on one of the player's characters: Evict on a resident; on anyone standing in a Fiefdom town, Make steward, or Stop being steward
    /// on the town's steward.
    /// </summary>
    static IEnumerable<UiMenuVerb> MenuRows(UiMenuContext context)
    {
        var target = context.Target;
        if (!target.IsPlayer) return [];

        var view = FiefdomSystem.View;
        var rows = new List<UiMenuVerb>();
        if (view.Residents.TryGetValue(target.Id, out int home) && view.Details.TryGetValue(home, out var homeTown))
        {
            var who = new Confirm(target.Id, target.Name, homeTown.Name);
            rows.Add(new UiMenuVerb("Evict from town", () => s_confirm = who));
        }

        var at = new Vector2(target.Position.X, target.Position.Z);
        foreach (var area in view.Areas)
        {
            if (Vector2.Distance(at, area.Centre) > area.Radius) continue;

            int town = area.Town;
            var id = target.Id;
            bool steward = view.Details.TryGetValue(town, out var detail) && detail.StewardId == id;
            rows.Add(steward
                ? new UiMenuVerb("Stop being steward", () => Send(new AppointSteward(town, CharacterId.None) { Tick = 0 }))
                : new UiMenuVerb("Make town steward", () => Send(new AppointSteward(town, id) { Tick = 0 })));
            break;
        }
        return rows;
    }

    /// <summary>
    /// The eviction question: the resident's name, Evict and Cancel, over a clear backdrop that cancels it.
    /// </summary>
    static UiNode BuildConfirm(UiFrameContext frame, Keys keys)
    {
        if (s_confirm is not { } confirm) return new UiNode(keys.Node("confirm-backdrop")) { Visible = false };

        var sheet = new Sheet(keys, 460);
        sheet.Title("confirm-title", $"Evict {confirm.Name}?");
        sheet.Line("confirm-text", $"They leave {confirm.Town} and your faction for good, as a dismissed character does.", note: true);
        sheet.Buttons(("confirm-yes", "Evict", "evict", false), ("confirm-no", "Cancel", "cancel-evict", false));
        return sheet.Centred(frame.Viewport, "confirm", keys.Do, "cancel-evict");
    }

    #endregion

    #region Actions

    /// <summary>
    /// The window's, the notice's and the question's buttons, by their argument.
    /// </summary>
    static void Do(string argument)
    {
        int colon = argument.IndexOf(':');
        string verb = colon < 0 ? argument : argument[..colon], value = colon < 0 ? "" : argument[(colon + 1)..];
        var window = s_window;
        var town = window is null ? null : FiefdomSystem.View.Details.GetValueOrDefault(window.Town);
        switch (verb)
        {
            case "tab":
                s_tab = Math.Clamp(Number(value), 0, Tabs.Length - 1);
                s_racePage = 0;
                s_workPage = 0;
                s_peoplePage = 0;
                s_line = null;
                s_picking = false;
                break;
            case "race-page":
                s_racePage = Math.Max(0, s_racePage + Number(value));
                break;
            case "group" when town is not null:
                var members = town.Races.Where(r => r.Group == value).ToList();
                bool block = members.Any(r => !r.Blocked);
                foreach (var r in members)
                    if (r.Blocked != block) Send(new SetRaceBlocked(town.Number, r.Id, block) { Tick = 0 });
                break;
            case "close":
                s_window = null;
                break;
            case "stop" when window is not null:
                s_window = null;
                Send(new SetTown(window.Town, false) { Tick = 0 });
                break;
            case "open" when town is not null:
                Send(new SetOpen(town.Number, !town.Open) { Tick = 0 });
                break;
            case "cap" when town is not null:
                Send(new SetCap(town.Number, town.Cap + Number(value)) { Tick = 0 });
                break;
            case "pace" when town is not null:
                Send(new SetPace(town.Number, (ArrivalPace)(((int)town.Pace + 1) % 3)) { Tick = 0 });
                break;
            case "harmed" when town is not null:
                Send(new SetAllowHarmed(town.Number, !town.AllowHarmed) { Tick = 0 });
                break;
            case "race" when town is not null && town.Races.FirstOrDefault(r => r.Id == value) is { } race:
                Send(new SetRaceBlocked(town.Number, race.Id, !race.Blocked) { Tick = 0 });
                break;
            case "steward-clear" when town is not null:
                Send(new AppointSteward(town.Number, CharacterId.None) { Tick = 0 });
                break;
            case "accept":
                Send(new AnswerGroup(Number(value), true) { Tick = 0 });
                break;
            case "refuse":
                Send(new AnswerGroup(Number(value), false) { Tick = 0 });
                break;
            case "show":
                Open(Number(value));
                s_tab = 3;
                break;
            case "evict" when s_confirm is { } confirm:
                s_confirm = null;
                Send(new EvictResident(confirm.Character) { Tick = 0 });
                break;
            case "cancel-evict":
                s_confirm = null;
                break;
            case "work-page":
                s_workPage = Math.Max(0, s_workPage + Number(value));
                break;
            case "people-page":
                s_peoplePage = Math.Max(0, s_peoplePage + Number(value));
                break;
            case "work-filter":
                s_openOnly = value == "1";
                s_workPage = 0;
                break;
            case "work-idle" when town is not null:
                Send(new PutIdleToWork(town.Number) { Tick = 0 });
                break;
            case "hours" when town is not null && Pick(town.Lines, Number(value)) is { } cycled:
                Send(new SetLineHours(town.Number, cycled.Key, (Hours)(((int)cycled.Hours + 1) % 4)) { Tick = 0 });
                break;
            case "set-hours" when town is not null && LineNamed(town) is { } shown:
                Send(new SetLineHours(town.Number, shown.Key, (Hours)Number(value)) { Tick = 0 });
                break;
            case "plus" when town is not null && Pick(town.Lines, Number(value)) is { } more:
                Send(new AddWorker(town.Number, more.Key, CharacterId.None) { Tick = 0 });
                break;
            case "minus" when town is not null && Pick(town.Lines, Number(value)) is { } fewer:
                Send(new RemoveWorker(town.Number, fewer.Key) { Tick = 0 });
                break;
            case "people" when town is not null && Pick(town.Lines, Number(value)) is { } opened:
                s_line = opened.Key;
                s_picking = false;
                break;
            case "people-back":
                s_line = null;
                s_picking = false;
                break;
            case "pick":
                s_picking = !s_picking;
                break;
            case "add-name" when town is not null && LineNamed(town) is { } into && Pick(town.Unposted, Number(value)) is { } person:
                s_picking = false;
                Send(new AddWorker(town.Number, into.Key, person.Id) { Tick = 0 });
                break;
            case "swap" when town is not null && LineNamed(town) is { } swapped && Pick(swapped.Holders, Number(value)) is { } mover:
                Send(new SetShift(mover.Id, mover.Shift == Shift.Day ? Shift.Night : Shift.Day) { Tick = 0 });
                break;
            case "take-off" when town is not null && LineNamed(town) is { } from && Pick(from.Holders, Number(value)) is { } leaver:
                Send(new ClearPost(leaver.Id) { Tick = 0 });
                break;
            case "attack" when town is not null && Pick(town.Policies, Number(value)) is { } policy:
                Send(new SetAttackOnSight(town.Number, policy.Race, !policy.Attack) { Tick = 0 });
                break;
            case "policy-page":
                s_policyPage = Math.Max(0, s_policyPage + Number(value));
                break;
            case "duty" when town is not null && Pick(town.Lines, Number(value)) is { } guarded:
                Send(new SetDuty(guarded.Sites[0], !guarded.Gate) { Tick = 0 });
                break;
            case "set-duty" when town is not null && LineNamed(town) is { } duty:
                Send(new SetDuty(duty.Sites[0], value == "1") { Tick = 0 });
                break;
            case "guard-off" when town is not null && LineNamed(town) is { } house && Pick(house.Holders, Number(value)) is { } guard:
                Send(new ReleaseGuard(guard.Id) { Tick = 0 });
                break;
            case "sells" when town is not null && LineNamed(town) is { } counters:
                foreach (var site in counters.Sites) Send(new SetSells(site, counters.Sells ^ (Goods)Number(value)) { Tick = 0 });
                break;
        }
    }

    /// <summary>
    /// A whole number from an argument (0 when it is not one).
    /// </summary>
    static int Number(string text) => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>
    /// The item at <paramref name="index"/>, or null.
    /// </summary>
    static T? Pick<T>(IReadOnlyList<T> list, int index) where T : class => index >= 0 && index < list.Count ? list[index] : null;

    /// <summary>
    /// The line of work whose people the Work tab shows, or null.
    /// </summary>
    static WorkLine? LineNamed(TownView town) => s_line is { } key ? town.Lines.FirstOrDefault(l => l.Key == key) : null;

    #endregion

    #region The Fiefdom window

    /// <summary>
    /// The window: the town's name, the tab buttons and the open tab; over a clear backdrop that closes it on a click elsewhere. Nothing while
    /// closed, and it closes itself when the town stops being one.
    /// </summary>
    static UiNode BuildWindow(UiFrameContext frame, Keys keys)
    {
        var hidden = new UiNode(keys.Node("window-backdrop")) { Visible = false };
        if (s_window is not { } window) return hidden;

        var fiefdom = FiefdomSystem.View;
        if (!fiefdom.Towns.Contains(window.Town) || !fiefdom.Details.TryGetValue(window.Town, out var town))
        {
            s_window = null;
            return hidden;
        }

        var sheet = new Sheet(keys, 660);
        sheet.Title("window-title", $"Fiefdom: {town.Name}");
        sheet.Buttons([.. Tabs.Select((name, i) => ($"tab-{i}", name, $"tab:{i}", i == s_tab))]);
        sheet.Gap(6);
        switch (s_tab)
        {
            case 1:
                SettlersTab(sheet, town);
                break;
            case 2:
                RacesTab(sheet, town);
                break;
            case 3:
                ResidentsTab(sheet, town);
                break;
            case 5:
                PoliciesTab(sheet, town);
                break;
            case 4:
                WorkTab(sheet, town);
                break;
            default:
                TownTab(sheet, fiefdom.Counts.GetValueOrDefault(window.Town), town);
                break;
        }
        sheet.Gap(6);
        if (s_tab == 0) sheet.Buttons(("window-stop", "Stop being a town", "stop", false), ("window-close", "Close", "close", false));
        else sheet.Buttons(("window-close", "Close", "close", false));
        return sheet.Centred(frame.Viewport, "window", keys.Do, "close");
    }

    /// <summary>
    /// The Town tab: the buildings, their rules, the posts and the storage boxes.
    /// </summary>
    static void TownTab(Sheet sheet, TownCounts? counts, TownView town)
    {
        counts ??= new TownCounts(0, 0, 0, 0, 0);
        sheet.Line("town-buildings", $"Buildings: {counts.Buildings}. Residents live in {counts.Homes}; every box is town stock in {counts.Storehouses}.");
        sheet.Line("town-posts", $"Work posts: {town.Posts.Count} (see the Work tab).");
        sheet.Line("town-boxes", $"Storage boxes: {counts.Boxes} ({counts.TownStock} town stock, {counts.Boxes - counts.TownStock} kept for you)");
        sheet.Line("town-how-1", "What a building is for comes from its furniture: beds, machines, counters, turrets.", note: true);
        sheet.Line("town-how-2", "Select a building to let residents live there; select a box to make it town stock or keep it for yourself.", note: true);
        sheet.Line("town-how-3", "A building's own Open to public switch decides whether outsiders may come in.", note: true);
    }

    /// <summary>
    /// The Settlers tab: open or closed, the steward, the pull and when the next group comes, the cap, the pace, the weather rule and the races.
    /// </summary>
    static void SettlersTab(Sheet sheet, TownView town)
    {
        sheet.Buttons(("set-open", town.Open ? "Settlers: Open" : "Settlers: Closed", "open", town.Open));
        sheet.Line("set-next", town.NextGroup);
        sheet.Line("set-steward", $"Steward: {town.Steward}");
        sheet.Line("set-steward-note", town.StewardNote, note: true);
        if (!town.StewardId.IsNone) sheet.Buttons(("set-steward-clear", "Remove steward (then you answer every group)", "steward-clear", false));
        sheet.Line("set-pull", $"Pull: {town.Pull} of 100");
        sheet.Line("set-pull-why", $"   {string.Join(", ", town.PullReasons)}", note: true);
        sheet.Buttons(("set-cap-down", "Cap -5", "cap:-5", false), ("set-cap", $"Cap: {town.Cap}", "cap:0", false),
            ("set-cap-up", "Cap +5", "cap:5", false), ("set-pace", $"Pace: {town.Pace}", "pace", false));
        sheet.Line("set-region", town.Harms.Length > 0
            ? $"Region: {town.Region}. Its weather can bring {town.Harms}."
            : $"Region: {town.Region}. Its weather harms no one.");
        sheet.Buttons(("set-harmed", town.AllowHarmed ? "Let in people the weather hurts: Yes" : "Let in people the weather hurts: No", "harmed",
            town.AllowHarmed));
        int kept = town.Races.Count(r => r.Blocked);
        sheet.Line("set-races", town.Races.Count == 0 ? "No settler groups come from this region."
            : $"Races: {town.Races.Count - kept} welcome, {kept} kept out. Choose them on the Races tab.", note: true);
    }

    /// <summary>
    /// The Races tab: the races the town's region could send, under the game's own race groups (from the data, so races and groups other mods add
    /// show up by themselves). Each group has a button that keeps all of it out or lets all of it in, and a group of several races a smaller button
    /// per race. Paged when it is long.
    /// </summary>
    static void RacesTab(Sheet sheet, TownView town)
    {
        if (town.Races.Count == 0)
        {
            sheet.Line("race-none", "No settler groups come from this region, so no races to choose.", note: true);
            return;
        }
        sheet.Line("race-how", "Races who may settle here, by the game's race groups. Press to keep one out or let it in.", note: true);
        sheet.Line("race-how-2", "A race not kept out is welcome, so races added by other mods are welcome until you say otherwise.", note: true);

        // Each group is a row of its own, then its races three to a row.
        var rows = new List<(string Id, string Caption, string Argument, bool Lit, bool Small)[]>();
        int n = 0;
        foreach (var group in town.Races.GroupBy(r => r.Group))
        {
            var races = group.ToList();
            int kept = races.Count(r => r.Blocked);
            string state = kept == 0 ? "all welcome" : kept == races.Count ? "all kept out" : $"{kept} of {races.Count} kept out";
            rows.Add([($"race-group-{n++}", $"{group.Key}: {state}", $"group:{group.Key}", kept == 0, false)]);
            if (races.Count < 2) continue;
            for (int i = 0; i < races.Count; i += 3)
                rows.Add([.. races.Skip(i).Take(3).Select(r =>
                    ($"race-{n++}", r.Blocked ? $"{r.Name}: out" : r.Name, $"race:{r.Id}", !r.Blocked, true))]);
        }

        int pages = (rows.Count + RaceRowsPerPage - 1) / RaceRowsPerPage;
        s_racePage = Math.Min(s_racePage, pages - 1);
        foreach (var row in rows.Skip(s_racePage * RaceRowsPerPage).Take(RaceRowsPerPage)) sheet.Buttons(row);
        if (pages > 1)
            sheet.Buttons(("race-prev", "Previous page", "race-page:-1", false), ("race-page", $"Page {s_racePage + 1} of {pages}", "race-page:0", false),
                ("race-next", "Next page", "race-page:1", false));
    }

    /// <summary>
    /// The Residents tab: residents, beds and homes, the groups coming or waiting (with Accept and Refuse), and the town's news.
    /// </summary>
    static void ResidentsTab(Sheet sheet, TownView town)
    {
        sheet.Line("res-count", $"Residents: {town.Residents} (cap {town.Cap}). Beds in homes: {town.Beds}, {town.FreeBeds} free.");
        if (town.Homes.Count == 0) sheet.Line("res-nohome", "No homes yet: select a building with beds and let residents live there.", note: true);
        for (int i = 0; i < town.Homes.Count; i++)
            sheet.Line($"res-home-{i}", $"   {town.Homes[i].Name}: {town.Homes[i].Residents} living in, {town.Homes[i].Beds} beds");

        if (town.People.Count > 0)
        {
            int pages = (town.People.Count + PeoplePerPage - 1) / PeoplePerPage;
            s_peoplePage = Math.Min(s_peoplePage, pages - 1);
            sheet.Line("res-people", "Who does what:", note: true);
            for (int i = s_peoplePage * PeoplePerPage; i < Math.Min(town.People.Count, (s_peoplePage + 1) * PeoplePerPage); i++)
            {
                var person = town.People[i];
                sheet.Line($"res-person-{i}", person.Post.Length > 0 ? $"   {person.Name}: {person.Post}" : $"   {person.Name}: no post");
            }
            if (pages > 1)
                sheet.Buttons(("people-prev", "Previous page", "people-page:-1", false), ("people-pages", $"Page {s_peoplePage + 1} of {pages}", "people-page:0", false),
                    ("people-next", "Next page", "people-page:1", false));
        }

        foreach (var group in town.Groups)
        {
            sheet.Line($"res-group-{group.Serial}", group.Text);
            if (!group.Waiting) continue;
            if (group.CannotAccept is { } why) sheet.Line($"res-group-why-{group.Serial}", $"Can't take them in: {why}.", note: true);
            if (group.CannotAccept is null)
                sheet.Buttons(($"res-accept-{group.Serial}", "Accept", $"accept:{group.Serial}", false),
                    ($"res-refuse-{group.Serial}", "Refuse", $"refuse:{group.Serial}", false));
            else sheet.Buttons(($"res-refuse-{group.Serial}", "Refuse", $"refuse:{group.Serial}", false));
        }

        sheet.Line("res-news", "News:", note: true);
        if (town.News.Count == 0) sheet.Line("res-news-none", "   Nothing yet.", note: true);
        for (int i = town.News.Count - 1; i >= 0; i--) sheet.Line($"res-news-{i}", $"   {town.News[i]}");
    }

    /// <summary>
    /// The Work tab: one line for each kind of work in each building (the same furniture together), with the hours it keeps (a button that cycles
    /// them), how many work it of how many places, minus and plus (taking off, or putting on someone without a post), and People. Paged; can show
    /// only lines with places open. People shows one line instead.
    /// </summary>
    static void WorkTab(Sheet sheet, TownView town)
    {
        if (town.Lines.Count == 0)
        {
            sheet.Line("work-none", "No work posts yet.");
            sheet.Line("work-none-how", "Furniture people work makes posts: benches, farms, wells, mines, refineries, gun turrets and shop counters. A building whose Lives here is Guards makes guard places, one per bed.", note: true);
            return;
        }
        if (LineNamed(town) is { } shown)
        {
            PeopleView(sheet, town, shown);
            return;
        }
        s_line = null;

        sheet.Line("work-count", $"Residents: {town.Residents}. At work: {town.Residents - town.Unposted.Count}. Without a post: {town.Unposted.Count}.");
        string bill = town.LastPaid > 0 || town.LastUnpaid > 0
            ? $" Last day change: {town.LastPaid} cats paid{(town.LastUnpaid > 0 ? $", {town.LastUnpaid} went unpaid" : "")}." : "";
        sheet.Line("work-wage", $"Wage: {FiefdomSettings.Current.WagePerShift} cats a shift worked, guards {FiefdomSettings.Current.GuardWagePerDay} a day, from your money.{bill}", note: true);
        sheet.Line("work-share", $"Merchants take no wage: they keep {FiefdomSettings.Current.MerchantSharePercent}% of what their counter takes, and the rest is yours.", note: true);
        sheet.Line("work-how", $"Day {FiefdomSystem.DayShiftStarts}:00 to {FiefdomSystem.NightShiftStarts}:00, night the rest. Workers step away to eat when hungry.", note: true);
        if (town.Unposted.Count > 0)
            sheet.Buttons(("work-all", "Show all", "work-filter:0", !s_openOnly, true), ("work-open", "Places open", "work-filter:1", s_openOnly, true),
                ("work-idle", $"Put {town.Unposted.Count} without a post to work", "work-idle", false, true));
        else
            sheet.Buttons(("work-all", "Show all", "work-filter:0", !s_openOnly, true), ("work-open", "Places open", "work-filter:1", s_openOnly, true));

        var shownLines = Enumerable.Range(0, town.Lines.Count).Where(i => !s_openOnly || town.Lines[i].Holders.Count < town.Lines[i].Capacity).ToList();
        sheet.Cells(("work-head-name", "Work, where", null, 4.6f, false), ("work-head-hours", "Hours", null, 2.1f, false),
            ("work-head-count", "Workers", null, 2.6f, false), ("work-head-people", "", null, 1.4f, false));
        if (shownLines.Count == 0) sheet.Line("work-all-full", "Every place is filled.", note: true);
        int pages = Math.Max(1, (shownLines.Count + LinesPerPage - 1) / LinesPerPage);
        s_workPage = Math.Min(s_workPage, pages - 1);
        foreach (int i in shownLines.Skip(s_workPage * LinesPerPage).Take(LinesPerPage))
        {
            var line = town.Lines[i];
            sheet.Cells(($"work-line-{i}", $"{line.Name}, {line.Where}", null, 4.6f, false),
                line.Kind == PostKind.Guard ? ($"work-hours-{i}", line.Gate ? "Gates" : "Patrol", $"duty:{i}", 2.1f, line.Gate)
                    : line.Kind == PostKind.Runner ? ($"work-hours-{i}", "Day and night", null, 2.1f, false)
                    : ($"work-hours-{i}", HoursName(line.Hours), $"hours:{i}", 2.1f, line.Hours != Hours.DayAndNight),
                ($"work-minus-{i}", "-", $"minus:{i}", 0.7f, false),
                ($"work-n-{i}", $"{line.Holders.Count}/{line.Capacity}", null, 1.2f, false),
                ($"work-plus-{i}", "+", $"plus:{i}", 0.7f, false),
                ($"work-people-{i}", "People", $"people:{i}", 1.4f, false));
        }
        if (pages > 1)
            sheet.Buttons(("work-prev", "Previous page", "work-page:-1", false), ("work-page", $"Page {s_workPage + 1} of {pages}", "work-page:0", false),
                ("work-next", "Next page", "work-page:1", false));
    }

    /// <summary>
    /// One line of work: its hours to choose, who works it (with their shift, why they are away, and Take off), Add someone by name, and for
    /// counters what they sell.
    /// </summary>
    static void PeopleView(Sheet sheet, TownView town, WorkLine line)
    {
        sheet.Line("people-title", $"{line.Name}, {line.Where}");
        sheet.Line("people-count", $"{line.Holders.Count} of {line.Capacity} places filled.", note: true);
        if (line.Kind is PostKind.Guard or PostKind.Runner)
        {
            GuardView(sheet, town, line);
            return;
        }
        Hours[] all = [Hours.DayAndNight, Hours.Day, Hours.Night, Hours.TwoShifts];
        sheet.Buttons([.. all.Select(h => ($"people-hours-{(int)h}", HoursName(h), $"set-hours:{(int)h}", line.Hours == h, true))]);
        for (int j = 0; j < line.Holders.Count; j++)
        {
            var h = line.Holders[j];
            string away = h.Unpaid ? " (unpaid, stopped)" : h.Eating ? " (eating)" : "";
            if (line.Hours == Hours.TwoShifts)
                sheet.Cells(($"people-name-{j}", $"{h.Name}: {Names.Of(h.Shift)}{away}", null, 4.4f, false),
                    ($"people-swap-{j}", h.Shift == Shift.Day ? "Move to night" : "Move to day", $"swap:{j}", 2f, false),
                    ($"people-off-{j}", "Take off", $"take-off:{j}", 1.6f, false));
            else
                sheet.Cells(($"people-name-{j}", $"{h.Name}: {Names.Of(h.Shift)}{away}", null, 6.4f, false),
                    ($"people-off-{j}", "Take off", $"take-off:{j}", 1.6f, false));
        }
        if (line.Holders.Count < line.Capacity)
        {
            sheet.Buttons(("people-pick", s_picking ? "Close the list" : "Add someone by name", "pick", s_picking));
            if (s_picking && town.Unposted.Count == 0) sheet.Line("people-pick-none", "Everyone already has a post.", note: true);
            if (s_picking)
                for (int i = 0; i < Math.Min(town.Unposted.Count, 24); i += 3)
                    sheet.Buttons([.. town.Unposted.Skip(i).Take(3).Select((p, k) => ($"people-person-{i + k}", p.Name, $"add-name:{i + k}", false, true))]);
        }
        if (line.Kind == PostKind.Counter)
        {
            sheet.Line("people-sells", "Sells:", note: true);
            SellsRows(sheet, line.Sells);
        }
        sheet.Buttons(("people-back", "Back to all work", "people-back", false));
    }

    /// <summary>
    /// The Policies tab: every race (people or animals) seen in or near the town lately, and any the guards attack on sight, each with a switch:
    /// leave be, or attack on sight inside the town. Paged.
    /// </summary>
    static void PoliciesTab(Sheet sheet, TownView town)
    {
        sheet.Line("policy-how", "Guards attack anyone of a marked race who comes inside the town.", note: true);
        sheet.Line("policy-how-2", "Attacks cost you with that race's factions. Lists who came near this week.", note: true);
        if (town.Policies.Count == 0)
        {
            sheet.Line("policy-none", "Nobody but your own people has come near the town lately.");
            return;
        }
        int pages = (town.Policies.Count + PoliciesPerPage - 1) / PoliciesPerPage;
        s_policyPage = Math.Min(s_policyPage, pages - 1);
        for (int i = s_policyPage * PoliciesPerPage; i < Math.Min(town.Policies.Count, (s_policyPage + 1) * PoliciesPerPage); i++)
        {
            var p = town.Policies[i];
            sheet.Cells(($"policy-name-{i}", $"{p.Name} ({p.Seen})", null, 5.6f, false),
                ($"policy-attack-{i}", p.Attack ? "Attack on sight" : "Leave be", $"attack:{i}", 2.4f, p.Attack));
        }
        if (pages > 1)
            sheet.Buttons(("policy-prev", "Previous page", "policy-page:-1", false), ("policy-pages", $"Page {s_policyPage + 1} of {pages}", "policy-page:0", false),
                ("policy-next", "Next page", "policy-page:1", false));
    }

    /// <summary>
    /// One guardhouse: its duty to choose, its guards (with Take off, which sends them back to live in a home), Add someone by name.
    /// </summary>
    static void GuardView(Sheet sheet, TownView town, WorkLine line)
    {
        bool guards = line.Kind == PostKind.Guard;
        if (guards)
        {
            sheet.Buttons(("people-duty-0", "Patrol the town", "set-duty:0", !line.Gate, true), ("people-duty-1", "Watch the gates", "set-duty:1", line.Gate, true));
            sheet.Line("people-duty-how", line.Gate
                ? "One mans a gate, the rest stand guard; they fight in town and heal each other."
                : "They patrol, man turrets, fight in town and heal each other; bed at night.", note: true);
            sheet.Line("people-orders", "Guards are not yours to command: like residents, they keep to their duty on their own.", note: true);
        }
        else
        {
            sheet.Line("people-runners-how", "Day and night they bring town food to anyone at work or on guard getting hungry.", note: true);
            sheet.Line("people-runners-wage", $"Paid two shifts a day ({FiefdomSettings.Current.WagePerShift * 2} cats). One for every five people at work or on guard.", note: true);
        }
        for (int j = 0; j < line.Holders.Count; j++)
        {
            var h = line.Holders[j];
            sheet.Cells(($"people-name-{j}", $"{h.Name}{(h.Unpaid ? " (unpaid)" : "")}", null, 6.4f, false),
                ($"people-off-{j}", "Take off", guards ? $"guard-off:{j}" : $"take-off:{j}", 1.6f, false));
        }
        if (line.Holders.Count < line.Capacity)
        {
            sheet.Buttons(("people-pick", s_picking ? "Close the list" : "Add someone by name", "pick", s_picking));
            if (s_picking && town.Unposted.Count == 0) sheet.Line("people-pick-none", "Every resident already has a post.", note: true);
            if (s_picking)
                for (int i = 0; i < Math.Min(town.Unposted.Count, 24); i += 3)
                    sheet.Buttons([.. town.Unposted.Skip(i).Take(3).Select((p, k) => ($"people-person-{i + k}", p.Name, $"add-name:{i + k}", false, true))]);
        }
        else sheet.Line("people-full", guards ? "Every bed is taken: build more beds in this building for more guards." : "Every place is filled.", note: true);
        sheet.Buttons(("people-back", "Back to all work", "people-back", false));
    }

    /// <summary>
    /// The name of the hours a line keeps.
    /// </summary>
    static string HoursName(Hours hours) => hours switch
    {
        Hours.Day => "Day only",
        Hours.Night => "Night only",
        Hours.TwoShifts => "Two shifts",
        _ => "Day and night",
    };

    /// <summary>
    /// The goods kinds a counter sells, one small switch each (lit: sold), in two rows.
    /// </summary>
    static void SellsRows(Sheet sheet, Goods sells)
    {
        Goods[] kinds = [Goods.Food, Goods.Medicine, Goods.Drugs, Goods.Weapons, Goods.Armour, Goods.Bags, Goods.Other];
        var buttons = kinds.Select(k => ($"people-sells-{(int)k}", GoodsName(k), $"sells:{(int)k}", (sells & k) != 0, true)).ToArray();
        sheet.Buttons(buttons[..4]);
        sheet.Buttons(buttons[4..]);
    }

    /// <summary>
    /// A goods kind's name.
    /// </summary>
    static string GoodsName(Goods kind) => kind switch
    {
        Goods.Medicine => "medicine",
        Goods.Drugs => "drugs",
        Goods.Weapons => "weapons",
        Goods.Armour => "armor",
        Goods.Bags => "bags",
        Goods.Other => "other goods",
        _ => "food",
    };

    #endregion

    #region The notice

    /// <summary>
    /// The notice at the top of the screen while a group waits for the player's answer and the window is closed: what the group is, Accept (when
    /// it can be taken in), Refuse and Town window.
    /// </summary>
    static UiNode BuildNotice(UiFrameContext frame, Keys keys)
    {
        var hidden = new UiNode(keys.Node("notice")) { Visible = false };
        if (s_window is not null) return hidden;

        foreach (var town in FiefdomSystem.View.Details.Values)
        {
            if (town.Groups.FirstOrDefault(g => g.Waiting) is not { } group) continue;

            var sheet = new Sheet(keys, 640);
            sheet.Title("notice-title", $"Settlers at {town.Name}");
            sheet.Line("notice-text", group.Text);
            if (group.CannotAccept is { } why) sheet.Line("notice-why", $"Can't take them in: {why}.", note: true);
            var buttons = new List<(string, string, string, bool)>();
            if (group.CannotAccept is null) buttons.Add(("notice-accept", "Accept", $"accept:{group.Serial}", false));
            buttons.Add(("notice-refuse", "Refuse", $"refuse:{group.Serial}", false));
            buttons.Add(("notice-open", "Town window", $"show:{town.Number}", false));
            sheet.Buttons([.. buttons]);
            return sheet.At(new Vector2((frame.Viewport.Width - sheet.Width) / 2, 70), "notice");
        }
        return hidden;
    }

    #endregion

    #region Layout

    /// <summary>
    /// A panel laid out top to bottom: a title, lines of text and rows of buttons sharing the width.
    /// </summary>
    sealed class Sheet(Keys keys, float width)
    {
        const float Pad = 12, LineH = 22, ButtonH = 28, TitleH = 30;
        readonly List<UiNode> _nodes = [];
        float _y = Pad / 2;

        /// <summary>
        /// The panel's width.
        /// </summary>
        public float Width => width;

        /// <summary>
        /// The title line.
        /// </summary>
        public void Title(string id, string text)
        {
            _nodes.Add(new UiNode(keys.Node(id), UiNodeKind.Label) { Placement = new(width - Pad * 2, TitleH, Pad, _y), Style = keys.Title, Text = text });
            _y += TitleH;
        }

        /// <summary>
        /// A line of text (a dim note when <paramref name="note"/>).
        /// </summary>
        public void Line(string id, string text, bool note = false)
        {
            _nodes.Add(new UiNode(keys.Node(id), UiNodeKind.Label)
            {
                Placement = new(width - Pad * 2, LineH, Pad, _y), Style = note ? keys.Note : keys.Text, Text = text,
            });
            _y += LineH;
        }

        /// <summary>
        /// A row of buttons of equal width: id, caption, the action's argument, and whether it is lit.
        /// </summary>
        public void Buttons(params (string Id, string Caption, string Argument, bool Lit)[] buttons) =>
            Buttons([.. buttons.Select(b => (b.Id, b.Caption, b.Argument, b.Lit, false))]);

        /// <summary>
        /// A row of buttons of equal width, each in the small style when asked.
        /// </summary>
        public void Buttons(params (string Id, string Caption, string Argument, bool Lit, bool Small)[] buttons)
        {
            if (buttons.Length == 0) return;
            float bw = (width - Pad * (buttons.Length + 1)) / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                var (id, caption, argument, lit, small) = buttons[i];
                string style = small ? lit ? keys.SmallChosen : keys.Small : lit ? keys.Chosen : keys.Button;
                _nodes.Add(new UiNode(keys.Node(id), UiNodeKind.Button)
                {
                    Placement = new(bw, ButtonH, Pad + i * (bw + Pad), _y), Style = style, Text = caption,
                    Action = keys.Do, Argument = argument,
                });
            }
            _y += ButtonH + 4;
        }

        /// <summary>
        /// One row of text and buttons side by side, each as wide as its weight's share: id, text, the action's argument (null: text only), weight,
        /// and whether a button is lit. Buttons are in the small style.
        /// </summary>
        public void Cells(params (string Id, string Text, string? Argument, float Weight, bool Lit)[] cells)
        {
            if (cells.Length == 0) return;
            float room = width - Pad * 2 - 4 * (cells.Length - 1), total = cells.Sum(c => c.Weight), x = Pad;
            foreach (var (id, text, argument, weight, lit) in cells)
            {
                float w = room * weight / total;
                _nodes.Add(argument is null
                    ? new UiNode(keys.Node(id), UiNodeKind.Label) { Placement = new(w, ButtonH, x, _y), Style = keys.Text, Text = text }
                    : new UiNode(keys.Node(id), UiNodeKind.Button)
                    {
                        Placement = new(w, ButtonH, x, _y), Style = lit ? keys.SmallChosen : keys.Small, Text = text, Action = keys.Do, Argument = argument,
                    });
                x += w + 4;
            }
            _y += ButtonH + 4;
        }

        /// <summary>
        /// Empty space.
        /// </summary>
        public void Gap(float h) => _y += h;

        /// <summary>
        /// The panel at a point, alone.
        /// </summary>
        public UiNode At(Vector2 at, string id)
        {
            var panel = new UiNode(keys.Node(id)) { Placement = new(width, _y + Pad / 2, at.X, at.Y), Style = keys.Panel };
            panel.Children.AddRange(_nodes);
            return panel;
        }

        /// <summary>
        /// The panel in the middle of the screen over a clear backdrop whose click runs <paramref name="action"/> with
        /// <paramref name="argument"/>.
        /// </summary>
        public UiNode Centred(UiRect view, string id, string action, string argument)
        {
            float h = _y + Pad / 2;
            var panel = At(new Vector2((view.Width - width) / 2, Math.Max(10, (view.Height - h) / 2)), id);
            var root = new UiNode(keys.Node(id + "-backdrop")) { Style = keys.Backdrop, Action = action, Argument = argument };
            root.Children.Add(panel);
            return root;
        }
    }

    #endregion
}
