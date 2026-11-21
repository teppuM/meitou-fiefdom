using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

// Fiefdom's data reader: prints game records from the merged data, for choosing packages, templates and regions.
//   fdata [--mods] find <TYPE> [text]      records of a type whose name or id holds the text
//   fdata [--mods] show <id or exact name> every field and reference list of the record(s)
//   fdata [--mods] types                   the record type names
//   fdata [--mods] refs <id> [TYPE]        the records (of a type) whose reference lists name the record, with the list
//   fdata [--mods] where <TYPE> <field> <n> the records of a type whose whole-number field equals n
//   fdata [--mods] regions [text]            each region's wandering groups with their faction's start relation to the player, safest first
//   fdata [--mods] region-at <x> <z> [...]    the region (weather area) at each world point
//   fdata [--mods] region-cells [text]         every map cell (one zone) of the matching regions, with its centre
// --mods loads Kenshi's mods.cfg as well as the base game; without it, the base game only.
var argv = args.ToList();
bool mods = argv.Remove("--mods");
var install = GameInstall.Locate() ?? throw new InvalidOperationException("Kenshi install not found");
var db = GameDatabase.Load(mods ? LoadOrder.FromInstall(install) : LoadOrder.BaseGame(install));
string cmd = argv.Count > 0 ? argv[0] : "types";

