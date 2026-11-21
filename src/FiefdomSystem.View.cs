using System.Globalization;
using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;

namespace Fiefdom;

/// <summary>
/// What the interface reads: the Fiefdom towns, and for every player building in one of them its town, its rules and its storage mark; each
/// town's settlers and residents; which characters are residents; and where each town lies. Made whole by the system and swapped in at once, so
/// the interface's thread always reads one consistent picture.
/// </summary>
public sealed record FiefdomView(
    IReadOnlySet<int> Towns,
    IReadOnlyDictionary<string, int> SiteTown,
    IReadOnlyDictionary<string, BuildingRule> Rules,
    IReadOnlyDictionary<string, StorageMark> Marks,
    IReadOnlySet<string> InStorehouse,
    IReadOnlyDictionary<int, TownCounts> Counts,
    IReadOnlyDictionary<int, TownView> Details,
    IReadOnlyDictionary<CharacterId, int> Residents,
    IReadOnlyList<TownArea> Areas)
{
    /// <summary>
    /// Nothing yet: no towns.
    /// </summary>
    public static readonly FiefdomView Empty = new(new HashSet<int>(), new Dictionary<string, int>(), new Dictionary<string, BuildingRule>(),
        new Dictionary<string, StorageMark>(), new HashSet<string>(), new Dictionary<int, TownCounts>(), new Dictionary<int, TownView>(),
        new Dictionary<CharacterId, int>(), []);
}

/// <summary>
/// One town's numbers for the Fiefdom window: buildings that can take rules, how many residents live in and how many keep town stock, and its
/// storage boxes by mark.
/// </summary>
public sealed record TownCounts(int Buildings, int Homes, int Storehouses, int Boxes, int TownStock);

/// <summary>
/// Where a Fiefdom town lies, for the right-click rows (which town a character stands in).
/// </summary>
public sealed record TownArea(int Town, string Name, Vector2 Centre, float Radius);

/// <summary>
/// A race the town's region could send, its group (the game's race group, else its own name) and whether the player keeps it out.
/// </summary>
public sealed record RaceChoice(string Id, string Name, string Group, bool Blocked);

/// <summary>
/// One home: its name, its residents and its beds.
/// </summary>
public sealed record HomeLine(string Name, int Residents, int Beds);

/// <summary>
/// A group on its way or at the town: what the window says of it, whether it waits for an answer, and why it cannot be taken in (null: it can).
/// </summary>
public sealed record GroupLine(int Serial, string Text, bool Waiting, string? CannotAccept);

/// <summary>
/// One town's settlers and residents for the Fiefdom window.
/// </summary>
public sealed record TownView(
    int Number,
    string Name,
    string Region,
    bool Open,
    int Cap,
    ArrivalPace Pace,
    bool AllowHarmed,
    string Harms,
    IReadOnlyList<RaceChoice> Races,
    string Steward,
    string StewardNote,
    int Pull,
    IReadOnlyList<string> PullReasons,
    string NextGroup,
    int Residents,
    int Beds,
    int FreeBeds,
    IReadOnlyList<HomeLine> Homes,
    IReadOnlyList<GroupLine> Groups,
    IReadOnlyList<string> News,
    CharacterId StewardId,
    IReadOnlyList<PostLine> Posts,
    IReadOnlyList<PersonLine> Unposted,
    IReadOnlyList<WorkLine> Lines,
    IReadOnlyList<PersonLine> People,
    long LastPaid,
    int LastUnpaid,
    IReadOnlyList<PolicyLine> Policies);

/// <summary>
/// A race (people or animals) for the Policies tab: its id and name, whether the guards attack it on sight, and when it was last seen near town.
/// </summary>
public sealed record PolicyLine(string Race, string Name, bool Attack, string Seen);

