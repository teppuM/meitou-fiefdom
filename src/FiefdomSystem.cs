using Meitou.Data;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Fiefdom;

/// <summary>
/// Makes one of the player's outposts a Fiefdom town, or stops it being one. <paramref name="Town"/> is the outpost's
/// <see cref="PlayerTown.Number"/>.
/// </summary>
public sealed record SetTown(int Town, bool On) : SimCommand;

/// <summary>
/// For testing: sets the player's faction's relation with the factions of this name, both ways (-100 to 100).
/// </summary>
public sealed record SetFactionRelation(string Faction, float Value) : SimCommand;

/// <summary>
/// Sets or clears one rule of the player's building <paramref name="Site"/> (its <see cref="BuildSite.Id"/>).
/// </summary>
public sealed record SetRule(string Site, BuildingRule Rule, bool On) : SimCommand;

/// <summary>
/// Marks the player's storage box <paramref name="Site"/> (its <see cref="BuildSite.Id"/>) as town stock or the player's own.
/// </summary>
public sealed record SetMark(string Site, StorageMark Mark) : SimCommand;

/// <summary>
/// Fiefdom's state in the world: which outposts are towns (a tag on the outpost, saved by the game with it, whose value is the town's key), each
/// building's rules and each storage box's mark (keyed by the building's id), each town's settler settings, steward and news, the groups on
/// their way, and the residents (keyed by lasting id), all in the mod's own save data. It takes the interface's commands in its serial input
/// step, checks each against the world, runs the settlers in its slow step, and publishes <see cref="View"/> for the interface.
/// </summary>
public sealed partial class FiefdomSystem : ITickSystem, IModSaveData, IWorldReady, IStateHashed, ICharacterDeathObserver
{
    #region State

    /// <summary>
    /// The mod's id, the key of its tags on towns and squads.
    /// </summary>
    public const string ModId = "fiefdom";

    /// <summary>
    /// A Fiefdom town's tag value starts with this, then its key.
    /// </summary>
    const string OnPrefix = "town:";

    /// <summary>
    /// A former Fiefdom town's tag value starts with this, then its key, so its settings come back when it is made a town again.
    /// </summary>
    const string OffPrefix = "off:";

    /// <summary>
    /// The tag value of a town made by the first build of the mod, which had no keys; given a key at the first slow step.
    /// </summary>
    const string OldTag = "town";

    /// <summary>
    /// The save data's format number (1: purposes and marks; 2: also towns, groups, residents and the serial counter; 3: also the day residents
    /// were last paid, who is starving since when, and each town's hungry leavings; 4: rules in place of purposes, what each counter sells and the residents' posts; 5: also the goods each merchant carries for their counter; 6: also each line's hours and each worker's pay record; 7: also the food runners and each town's policies; 8: also when visitors are next due and which groups visit; 9: also whether visitors have shopped).
    /// </summary>
    const int SaveVersion = 9;

    /// <summary>
    /// How often, in ticks, the view is rebuilt when nothing the system knows of has changed.
    /// </summary>
    const int RefreshEvery = 100;

    /// <summary>
    /// The latest view, for the interface's thread.
    /// </summary>
    public static volatile FiefdomView View = FiefdomView.Empty;

    /// <summary>
    /// The mod's context, for its log. Set when the mod starts.
    /// </summary>
    public static ModContext? Context { get; set; }

    /// <summary>
    /// The BUILDING records that can take rules (a building with an inside: houses and gates, not furniture or plain walls). Set once from the game
    /// data when the mod starts.
    /// </summary>
    public static IReadOnlySet<string> RuleRecords { get; set; } = new HashSet<string>();

    /// <summary>
    /// The BUILDING records that are storage (the game's own storage functions, with an inventory).
    /// </summary>
    public static IReadOnlySet<string> StorageRecords { get; set; } = new HashSet<string>();

    /// <summary>
    /// Each building's rules, by building id (only buildings with some).
    /// </summary>
    readonly SortedDictionary<string, BuildingRule> _rules = new(StringComparer.Ordinal);

    /// <summary>
    /// The storage boxes marked town stock, by building id (a box not here is the player's).
    /// </summary>
    readonly SortedDictionary<string, StorageMark> _marks = new(StringComparer.Ordinal);

    /// <summary>
    /// Each town's own state, by key.
    /// </summary>
    readonly SortedDictionary<string, TownState> _towns = new(StringComparer.Ordinal);