if (cmd == "types")
{
    foreach (var t in Enum.GetValues<FcsRecordType>()) Console.WriteLine($"{(int)t,4} {t}");
}
else if (cmd == "find")
{
    var type = Enum.Parse<FcsRecordType>(argv[1], ignoreCase: true);
    string text = argv.Count > 2 ? argv[2] : "";
    foreach (var r in db.OfType(type).Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                 || r.StringId.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name, StringComparer.Ordinal))
        Console.WriteLine($"{r.StringId,-40} {r.Name}");
}
else if (cmd == "show")
{
    string key = argv[1];
    var found = db.Find(key) is { } one ? new List<GameRecord> { one }
        : db.Records.Values.Where(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
    foreach (var r in found) Show(r);
}
else if (cmd == "refs")
{
    string target = argv[1];
    FcsRecordType? only = argv.Count > 2 ? Enum.Parse<FcsRecordType>(argv[2], ignoreCase: true) : null;
    foreach (var r in db.Records.Values.Where(r => only is null || r.Type == only).OrderBy(r => r.Name, StringComparer.Ordinal))
        foreach (string list in r.ReferenceLists)
            foreach (var re in r.GetReferences(list))
                if (re.TargetStringId == target) Console.WriteLine($"{r.StringId,-40} {r.Name}  [{list}] {re.Values}");
}
else if (cmd == "where")
{
    var type = Enum.Parse<FcsRecordType>(argv[1], ignoreCase: true);
    string field = argv[2];
    int value = int.Parse(argv[3]);
    foreach (var r in db.OfType(type).Where(r => r.Ints.TryGetValue(field, out int v) && v == value).OrderBy(r => r.Name, StringComparer.Ordinal))
        Console.WriteLine($"{r.StringId,-40} {r.Name}");
}

else if (cmd == "regions")
{
    Regions(argv.Count > 1 ? argv[1] : "");
}
else if (cmd == "region-at")
{
    var (areas, weather) = RegionMap();
    for (int i = 1; i + 1 < argv.Count; i += 2)
    {
        float x = float.Parse(argv[i]), z = float.Parse(argv[i + 1]);
        Console.WriteLine($"{x:0} {z:0}: {areas.RegionAt(weather, x, z).Name}");
    }
}
else if (cmd == "region-cells")
{
    // Every map cell (one zone, 4608 units a side) of the regions whose name holds the text, with its centre in world units.
    var (areas, weather) = RegionMap();
    string text = argv.Count > 1 ? argv[1] : "";
    for (int cz = 0; cz < Meitou.Data.World.WeatherAreas.Cells; cz++)
        for (int cx = 0; cx < Meitou.Data.World.WeatherAreas.Cells; cx++)
        {
            var region = areas.RegionOf(weather, cx, cz);
            if (!region.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            float size = Meitou.Data.World.WeatherAreas.CellSize;
            Console.WriteLine($"{region.Name}: cell {cx},{cz} centre {(cx - 32 + 0.5f) * size:0} {(cz - 32 + 0.5f) * size:0}");
        }
}

(Meitou.Data.World.WeatherAreas Areas, Meitou.Data.World.WeatherData Weather) RegionMap() =>
    (Meitou.Data.World.WeatherAreas.Load(install), Meitou.Data.World.WeatherData.Load(db));

// Every region (BIOME_GROUP) whose name holds the text: its wandering groups (`homeless spawns`, the pool settlers and roaming squads are
// drawn from) with their share, faction and that faction's start relation to the player's (hostile at -30 or below, the game's line), and a
// summary line: how much of the pool is hostile, how much is friendly people, and how many nests the region has. Sorted safest first.
void Regions(string text)
{
    var factions = Meitou.Data.Gameplay.FactionData.LoadAll(db);
    var relations = Meitou.Data.Gameplay.FactionRelations.Build(factions);
    var index = new Dictionary<string, int>(StringComparer.Ordinal);
    for (int i = 0; i < factions.Count; i++) index[factions[i].Id] = i;
    int player = index.GetValueOrDefault("204-gamedata.base", -1);
    var rows = new List<(double Hostile, string Text)>();
    foreach (var region in db.OfType(FcsRecordType.BiomeGroup).Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
    {
        var lines = new List<string>();
        double total = 0, hostile = 0, people = 0;
        foreach (var re in region.GetReferences("homeless spawns"))
        {
            if (db.Find(re.TargetStringId) is not { } t) continue;
            int weight = re.Values.Value0;
            string? fid = t.GetReferences("faction").Select(g => g.TargetStringId).FirstOrDefault();
            string fname = fid is not null && db.Find(fid) is { } f ? f.Name : "no faction";
            float rel = fid is not null && index.TryGetValue(fid, out int fi) && player >= 0 ? Math.Min(relations.Get(fi, player), relations.Get(player, fi)) : 0;
            bool bad = rel <= Meitou.Data.Gameplay.FactionRelations.HostileThreshold;
            total += weight;
            if (bad) hostile += weight;
            else if (fid is not null) people += weight;
            lines.Add($"    {(bad ? "HOSTILE" : "       ")} {weight,3}  {t.Name} ({fname}, {rel:0})");
        }
        if (total == 0) continue;
        int nests = region.GetReferences("nests").Count();
        double share = hostile / total;
        rows.Add((share, $"{region.Name} [{region.StringId}]: {share:P0} of the pool hostile, {people / total:P0} not hostile with a faction, {nests} nests\n"
            + string.Join("\n", lines)));
    }
    foreach (var row in rows.OrderBy(r => r.Hostile)) Console.WriteLine(row.Text);
}

void Show(GameRecord r)
{
    Console.WriteLine($"== {r.StringId} \"{r.Name}\" {r.Type} (from {r.DefinedBy})");
    foreach (var (k, v) in r.Bools.OrderBy(p => p.Key)) Console.WriteLine($"  b {k} = {v}");
    foreach (var (k, v) in r.Ints.OrderBy(p => p.Key)) Console.WriteLine($"  i {k} = {v}");
    foreach (var (k, v) in r.Floats.OrderBy(p => p.Key)) Console.WriteLine($"  f {k} = {v}");
    foreach (var (k, v) in r.Strings.OrderBy(p => p.Key)) Console.WriteLine($"  s {k} = {v}");
    foreach (string list in r.ReferenceLists.OrderBy(l => l))
        foreach (var re in r.GetReferences(list))
        {
            string name = db.Find(re.TargetStringId) is { } t ? $"{t.Name} ({t.Type})" : "?";
            Console.WriteLine($"  > {list}: {re.TargetStringId} {name} {re.Values}");
        }
}
