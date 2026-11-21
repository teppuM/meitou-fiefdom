using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.World;

namespace Fiefdom;

/// <summary>
/// One wandering group a region's data lists (a BIOME_GROUP's <c>homeless spawns</c> entry) that could settle in a town: people, not animals,
/// not a trader caravan. Whether its faction is hostile to the player, whether its world states pass, and the town's own rules are checked when
/// a group is wanted, against the world as it is then.
/// </summary>
/// <param name="TemplateId">The SQUAD_TEMPLATE's string id.</param>
/// <param name="Name">The template's name.</param>
/// <param name="Weight">The region's share for it (the entry's first value).</param>
/// <param name="Races">Every race its people may be (RACE string ids); empty when its records name none.</param>
/// <param name="Unprotected">The kinds of harmful weather some of its people could meet with neither a race immune to it nor clothing in their
/// lists that keeps it off.</param>
/// <param name="MinSize">The fewest people it makes.</param>
/// <param name="MaxSize">The most people it makes (each entry's count rolled as the game rolls it, at the population's size multiplier 1).</param>
public sealed record SettlerTemplate(string TemplateId, string Name, int Weight, IReadOnlySet<string> Races, IReadOnlySet<WeatherAffecting> Unprotected,
    int MinSize, int MaxSize);

/// <summary>
/// A region (BIOME_GROUP) as Fiefdom sees it: its name, the harmful weather that can fall or burn there, and the wandering groups that could
/// settle from it.
/// </summary>
public sealed record SettlerRegion(string Id, string Name, IReadOnlySet<WeatherAffecting> Harms, IReadOnlyList<SettlerTemplate> Templates);

/// <summary>
/// What Fiefdom reads from the merged game data once, when the mod starts: each region's settler groups and harmful weather, and each race's
/// name and weather immunities. Everything comes from the data as the load order leaves it, so races, regions, weather and groups other mods add
/// or change count with no list kept here.
/// </summary>
public static class SettlerData
{
    /// <summary>
    /// The kinds of weather that hurt a person who is neither immune nor fully covered: acid rain, fire, gas. Dust storms and plain rain only
    /// blind (the weather system's rules).
    /// </summary>
    public static readonly WeatherAffecting[] Harmful = [WeatherAffecting.Acid, WeatherAffecting.Burning, WeatherAffecting.Gas];

    /// <summary>
    /// The regions by BIOME_GROUP id.
    /// </summary>
    public static IReadOnlyDictionary<string, SettlerRegion> Regions { get; private set; } = new Dictionary<string, SettlerRegion>();

    /// <summary>
    /// Every race by RACE id.
    /// </summary>
    public static IReadOnlyDictionary<string, RaceData> Races { get; private set; } = new Dictionary<string, RaceData>();

    /// <summary>
    /// Each race's group (the name of the first RACE_GROUP record listing it: the game's Human, Shek, Hive...), by RACE id; a race no group lists is
    /// not here.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RaceGroups { get; private set; } = new Dictionary<string, string>();

    /// <summary>
    /// A race's group name, or the race's own name when no group lists it.
    /// </summary>
    public static string GroupOf(string race) => RaceGroups.TryGetValue(race, out string? group) ? group : RaceName(race);

    /// <summary>
    /// A weather kind's name for the player.
    /// </summary>
    public static string NameOf(WeatherAffecting kind) => kind switch
    {
        WeatherAffecting.Acid => "acid rain",
        WeatherAffecting.Burning => "fire",
        WeatherAffecting.Gas => "gas",
        _ => kind.ToString(),
    };

    /// <summary>
    /// A race's name, or its id when the data has no such race.
    /// </summary>
    public static string RaceName(string id) => Races.TryGetValue(id, out var race) && race.Name.Length > 0 ? race.Name : id;

