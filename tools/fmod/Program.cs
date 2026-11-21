using Meitou.Data.Fcs;

// Fiefdom's data writer: writes out\Fiefdom\Fiefdom.mod, the mod's own game records (no code). Run after changing a record here:
//   fmod [path]   (default: ..\..\out\Fiefdom\Fiefdom.mod beside this tool's folder)
// Records keep their string ids for ever: add new ones with the next number, never renumber.
string path = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../out/Fiefdom/Fiefdom.mod"));
const string File = "Fiefdom.mod";
// The header's description is the Steam Workshop page's (the construction set uploads it from here): workshop\workshop-description.txt.
string descriptionFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../workshop/workshop-description.txt"));
string description = System.IO.File.Exists(descriptionFile) ? System.IO.File.ReadAllText(descriptionFile).Replace("\r\n", "\n").TrimEnd()
    : "Fiefdom's own game records: the townspeople's daily round.";
var mod = new FcsFile { FileType = FcsFileType.V17, Version = 1, Author = "thenamejame", Description = description };

// 1: the town wander goal (task 37, Wandering town): walks between the town's public buildings.
var wander = Record(1, FcsRecordType.AiTask, "Fiefdom: wander the town");
wander.Ints["classification"] = 2;
wander.Ints["ending"] = 0;
wander.Ints["enum"] = 37;
wander.Ints["targeting"] = 0;

// 2: the residents' package: the game's Civilian day (shopping 8-17, home, bed 21-8, shooing strangers, doors) with wandering the town by day and
// sitting about in the town's public buildings (a bar is one with chairs) in the evening.
var residents = Record(2, FcsRecordType.AiPackage, "Fiefdom residents");
residents.Bools["clears existing jobs"] = false;
residents.Ints["signal func"] = 2;
residents.Ints["unloaded func"] = 4;
residents.References["inherits from"] = [new FcsReference("3107-gamedata.base", 0, 0, 0)];
List<FcsReference> day =
[
    new(wander.StringId, 9, 18, 30),
    new("96454-rebirth.mod", 18, 22, 60),
];
residents.References["Leader AI Goals"] = [.. day];
residents.References["Squad AI Goals"] = [.. day];

// 3: the visitors' package: shopping at the town's counters, sitting about in its public buildings and wandering the town, all day, looking
// after their own group.
var visitors = Record(3, FcsRecordType.AiPackage, "Fiefdom visitors");
visitors.Bools["clears existing jobs"] = false;
visitors.Ints["signal func"] = 0;
visitors.Ints["unloaded func"] = 4;
List<FcsReference> visit =
[
    new("2825-gamedata.base", 0, 24, 0),
    new("969-gamedata.base", 0, 24, 50),
    new("96454-rebirth.mod", 0, 24, 60),
    new(wander.StringId, 0, 24, 30),
];
visitors.References["Leader AI Goals"] = [.. visit];
visitors.References["Squad AI Goals"] = [.. visit];

// 4: the patrol guards' package: the game's "Guard town no shifts 24hr" (protecting allies, first aid, fighting without leaving town, law
// enforcing, bed 20-8) with its day goal "Static guard - building", which keeps the guards standing in their own building, swapped for the
// game's "Patrol hometown" (strolling about the squad's town). Every goal is the game's own, so a mod that changes one changes the guards too.
var patrol = Record(4, FcsRecordType.AiPackage, "Fiefdom patrol guards");
patrol.Bools["clears existing jobs"] = false;
patrol.Ints["signal func"] = 0;
patrol.Ints["unloaded func"] = 5;
// Leader and members get the same list, so every guard patrols on their own (Patrolling town draws each walker's own point) and goes to bed;
// a members' list without the patrol left everyone but the leader standing idle.
patrol.References["Leader AI Goals"] =
[
    new("957-gamedata.base", 0, 100, 0),
    new("965-gamedata.base", 8, 20, 0),
    new("1360-gamedata.base", 0, 100, 0),
    new("2826-gamedata.base", 0, 100, 0),
    new("3105-gamedata.base", 20, 8, 0),
    new("959-gamedata.base", 0, 0, 0),
    new("12607-gamedata.base", 0, 24, 0),
];
patrol.References["Squad AI Goals"] = [.. patrol.References["Leader AI Goals"]];
patrol.References["Slave AI Goals"] = [new("3397-gamedata.base", 0, 24, 0)];

mod.Records.Add(wander);
mod.Records.Add(residents);
mod.Records.Add(visitors);
mod.Records.Add(patrol);
mod.NextId = 5;
FcsWriter.WriteFile(mod, path);
Console.WriteLine($"wrote {mod.Records.Count} records to {path}");

static FcsRecord Record(int n, FcsRecordType type, string name) => new() { RecordType = type, Name = name, StringId = $"{n}-{File}" };
