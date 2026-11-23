using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Modding;
using Meitou.Simulation;

namespace Fiefdom;

/// <summary>
/// The mod's entry: sorts the game's buildings into those that can take rules and storage boxes, and registers the Fiefdom system and its
/// interface.
/// </summary>
public sealed class FiefdomMod : IMod
{
    /// <summary>
    /// Sorts the buildings, then registers the system (after every standard system) and the interface.
    /// </summary>
    public void Initialize(ModContext context)
    {
        FiefdomSystem.Context = context;
        FiefdomSystem.RollPurpose = context.RngPurpose;
        context.AddSettings(FiefdomSettings.Current);
        Placement.Register(context);
        SortBuildings(context.Data);
        string settlers = SettlerData.Load(context.Data, context.Log);
        context.AddJobType((Meitou.Data.Gameplay.Ai.TaskType)FiefdomSystem.MerchantRow, "Keeping a shop counter");
        context.AddJobType((Meitou.Data.Gameplay.Ai.TaskType)FiefdomSystem.RunnerRow, "Carrying meals");
        context.AddJobType((Meitou.Data.Gameplay.Ai.TaskType)FiefdomSystem.KitRow, "Fetching a first aid kit");
        context.AddSystem(FiefdomSystem.ModId, FiefdomSystem.Create, SystemAnchor.End);
        context.AddUi(new FiefdomUi());
        context.AddDebugCommand("Fiefdom: bring settlers now", BringNow);
        context.AddDebugCommand("Fiefdom: plan beds in homes (testing)", PlanBedsNow, hasField: true);
        context.AddDebugCommand("Fiefdom: make the selected character steward", StewardNow);
        context.AddDebugCommand("Fiefdom: residents take open work posts (testing)", PostsNow);
        context.AddDebugCommand("Fiefdom: plan a shop counter in homes (testing)", CounterNow, hasField: true);
        context.AddDebugCommand("Fiefdom: put food in town stock (testing)", FoodNow, hasField: true);
        context.AddDebugCommand("Fiefdom: plan two beds in each guardhouse (testing)", GuardBedsNow);
        context.AddDebugCommand("Fiefdom: bring visitors now (testing)", VisitorsNow);
        context.AddDebugCommand("Fiefdom: put first aid kits in town stock (testing)", KitsNow, hasField: true);
        context.AddDebugCommand("Fiefdom: log what townspeople are doing", MindsNow);
        context.AddDebugCommand("Fiefdom: set relations with a faction (testing)", RelationNow, hasField: true);
        context.Log($"{FiefdomSystem.RuleRecords.Count} buildings can take rules, {FiefdomSystem.StorageRecords.Count} are storage; {settlers}");
    }