/// <summary>
/// One line of work: the same furniture in the same building (or outside). Its key, the furniture's name with how many there are, where it
/// stands, its kind, the places on each shift across all of them, the hours it keeps, its workers, its posts' building ids, and for counters
/// what they sell.
/// </summary>
public sealed record WorkLine(string Key, string Name, string Where, PostKind Kind, int Places, Hours Hours, IReadOnlyList<HolderLine> Holders,
    IReadOnlyList<string> Sites, Goods Sells)
{
    /// <summary>
    /// How many may work the line in all: twice the places when it keeps two shifts.
    /// </summary>
    public int Capacity => Hours == Hours.TwoShifts ? Places * 2 : Places;

    /// <summary>
    /// For a guard line: whether its guards watch the gates (else they patrol).
    /// </summary>
    public bool Gate { get; init; }
}

/// <summary>
/// One holder of a post: who, and on which shift.
/// </summary>
public sealed record HolderLine(CharacterId Id, string Name, Shift Shift, bool Eating = false, bool Unpaid = false);

/// <summary>
/// One post: the furniture's building id, its name (with the building it stands in), its kind, how many may work it on each shift, its holders,
/// whether the day and the night shift have a place free, and for a counter what it sells.
/// </summary>
public sealed record PostLine(string Site, string Name, PostKind Kind, int Places, IReadOnlyList<HolderLine> Holders, bool DayOpen, bool NightOpen,
    Goods Sells);