    /// <summary>
    /// The groups on their way or at a gate, by serial.
    /// </summary>
    readonly SortedDictionary<int, SettlerGroup> _groups = [];

    /// <summary>
    /// The residents, by lasting id.
    /// </summary>
    readonly SortedDictionary<ulong, Resident> _residents = [];

    /// <summary>
    /// The last number given to a town key or a group.
    /// </summary>
    int _serial;

    /// <summary>
    /// Whether the view must be rebuilt at the next slow step.
    /// </summary>
    bool _dirty = true;

    /// <summary>
    /// The building registry's revision the view was last built from.
    /// </summary>
    long _seenRevision = -1;

    /// <summary>
    /// The population data, the population system, the AI, the player system and the faction relations of this world.
    /// </summary>
    readonly PopulationData _data;
    readonly PopulationSystem? _population;
    readonly AiSystem? _ai;
    readonly PlayerSystem? _player;
    readonly AggroSystem? _aggro;
    readonly ConstructionSystem? _construction;
    readonly ProductionSystem? _production;

    /// <summary>
    /// A system for one world.
    /// </summary>
    FiefdomSystem(SystemBuildContext context)
    {
        _data = context.Data;
        _population = context.Find<PopulationSystem>();
        _ai = context.Find<AiSystem>();
        _player = context.Find<PlayerSystem>();
        _aggro = context.Find<AggroSystem>();
        _construction = context.Find<ConstructionSystem>();
        _production = context.Find<ProductionSystem>();
    }

    /// <summary>
    /// The factory the mod registers.
    /// </summary>
    public static ITickSystem Create(SystemBuildContext context) => new FiefdomSystem(context);

    /// <summary>
    /// Writes a line to the game's log.
    /// </summary>
    static void Log(string line) => Context?.Log(line);

    #endregion

    #region Commands