    /// <summary>
    /// Reads everything from the merged data, writing a line for each region with harmful weather to <paramref name="log"/>. Returns a line for
    /// the log.
    /// </summary>
    public static string Load(GameDatabase db, Action<string> log)
    {
        var races = new Dictionary<string, RaceData>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.Race)) races[r.StringId] = RaceData.From(r, db);
        Races = races;
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in db.OfType(FcsRecordType.RaceGroup).OrderBy(g => g.StringId, StringComparer.Ordinal))
            foreach (var r in g.GetReferences("races"))
                if (races.ContainsKey(r.TargetStringId) && g.Name.Length > 0) groups.TryAdd(r.TargetStringId, g.Name);
        RaceGroups = groups;

        var protects = new Dictionary<string, ArmourData>(StringComparer.Ordinal);
        ArmourData? Armour(string id)
        {
            if (protects.TryGetValue(id, out var known)) return known;
            if (db.Find(id) is not { Type: FcsRecordType.Armour } a) return null;
            return protects[id] = ArmourData.From(a);
        }

        var weather = WeatherData.Load(db);
        var harmsByRegion = new Dictionary<string, HashSet<WeatherAffecting>>(StringComparer.Ordinal);
        foreach (var region in weather.Regions)
        {
            var harms = new HashSet<WeatherAffecting>();
            foreach (var season in region.Seasons)
                foreach (var w in season.Season.Weathers)
                {
                    if (Harmful.Contains(w.Weather.AffectType)) harms.Add(w.Weather.AffectType);
                    foreach (var e in w.Weather.Effects)
                        if (Harmful.Contains(e.Effect.AffectType)) harms.Add(e.Effect.AffectType);
                }
            harmsByRegion[region.StringId] = harms;
        }

        var regions = new Dictionary<string, SettlerRegion>(StringComparer.Ordinal);
        int templates = 0;
        foreach (var group in BiomeGroupData.LoadAll(db))
        {
            var harms = harmsByRegion.GetValueOrDefault(group.Id) ?? [];
            var list = new List<SettlerTemplate>();
            foreach (var entry in group.HomelessSpawns)
            {
                if (entry.V0 <= 0 || db.Find(entry.Id) is not { Type: FcsRecordType.SquadTemplate } record) continue;
                if (Settler(db, record, entry.V0, harms, races, Armour) is { } settler) list.Add(settler);
            }
            templates += list.Count;
            regions[group.Id] = new SettlerRegion(group.Id, group.Name, harms, list);
        }
        Regions = regions;
        foreach (var region in regions.Values.Where(r => r.Harms.Count > 0 && r.Templates.Count > 0).OrderBy(r => r.Name, StringComparer.Ordinal))
            log($"{region.Name}: {string.Join(", ", region.Harms.Select(NameOf))}; {region.Templates.Count} settler groups, "
                + $"{region.Templates.Count(t => t.Unprotected.Count == 0)} of them safe from it");
        return $"{regions.Count} regions, {templates} settler groups, {races.Count} races";
    }

    /// <summary>
    /// The races a template's character may be, in the order the game chooses a race: the template's <c>race override</c>, else the character's
    /// <c>race</c> list, else its faction's <c>races</c> (the template's faction, else the character's own), else the game's default race. Entries of
    /// weight 0 are never drawn.
    /// </summary>
    static List<string> RacesOf(GameDatabase db, GameRecord template, SquadTemplate t, GameRecord character, Dictionary<string, RaceData> races)
    {
        static List<string> Drawn(IEnumerable<GameReference> refs, Dictionary<string, RaceData> races) =>
            [.. refs.Where(r => r.Values.Value0 > 0).Select(r => r.TargetStringId).Where(races.ContainsKey)];

        var list = Drawn(template.GetReferences("race override"), races);
        if (list.Count == 0) list = Drawn(character.GetReferences("race"), races);
        if (list.Count == 0)
        {
            string? factionId = t.Faction ?? (character.GetReferences("faction") is { Count: > 0 } own ? own[0].TargetStringId : null);
            if (factionId is not null && db.Find(factionId) is { } faction) list = Drawn(faction.GetReferences("races"), races);
        }
        if (list.Count == 0 && races.ContainsKey(Meitou.Data.Characters.CharacterGenerator.DefaultRaceId))
            list.Add(Meitou.Data.Characters.CharacterGenerator.DefaultRaceId);
        return list;
    }

    /// <summary>
    /// The settler entry for a template, or null when it is not people (it lists animals, or a member that is not a CHARACTER) or is a trader.
    /// </summary>
    static SettlerTemplate? Settler(GameDatabase db, GameRecord record, int weight, IReadOnlySet<WeatherAffecting> harms,
        Dictionary<string, RaceData> races, Func<string, ArmourData?> armour)
    {
        var t = SquadTemplate.From(record);
        if (t.IsTrader || t.Vendors.Count > 0 || t.Animals.Count > 0 || t.Animals2.Count > 0) return null;

        var members = new List<RecordLink>();
        if (t.Leader is { } leader) members.Add(leader);
        members.AddRange(t.Squad);
        members.AddRange(t.Squad2);
        members.AddRange(t.ChooseFrom);
        if (members.Count == 0) return null;

        var raceIds = new HashSet<string>(StringComparer.Ordinal);
        var unprotected = new HashSet<WeatherAffecting>();
        foreach (var link in members)
        {
            if (db.Find(link.Id) is not { Type: FcsRecordType.Character } c) return null;

            var own = RacesOf(db, record, t, c, races);
            raceIds.UnionWith(own);
            var clothing = c.GetReferences("clothing").Select(r => armour(r.TargetStringId)).OfType<ArmourData>().ToList();
            foreach (var kind in harms)
            {
                bool immune = own.Count > 0 && own.All(r => races[r].ImmuneTo(kind));
                bool covered = clothing.Any(a => a.Protects(kind));
                if (!immune && !covered) unprotected.Add(kind);
            }
        }

        static int Most(RecordLink l) => l.V1 != 0 && l.V1 != 100 && l.V1 > l.V0 ? l.V1 : Math.Max(0, l.V0);
        int lead = t.Leader is null ? 0 : 1;
        int min = lead + t.Squad.Sum(l => Math.Max(0, l.V0)) + t.Squad2.Sum(l => Math.Max(0, l.V0)) + Math.Max(0, t.NumRandomChars);
        int max = lead + t.Squad.Sum(Most) + t.Squad2.Sum(Most) + Math.Max(t.NumRandomChars, t.NumRandomCharsMax);
        return new SettlerTemplate(t.Id, t.Name, weight, raceIds, unprotected, Math.Max(1, min), Math.Max(1, max));
    }
}