/// <summary>
/// A resident with no post.
/// </summary>
public sealed record PersonLine(CharacterId Id, string Name, string Post = "");

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// The view: the player's Fiefdom towns, every player building in them that can take rules or is a storage box, and each town's
    /// settlers and residents.
    /// </summary>
    FiefdomView Build(World world)
    {
        var towns = new HashSet<int>();
        var details = new Dictionary<int, TownView>();
        var areas = new List<TownArea>();
        foreach (var (town, state) in LiveTowns(world))
        {
            towns.Add(town.Number);
            details[town.Number] = Detail(world, town, state);
            areas.Add(new TownArea(town.Number, town.Name, Centre(town), town.Radius));
        }

        var siteTown = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in world.Buildings.All)
        {
            if (site.Owner != world.Player.Faction) continue;
            if (!RuleRecords.Contains(site.Record) && !StorageRecords.Contains(site.Record)) continue;

            int number = TownOf(world, site);
            if (towns.Contains(number)) siteTown[site.Id] = number;
        }

        var inStorehouse = new HashSet<string>(StringComparer.Ordinal);
        var marks = EffectiveMarks(world, siteTown, inStorehouse);
        return new FiefdomView(towns, siteTown, new Dictionary<string, BuildingRule>(_rules), marks, inStorehouse, Count(siteTown, marks), details,
            ResidentIds(world), areas);
    }

    /// <summary>
    /// The residents in the world now, with their town's number.
    /// </summary>
    Dictionary<CharacterId, int> ResidentIds(World world)
    {
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (town, state) in LiveTowns(world)) byKey[state.Key] = town.Number;

        var ids = new Dictionary<CharacterId, int>();
        foreach (var (id, r) in _residents)
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (!c.IsNone && byKey.TryGetValue(r.Town, out int number)) ids[c] = number;
        }
        return ids;
    }

    /// <summary>
    /// One town's settlers and residents.
    /// </summary>
    TownView Detail(World world, PlayerTown town, TownState state)
    {
        var region = RegionOf(town);
        var homes = Homes(world, town);
        int free = homes.Sum(h => h.Free), residents = ResidentsOf(state.Key);
        var (pull, reasons) = Pull(world, town, state);

        var raceIds = new SortedSet<string>(StringComparer.Ordinal);
        if (region is not null)
            foreach (var t in region.Templates) raceIds.UnionWith(t.Races);
        // A race kept out that the data no longer has (its mod removed) stays in the save but is not shown.
        raceIds.UnionWith(state.BlockedRaces.Where(SettlerData.Races.ContainsKey));
        var races = raceIds.Select(id => new RaceChoice(id, SettlerData.RaceName(id), SettlerData.GroupOf(id), state.BlockedRaces.Contains(id)))
            .OrderBy(r => r.Group, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        string steward = "none", note = "Right-click one of your characters in town and choose Make town steward.";
        var stewardId = CharacterId.None;
        if (state.Steward != 0)
        {
            var c = world.LastingIds.Find(new LastingId(state.Steward));
            if (!c.IsNone && world.Characters.TryResolveNext(c, out int slot) && world.Characters.Cold(slot) is { } cold)
            {
                int personality = PersonalityOf(world, slot, cold);
                bool here = StewardHere(world, town, state) is not null;
                stewardId = c;
                steward = $"{cold.Name} ({Steward.NameOf(personality)})";
                note = here ? Steward.Describe(personality) : "Away from town or down: groups wait for your answer until they are back.";
            }
            else
            {
                steward = "away";
                note = "Your steward is out of the world: groups wait for your answer.";
            }
        }

        var groups = new List<GroupLine>();
        foreach (var g in _groups.Values.Where(g => g.Town == state.Key)) groups.Add(GroupOf(world, town, state, g));

        return new TownView(town.Number, town.Name, region?.Name ?? "unknown", state.Open, state.Cap, state.Pace, state.AllowHarmed,
            region is { Harms.Count: > 0 } ? string.Join(", ", region.Harms.Select(SettlerData.NameOf)) : "",
            races, steward, note, pull, reasons, NextGroupText(world, state, groups.Count > 0, free, residents, pull), residents,
            homes.Sum(h => h.Beds), free, [.. homes.Select(h => new HomeLine(h.Site.Name, h.Members, h.Beds))], groups, [.. state.News], stewardId,
            PostLines(world, town, state, out var unposted), unposted, WorkLines(world, town, state), PeopleLines(world, state),
            _lastBill.GetValueOrDefault(state.Key).Paid, _lastBill.GetValueOrDefault(state.Key).Unpaid, PolicyLines(world, state));
    }

    /// <summary>
    /// The town's lines of work, in the order their first piece of furniture comes in the machine table.
    /// </summary>
    List<WorkLine> WorkLines(World world, PlayerTown town, TownState state)
    {
        var lines = new List<WorkLine>();
        foreach (var group in WorkPosts(world, town.Number).GroupBy(p => LineOf(p.Site)))
        {
            var posts = group.ToList();
            var first = posts[0].Site;
            var sites = posts.Select(p => p.Site.Id).ToList();
            var holders = new List<HolderLine>();
            foreach (var (id, h) in _holders)
            {
                if (h.Town != state.Key || !sites.Contains(h.Site)) continue;
                var c = world.LastingIds.Find(new LastingId(id));
                var pay = _pay.GetValueOrDefault(id);
                if (!c.IsNone) holders.Add(new HolderLine(c, _residents.TryGetValue(id, out var r) ? r.Name : "someone", h.Shift, pay?.Eating ?? false, pay?.Unpaid > 0));
            }
            string where = first.Parent is { } parent && world.Buildings.Find(parent) is { } outer ? $"in the {outer.Name}" : "outside";
            string name = posts.Count > 1 ? $"{first.Name} x{posts.Count}" : first.Name;
            lines.Add(new WorkLine(group.Key, name, where, posts[0].Kind, posts.Sum(p => p.Places), HoursOf(state.Key, group.Key), holders, sites,
                posts[0].Kind == PostKind.Counter ? SellsAt(first.Id) : Goods.None));
        }
        var runners = new List<HolderLine>();
        foreach (var (id, key) in _runners)
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (key == state.Key && !c.IsNone && _residents.TryGetValue(id, out var r))
                runners.Add(new HolderLine(c, r.Name, Shift.Always, false, _pay.GetValueOrDefault(id)?.Unpaid > 0));
        }
        lines.Add(new WorkLine(RunnerLine + state.Key, "Food runners", "around town", PostKind.Runner, RunnerPlaces(state.Key), Hours.DayAndNight,
            runners, [], Goods.None));
        foreach (var house in Guardhouses(world, town))
        {
            var holders = new List<HolderLine>();
            if (house.Squad is { } squad)
                foreach (var m in squad.Members)
                {
                    if (!world.Characters.TryResolveNext(m, out int slot) || !world.Characters.Next[slot].Alive
                        || world.Characters.Cold(slot) is not { LastingId.IsNone: false } cold || !_residents.TryGetValue(cold.LastingId.Value, out var r)) continue;
                    holders.Add(new HolderLine(m, r.Name, Shift.Always, false, _pay.GetValueOrDefault(cold.LastingId.Value)?.Unpaid > 0));
                }
            lines.Add(new WorkLine(GuardLine + house.Site.Id, house.Gate ? "Gate guards" : "Patrol guards", $"in the {house.Site.Name}", PostKind.Guard,
                house.Beds, Hours.DayAndNight, holders, [house.Site.Id], Goods.None) { Gate = house.Gate });
        }
        return lines;
    }

    /// <summary>
    /// The races seen near the town lately and those its guards attack on sight, by name.
    /// </summary>
    static List<PolicyLine> PolicyLines(World world, TownState state)
    {
        var races = new SortedSet<string>(state.Seen.Keys, StringComparer.Ordinal);
        races.UnionWith(state.AttackOnSight);
        return [.. races.Select(id =>
        {
            string seen = state.Seen.TryGetValue(id, out double at)
                ? world.GameHours - at < 1 ? "here now" : world.GameHours - at < 24 ? "seen today" : $"seen {Math.Floor((world.GameHours - at) / 24)} days ago"
                : "not seen lately";
            return new PolicyLine(id, SettlerData.RaceName(id), state.AttackOnSight.Contains(id), seen);
        }).OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The town's residents with what they do: their post, or none.
    /// </summary>
    List<PersonLine> PeopleLines(World world, TownState state)
    {
        var people = new List<PersonLine>();
        foreach (var (id, r) in _residents)
        {
            if (r.Town != state.Key) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone) continue;
            string post = _holders.TryGetValue(id, out var h) ? $"{NameOfSite(world, h.Site)}, {Names.Of(h.Shift)}"
                : _runners.ContainsKey(id) ? "food runner, day and night"
                : IsGuard(id) ? $"guard, {((_rules.GetValueOrDefault(r.Home) & BuildingRule.GateDuty) != 0 ? "gates" : "patrol")}, {NameOfSite(world, r.Home)}" : "";
            people.Add(new PersonLine(c, r.Name, post));
        }
        return people;
    }

    /// <summary>
    /// The town's posts with their holders, and its residents with no post.
    /// </summary>
    List<PostLine> PostLines(World world, PlayerTown town, TownState state, out List<PersonLine> unposted)
    {
        var lines = new List<PostLine>();
        foreach (var post in WorkPosts(world, town.Number))
        {
            var holders = new List<HolderLine>();
            foreach (var (id, h) in _holders)
            {
                if (h.Site != post.Site.Id) continue;
                var c = world.LastingIds.Find(new LastingId(id));
                if (!c.IsNone) holders.Add(new HolderLine(c, _residents.TryGetValue(id, out var r) ? r.Name : "someone", h.Shift));
            }
            string name = post.Site.Parent is { } parent && world.Buildings.Find(parent) is { } outer ? $"{post.Site.Name} in the {outer.Name}" : post.Site.Name;
            lines.Add(new PostLine(post.Site.Id, name, post.Kind, post.Places, holders,
                HoldersOn(post.Site.Id, Shift.Day) < post.Places, HoldersOn(post.Site.Id, Shift.Night) < post.Places,
                post.Kind == PostKind.Counter ? SellsAt(post.Site.Id) : Goods.None));
        }

        unposted = [];
        foreach (var (id, r) in _residents)
        {
            if (r.Town != state.Key || HasRole(id)) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (!c.IsNone) unposted.Add(new PersonLine(c, r.Name));
        }
        return lines;
    }

    /// <summary>
    /// What the window says of one group.
    /// </summary>
    GroupLine GroupOf(World world, PlayerTown town, TownState state, SettlerGroup g)
    {
        string name = _data.Db.Find(g.Template)?.Name ?? "travellers";
        if (g.Visiting)
            return new GroupLine(g.Serial, g.Stage == GroupStage.Travelling ? $"Visitors ({name}) are on their way." : $"Visitors ({name}) are in town.", false, null);
        if (g.Stage == GroupStage.Travelling) return new GroupLine(g.Serial, $"A group ({name}) is on its way.", false, null);

        if (PlatoonOf(world, g) is not { State: PlatoonState.Loaded } p || world.Squads.Find(p.SquadId) is not { } squad)
            return new GroupLine(g.Serial, g.Accepted ? $"A group ({name}) you took in will move in when you are near."
                : $"A group ({name}) waits outside town for your answer.", !g.Accepted, null);

        var members = Applying(world, p, squad);
        if (members.Count == 0) return new GroupLine(g.Serial, $"A group ({name}) has no one left to settle.", false, null);

        var a = Assess(world, town, state, p, squad, members);
        var races = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in members)
            if (world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot)?.Race is { } race)
                races.Add(SettlerData.RaceName(race.StringId));
        // Their money is their own: on moving in it goes to their home's purse, never from the player.
        string text = $"{members.Count} {(members.Count == 1 ? "person" : "people")} ({string.Join(", ", races)}) {(members.Count == 1 ? "asks" : "ask")} to live here";
        if (a.RuleBroken is { } broken) text += $" ({broken})";
        text += squad.Money > 0 ? $". They bring {squad.Money:N0} cats of their own into their home's purse" : ". They bring no money";
        text += a.Armed ? " and carry weapons." : " and come unarmed.";
        return new GroupLine(g.Serial, text, !g.Accepted, a.HardRefusal);
    }

    /// <summary>
    /// When the next group comes, in words.
    /// </summary>
    static string NextGroupText(World world, TownState state, bool coming, int free, int residents, int pull)
    {
        if (!state.Open) return "Closed: no settlers come.";
        if (coming) return "A group is coming: no other comes until it has settled or gone.";
        if (free <= 0) return "No free bed: let residents live in a building (its own panel) and build beds in it.";
        if (residents >= state.Cap) return "The town is at its cap.";
        if (pull < NoArrivalsBelow) return $"The pull is below {NoArrivalsBelow}: nobody wants to come.";
        if (double.IsNaN(state.NextArrival)) return "Working out when the next group comes.";

        double hours = state.NextArrival - world.GameHours;
        if (hours <= 1) return "The next group is due now.";
        return hours < 48
            ? $"The next group is due in about {Math.Ceiling(hours).ToString(CultureInfo.InvariantCulture)} hours."
            : $"The next group is due in about {Math.Round(hours / 24, 1).ToString(CultureInfo.InvariantCulture)} days.";
    }

    /// <summary>
    /// Every storage box's mark as it applies: town stock inside a building whose boxes are all town stock, whatever its own mark, else its own.
    /// Boxes in such a building are added to <paramref name="inStorehouse"/>.
    /// </summary>
    Dictionary<string, StorageMark> EffectiveMarks(World world, Dictionary<string, int> siteTown, HashSet<string> inStorehouse)
    {
        var marks = new Dictionary<string, StorageMark>(StringComparer.Ordinal);
        foreach (string id in siteTown.Keys)
        {
            if (world.Buildings.Find(id) is not { } site || !StorageRecords.Contains(site.Record)) continue;

            bool stored = site.Parent is { } parent && (_rules.GetValueOrDefault(parent) & BuildingRule.TownStock) != 0;
            if (stored) inStorehouse.Add(id);
            marks[id] = stored ? StorageMark.TownStock : _marks.GetValueOrDefault(id);
        }
        return marks;
    }

    /// <summary>
    /// Each town's numbers for the window.
    /// </summary>
    Dictionary<int, TownCounts> Count(Dictionary<string, int> siteTown, Dictionary<string, StorageMark> marks)
    {
        var counts = new Dictionary<int, TownCounts>();
        foreach (var group in siteTown.GroupBy(p => p.Value))
        {
            var buildings = group.Where(p => !marks.ContainsKey(p.Key)).Select(p => p.Key).ToList();
            int homes = buildings.Count(id => (_rules.GetValueOrDefault(id) & BuildingRule.Residents) != 0);
            int stores = buildings.Count(id => (_rules.GetValueOrDefault(id) & BuildingRule.TownStock) != 0);
            var boxes = group.Where(p => marks.ContainsKey(p.Key)).Select(p => marks[p.Key]).ToList();
            counts[group.Key] = new TownCounts(buildings.Count, homes, stores, boxes.Count, boxes.Count(m => m == StorageMark.TownStock));
        }
        return counts;
    }
}
