using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

// Fiefdom's data reader: prints game records from the merged data, for choosing packages, templates and regions.
//   fdata [--mods] find <TYPE> [text]      records of a type whose name or id holds the text
//   fdata [--mods] show <id or exact name> every field and reference list of the record(s)
//   fdata [--mods] types                   the record type names
//   fdata [--mods] refs <id> [TYPE]        the records (of a type) whose reference lists name the record, with the list
//   fdata [--mods] where <TYPE> <field> <n> the records of a type whose whole-number field equals n
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