    /// <summary>
    /// The F10 row for testing: plans beds (the field: how many per home, 1 to 8, default 3) of the game's own Bed record in every Home of every
    /// Fiefdom town, for the selection to build.
    /// </summary>
    static string PlanBedsNow(DebugCommandContext context)
    {
        int count = int.TryParse(context.Field, out int n) ? n : 3;
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new PlanBeds(town, "3460-otto.mod", count) { Tick = context.Tick });
        return $"Planned up to {count} beds per home; build them with your selection.";
    }

    /// <summary>
    /// The F10 row: the first selected character becomes steward of the Fiefdom town they stand in (the same as the right-click row).
    /// </summary>
    static string StewardNow(DebugCommandContext context)
    {
        if (context.Selected.Count == 0) return "Select one of your characters first.";
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new AppointSteward(town, context.Selected[0]) { Tick = context.Tick });
        return "Appointed, if they stand in a Fiefdom town.";
    }

    /// <summary>
    /// The F10 row for testing: plans the game's Shop Counter in every home of every Fiefdom town; the field is the offset from the home's middle,
    /// "x z" (0 0 if empty).
    /// </summary>
    static string CounterNow(DebugCommandContext context)
    {
        var parts = (context.Field ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float x = parts.Length > 0 && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float a) ? a : 0;
        float z = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float b) ? b : 0;
        foreach (int town in FiefdomSystem.View.Towns)
            context.Enqueue(new PlanFurniture(town, "3469-otto.mod", new System.Numerics.Vector3(x, 0, z)) { Tick = context.Tick });
        return $"Planned a shop counter in every home at {x} {z}; build it with your selection.";
    }

    /// <summary>
    /// The F10 row for testing: plans two beds in the middle of every guardhouse of every Fiefdom town, for the selection to build.
    /// </summary>
    static string GuardBedsNow(DebugCommandContext context)
    {
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new PlanGuardBeds(town) { Tick = context.Tick });
        return "Planned two beds in every guardhouse; build them with your selection.";
    }

    /// <summary>
    /// The F10 row for testing: every Fiefdom town's next visitors are due now.
    /// </summary>
    static string VisitorsNow(DebugCommandContext context)
    {
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new BringVisitors(town) { Tick = context.Tick });
        return "Visitors are due now in every town open to the public.";
    }

    /// <summary>
    /// The F10 row for testing: puts food (the field: how many Dried Meat, 10 if empty) into every Fiefdom town's first town-stock box.
    /// </summary>
    static string FoodNow(DebugCommandContext context)
    {
        int count = int.TryParse(context.Field, out int n) ? n : 10;
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new FillTownStock(town, "42334-changes_otto.mod", count) { Tick = context.Tick });
        return $"Put up to {count} Dried Meat in town stock.";
    }

    /// <summary>
    /// The F10 row for testing: puts the typed number (3 if empty) of the game's Basic First Aid Kit in each Fiefdom town's stock.
    /// </summary>
    static string KitsNow(DebugCommandContext context)
    {
        int count = int.TryParse(context.Field, out int n) ? n : 3;
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new FillTownStock(town, "209-gamedata.base", count) { Tick = context.Tick });
        return $"Put up to {count} Basic First Aid Kits in town stock.";
    }

    /// <summary>
    /// The F10 row for testing: sets the player's relation with a faction, both ways. The field: "name=value", "name" (100), or empty for the
    /// United Cities at 100 (allied).
    /// </summary>
    static string RelationNow(DebugCommandContext context)
    {
        string field = context.Field.Trim();
        string name = field.Length == 0 ? "United Cities" : field.Split('=')[0].Trim();
        float value = field.Contains('=') && float.TryParse(field.Split('=')[1], out float v) ? Math.Clamp(v, -100, 100) : 100;
        context.Enqueue(new SetFactionRelation(name, value) { Tick = context.Tick });
        return $"Relation with {name} set to {value}.";
    }

    /// <summary>
    /// The F10 row for bug reports: writes each resident's and guard's squad role, goal and task to the game's log, once.
    /// </summary>
    static string MindsNow(DebugCommandContext context)
    {
        if (FiefdomSystem.View.Towns.Count == 0) return "No Fiefdom town.";
        context.Enqueue(new LogMinds(FiefdomSystem.View.Towns.Min()) { Tick = context.Tick });
        return "Written to the game's log (lines starting \"fiefdom: minds\").";
    }

    /// <summary>
    /// The F10 row for testing: every resident of every Fiefdom town without a post takes an open one.
    /// </summary>
    static string PostsNow(DebugCommandContext context)
    {
        foreach (int town in FiefdomSystem.View.Towns) context.Enqueue(new TakePosts(town) { Tick = context.Tick });
        return "Residents without a post take open ones; see the log.";
    }

    /// <summary>
    /// The F10 row for testing: the next group of every open Fiefdom town is due now.
    /// </summary>
    static string BringNow(DebugCommandContext context)
    {
        var towns = FiefdomSystem.View.Towns;
        foreach (int town in towns) context.Enqueue(new BringSettlers(town) { Tick = context.Tick });
        return towns.Count == 0 ? "No Fiefdom town." : $"Next group due now in {towns.Count} town(s); an open town with a free bed brings one.";
    }

    /// <summary>
    /// The BUILDING records that can take rules (a building with an inside that is neither furniture nor a plain wall: houses and gates) and
    /// those that are storage (the game's own storage functions, with an inventory).
    /// </summary>
    static void SortBuildings(GameDatabase db)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        var storage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in db.OfType(FcsRecordType.Building))
        {
            var build = BuildingBuildData.From(record);
            if (CanTakeRules(build)) rules.Add(build.Id);
            if (MachineData.From(db, record) is { IsStorage: true }) storage.Add(build.Id);
        }
        FiefdomSystem.RuleRecords = rules;
        FiefdomSystem.StorageRecords = storage;
    }

    /// <summary>
    /// A building with an inside that is not furniture, a node, or a wall piece other than a gate.
    /// </summary>
    static bool CanTakeRules(BuildingBuildData b) =>
        b.HasInterior && !b.IsInteriorFurniture && !b.IsExteriorFurniture && !b.IsNode && !b.IsDoor
        && (!string.Equals(b.Category, "WALLS", StringComparison.OrdinalIgnoreCase) || b.LinkType == 3);
}