    /// <summary>
    /// Takes the tick's Fiefdom commands.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case SetTown t:
                    Apply(world, t);
                    break;
                case SetRule p:
                    Apply(world, p);
                    break;
                case SetMark m:
                    Apply(world, m);
                    break;
                case SetLiving l:
                    Apply(world, l);
                    break;
                case SettlerCommand s:
                    Apply(world, s);
                    break;
                case SetFactionRelation r:
                    Apply(world, r);
                    break;
            }
        }
    }

    /// <summary>
    /// For testing: sets the relation between the player's faction and every faction whose name is the command's (ignoring case), both ways,
    /// through the debug menu's own relation command. Logs what it did, or that no faction has the name.
    /// </summary>
    void Apply(World world, SetFactionRelation command)
    {
        if (_aggro is null) return;
        var relations = _aggro.Relations;
        int player = world.Player.Faction;
        int found = 0;
        for (int f = 0; f < relations.Count; f++)
        {
            if (f == player || !string.Equals(relations.Factions[f].Name, command.Faction, StringComparison.OrdinalIgnoreCase)) continue;
            world.Commands.Enqueue(new DebugSetRelation(player, f, command.Value) { Tick = world.Tick + 1 });
            found++;
        }
        Log(found > 0 ? $"relation with {command.Faction} set to {command.Value} (testing, {found} faction(s))" : $"no faction named {command.Faction} (testing)");
    }

    /// <summary>
    /// Tags or untags the outpost, when it still exists and the player's faction owns it. Rules, marks and settings stay, so a town switched
    /// off and on again keeps them.
    /// </summary>
    void Apply(World world, SetTown command)
    {
        if (world.Buildings.FindTown(command.Town) is not { } town || town.Owner != world.Player.Faction) return;

        string key = KeyOf(town) ?? NewKey();
        bool was = IsFiefdom(world, town);
        town.ModTags[ModId] = (command.On ? OnPrefix : OffPrefix) + key;
        if (command.On != was) TownOf(key).Tell(world.GameHours, command.On ? "became a Fiefdom town" : "stopped being a Fiefdom town");
        _dirty = true;
    }

    /// <summary>
    /// Sets or clears a building's rule, when it is the player's, stands in a Fiefdom town and can take rules.
    /// </summary>
    void Apply(World world, SetRule command)
    {
        if (OwnedInTown(world, command.Site) is not { } site || !RuleRecords.Contains(site.Record)) return;

        var rules = _rules.GetValueOrDefault(site.Id);
        rules = command.On ? rules | command.Rule : rules & ~command.Rule;
        if (rules == BuildingRule.None) _rules.Remove(site.Id);
        else _rules[site.Id] = rules;
        _dirty = true;
    }

    /// <summary>
    /// Marks a storage box, when it is the player's and stands in a Fiefdom town.
    /// </summary>
    void Apply(World world, SetMark command)
    {
        if (OwnedInTown(world, command.Site) is not { } site || !StorageRecords.Contains(site.Record)) return;

        if (command.Mark == StorageMark.KeepForMe) _marks.Remove(site.Id);
        else _marks[site.Id] = command.Mark;
        _dirty = true;
    }

    /// <summary>
    /// The player's building <paramref name="id"/> when it stands in one of the player's Fiefdom towns; null otherwise.
    /// </summary>
    static BuildSite? OwnedInTown(World world, string id)
    {
        if (world.Buildings.Find(id) is not { } site || site.Owner != world.Player.Faction) return null;

        return IsFiefdom(world, TownOf(world, site)) ? site : null;
    }

    /// <summary>
    /// The player town a building belongs to: its own, else the building it stands in; 0 for none.
    /// </summary>
    static int TownOf(World world, BuildSite site)
    {
        if (site.Town > 0) return site.Town;
        if (site.Parent is { } parent && world.Buildings.Find(parent) is { } outer) return outer.Town;
        return 0;
    }

    /// <summary>
    /// Whether outpost <paramref name="number"/> is one of the player's and a Fiefdom town.
    /// </summary>
    static bool IsFiefdom(World world, int number) => world.Buildings.FindTown(number) is { } town && IsFiefdom(world, town);

    /// <summary>
    /// Whether the outpost is the player's and a Fiefdom town.
    /// </summary>
    static bool IsFiefdom(World world, PlayerTown town) =>
        town.Owner == world.Player.Faction && town.ModTags.TryGetValue(ModId, out string? tag) && tag.StartsWith(OnPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The town's key from its tag (on or off), or null when it has none.
    /// </summary>
    static string? KeyOf(PlayerTown town)
    {
        if (!town.ModTags.TryGetValue(ModId, out string? tag)) return null;
        if (tag.StartsWith(OnPrefix, StringComparison.Ordinal)) return tag[OnPrefix.Length..];
        if (tag.StartsWith(OffPrefix, StringComparison.Ordinal)) return tag[OffPrefix.Length..];
        return null;
    }

    /// <summary>
    /// A new town key.
    /// </summary>
    string NewKey() => $"t{++_serial}";

    /// <summary>
    /// The state of the town with <paramref name="key"/>, made when it has none.
    /// </summary>
    TownState TownOf(string key)
    {
        if (!_towns.TryGetValue(key, out var town)) _towns[key] = town = new TownState { Key = key };
        return town;
    }

    /// <summary>
    /// The player's Fiefdom towns now, with their states, in town number order.
    /// </summary>
    List<(PlayerTown Town, TownState State)> LiveTowns(World world)
    {
        var list = new List<(PlayerTown, TownState)>();
        foreach (var town in world.Buildings.Towns)
            if (IsFiefdom(world, town) && KeyOf(town) is { } key) list.Add((town, TownOf(key)));
        return list;
    }

    /// <summary>
    /// The Fiefdom town with <paramref name="key"/> now, or null when no outpost carries it as a town.
    /// </summary>
    static PlayerTown? FindTown(World world, string key)
    {
        foreach (var town in world.Buildings.Towns)
            if (IsFiefdom(world, town) && KeyOf(town) == key) return town;
        return null;
    }

    /// <summary>
    /// Gives a key to every town still carrying the first build's keyless tag.
    /// </summary>
    void KeyOldTowns(World world)
    {
        foreach (var town in world.Buildings.Towns)
        {
            if (town.ModTags.TryGetValue(ModId, out string? tag) && tag == OldTag)
            {
                town.ModTags[ModId] = OnPrefix + NewKey();
                _dirty = true;
            }
        }
    }

    #endregion

    #region The slow step

    /// <summary>
    /// Runs the settlers every second and rebuilds the view when something changed: a command, a building added, finished or removed, a load, or
    /// every <see cref="RefreshEvery"/> ticks.
    /// </summary>
    public void SlowWorld(World world)
    {
        if (world.Tick % SettlerEvery == 0)
        {
            KeyOldTowns(world);
            StepSettlers(world);
        }

        bool built = world.Buildings.Revision != _seenRevision;
        if (!_dirty && !built && world.Tick % RefreshEvery != 0) return;

        _seenRevision = world.Buildings.Revision;
        _dirty = false;
        Forget(world);
        View = Build(world);
    }

    /// <summary>
    /// Drops the rules and marks of buildings that are gone (dismantled, destroyed for good), so the save does not grow without end.
    /// </summary>
    void Forget(World world)
    {
        foreach (string id in _rules.Keys.Where(id => world.Buildings.Find(id) is null).ToList()) _rules.Remove(id);
        foreach (string id in _marks.Keys.Where(id => world.Buildings.Find(id) is null).ToList()) _marks.Remove(id);
    }

    #endregion

    #region Save, load and hash

    /// <summary>
    /// After a load or a new game: find the groups' platoons again and rebuild the view at the next slow step.
    /// </summary>
    public void Ready(World world, bool fromSave)
    {
        _dirty = true;
        _platoons.Clear();
        _walking.Clear();
        _seenStates.Clear();
        _lastDistance.Clear();
        _trips.Clear();
        _rounds.Clear();
        _reattach = true;
    }

    /// <summary>
    /// Writes the rules, marks, towns, groups, residents and posts.
    /// </summary>
    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(SaveVersion);
        w.Write(_rules.Count);
        foreach (var (id, rules) in _rules)
        {
            w.Write(id);
            w.Write((byte)rules);
        }
        w.Write(_marks.Count);
        foreach (var (id, mark) in _marks)
        {
            w.Write(id);
            w.Write((byte)mark);
        }
        w.Write(_serial);
        w.Write(_towns.Count);
        foreach (var town in _towns.Values) town.Write(w);
        w.Write(_groups.Count);
        foreach (var group in _groups.Values) group.Write(w);
        w.Write(_residents.Count);
        foreach (var (id, r) in _residents)
        {
            w.Write(id);
            w.Write(r.Town);
            w.Write(r.Home);
            w.Write(r.Since);
            w.Write(r.Name);
        }
        w.Write(_paidDay);
        w.Write(_starvingSince.Count);
        foreach (var (id, since) in _starvingSince)
        {
            w.Write(id);
            w.Write(since);
        }
        w.Write(_sells.Count);
        foreach (var (id, goods) in _sells)
        {
            w.Write(id);
            w.Write((byte)goods);
        }
        w.Write(_carrying.Count);
        foreach (var (id, goods) in _carrying)
        {
            w.Write(id);
            w.Write(goods.Count);
            foreach (var (record, units) in goods)
            {
                w.Write(record);
                w.Write(units);
            }
        }
        w.Write(_holders.Count);
        foreach (var (id, h) in _holders)
        {
            w.Write(id);
            w.Write(h.Town);
            w.Write(h.Site);
            w.Write((byte)h.Shift);
        }
        w.Write(_lineHours.Count);
        foreach (var (line, hours) in _lineHours)
        {
            w.Write(line);
            w.Write((byte)hours);
        }
        w.Write(_pay.Count);
        foreach (var (id, pay) in _pay)
        {
            w.Write(id);
            w.Write(pay.Worked);
            w.Write(pay.Unpaid);
        }
        w.Write(_runners.Count);
        foreach (var (id, key) in _runners)
        {
            w.Write(id);
            w.Write(key);
        }
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote (format 1 has only purposes and marks). A newer format than this build knows is left unread, keeping
    /// nothing.
    /// </summary>
    public void Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        _rules.Clear();
        _marks.Clear();
        _towns.Clear();
        _groups.Clear();
        _residents.Clear();
        _starvingSince.Clear();
        _holders.Clear();
        _sells.Clear();
        _carrying.Clear();
        _lineHours.Clear();
        _pay.Clear();
        _runners.Clear();
        _rounds.Clear();
        _paidDay = -1;
        _serial = 0;
        int version = r.ReadInt32();
        if (version > SaveVersion) return;

        for (int n = r.ReadInt32(); n > 0; n--)
        {
            string id = r.ReadString();
            byte value = r.ReadByte();
            var rules = version >= 4 ? (BuildingRule)value
                : value == (byte)OldPurpose.Home ? BuildingRule.Residents
                : value == (byte)OldPurpose.Storehouse ? BuildingRule.TownStock : BuildingRule.None;
            if (rules != BuildingRule.None) _rules[id] = rules;
        }
        for (int n = r.ReadInt32(); n > 0; n--) _marks[r.ReadString()] = (StorageMark)r.ReadByte();
        if (version >= 2)
        {
            _serial = r.ReadInt32();
            for (int n = r.ReadInt32(); n > 0; n--)
            {
                var town = TownState.Read(r, version);
                _towns[town.Key] = town;
            }
            for (int n = r.ReadInt32(); n > 0; n--)
            {
                var group = SettlerGroup.Read(r, version);
                _groups[group.Serial] = group;
            }
            for (int n = r.ReadInt32(); n > 0; n--)
                _residents[r.ReadUInt64()] = new Resident(r.ReadString(), r.ReadString(), r.ReadDouble(), r.ReadString());
        }
        if (version >= 3)
        {
            _paidDay = r.ReadInt32();
            for (int n = r.ReadInt32(); n > 0; n--) _starvingSince[r.ReadUInt64()] = r.ReadDouble();
        }
        if (version >= 4)
        {
            for (int n = r.ReadInt32(); n > 0; n--) _sells[r.ReadString()] = (Goods)r.ReadByte();
            for (int n = version >= 5 ? r.ReadInt32() : 0; n > 0; n--)
            {
                var goods = new SortedDictionary<string, int>(StringComparer.Ordinal);
                ulong id = r.ReadUInt64();
                for (int m = r.ReadInt32(); m > 0; m--) goods[r.ReadString()] = r.ReadInt32();
                _carrying[id] = goods;
            }
            for (int n = r.ReadInt32(); n > 0; n--)
                _holders[r.ReadUInt64()] = new PostHolder(r.ReadString(), r.ReadString(), (Shift)r.ReadByte());
        }
        if (version >= 6)
        {
            for (int n = r.ReadInt32(); n > 0; n--) _lineHours[r.ReadString()] = (Hours)r.ReadByte();
            for (int n = r.ReadInt32(); n > 0; n--) _pay[r.ReadUInt64()] = new PayState { Worked = r.ReadByte(), Unpaid = r.ReadByte() };
        }
        if (version >= 7)
            for (int n = r.ReadInt32(); n > 0; n--) _runners[r.ReadUInt64()] = r.ReadString();
        _dirty = true;
    }

    /// <summary>
    /// Adds the mod's state to the world's state hash (the town tags are hashed with the towns).
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        foreach (var (id, rules) in _rules)
        {
            hasher.Add(Rng.StableHash(id));
            hasher.Add((int)rules);
        }
        foreach (var (id, mark) in _marks)
        {
            hasher.Add(Rng.StableHash(id));
            hasher.Add((int)mark + 100);
        }
        hasher.Add(_serial);
        foreach (var town in _towns.Values) town.Hash(ref hasher);
        foreach (var group in _groups.Values)
        {
            hasher.Add(group.Serial);
            hasher.Add((int)group.Stage);
            hasher.Add(group.Accepted);
            hasher.Add(group.Walks);
            hasher.Add(group.Visiting);
            hasher.Add(group.Shopped);
        }
        foreach (var (id, r) in _residents)
        {
            hasher.Add(id);
            hasher.Add(Rng.StableHash(r.Home));
        }
        hasher.Add(_paidDay);
        foreach (var (id, since) in _starvingSince)
        {
            hasher.Add(id);
            hasher.Add(since);
        }
        foreach (var (id, goods) in _sells)
        {
            hasher.Add(Rng.StableHash(id));
            hasher.Add((int)goods + 200);
        }
        foreach (var (id, goods) in _carrying)
            foreach (var (record, units) in goods)
            {
                hasher.Add(id);
                hasher.Add(Rng.StableHash(record));
                hasher.Add(units);
            }
        foreach (var (id, h) in _holders)
        {
            hasher.Add(id);
            hasher.Add(Rng.StableHash(h.Site));
            hasher.Add((int)h.Shift);
        }
        foreach (var (line, hours) in _lineHours)
        {
            hasher.Add(Rng.StableHash(line));
            hasher.Add((int)hours + 300);
        }
        foreach (var (id, pay) in _pay)
        {
            hasher.Add(id);
            hasher.Add(pay.Worked + pay.Unpaid * 4);
        }
        foreach (var (id, key) in _runners)
        {
            hasher.Add(id);
            hasher.Add(Rng.StableHash(key) + 400);
        }
    }

    #endregion
}
