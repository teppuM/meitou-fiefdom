using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Fiefdom;

/// <summary>
/// Which of the player's storage boxes Fiefdom's townspeople may use when the game picks one for them, built in the serial slow step and read from
/// the AI's worker threads: each of the mod's squads (homes and guardhouses) with its town, and each of the player's complete storage boxes with
/// the town whose town stock it is (0 for a box kept for the player). Never changed once built; a new one replaces it, so reading it from any
/// thread is safe.
/// </summary>
sealed class TownStores
{
    /// <summary>
    /// A mod squad's town when its home or guardhouse is gone: no box of the player's matches it.
    /// </summary>
    public const int NoTown = -1;

    /// <summary>
    /// None: no Fiefdom squads, so every storage is allowed.
    /// </summary>
    public static readonly TownStores Empty = new([], new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>
    /// Each of the mod's squads, by squad id, with its town's number (<see cref="NoTown"/> when its building is gone).
    /// </summary>
    readonly Dictionary<int, int> _squadTown;

    /// <summary>
    /// Each of the player's complete storage boxes, by building id, with the number of the town whose town stock it is (0 for a kept box).
    /// </summary>
    readonly Dictionary<string, int> _boxTown;

    /// <summary>
    /// A snapshot of the squads and boxes; the dictionaries are never changed afterwards.
    /// </summary>
    public TownStores(Dictionary<int, int> squadTown, Dictionary<string, int> boxTown)
    {
        _squadTown = squadTown;
        _boxTown = boxTown;
    }

    /// <summary>
    /// Whether <paramref name="who"/> may use <paramref name="storage"/>: anyone not of the mod's squads may use anything (the game's own rules);
    /// a townsperson or guard may use any storage that is not one of the player's boxes (a machine, someone else's container) and, of the player's
    /// boxes, only the town stock of their own town. Reads only the last finished tick's state (<see cref="CharacterTable.IsAlive"/> and the
    /// character's cold data, which the parallel steps do not change). Thread-safe, and allocates nothing.
    /// </summary>
    public bool Allows(World world, CharacterId who, string storage)
    {
        if (_squadTown.Count == 0) return true;
        if (!_boxTown.TryGetValue(storage, out int boxTown)) return true;

        var table = world.Characters;
        if (!table.IsAlive(who) || table.Cold(who.Slot) is not { } cold) return true;
        if (!_squadTown.TryGetValue(cold.SquadId, out int town)) return true;

        return boxTown == town;
    }
}

public sealed partial class FiefdomSystem : IStorageFilter
{
    #region Storage use

    /// <summary>
    /// The news line a town with townspeople and no town stock gets.
    /// </summary>
    const string NoStockLine = "No town stock: townspeople eat and work only from boxes marked Town stock, never from yours";

    /// <summary>
    /// The latest snapshot of which boxes the townspeople may use, for the storage searches on any thread.
    /// </summary>
    volatile TownStores _stores = TownStores.Empty;

    /// <summary>
    /// Asked by the game for each storage its own searches would pick for a character (a meal, a machine's input or output, a stash, building
    /// materials): Fiefdom's townspeople and guards use only their own town's town stock among the player's boxes, so a box marked Keep for me
    /// stays the player's. The player's own characters, other squads and what the player does by hand are never refused. Called on the AI's
    /// worker threads, up to many times a tick; with no Fiefdom squads it is one count check.
    /// </summary>
    public bool Allows(World world, CharacterId who, string storage, StorageUse use, string? record) => _stores.Allows(world, who, storage);

    /// <summary>
    /// Builds a new snapshot from the mod's squads and the player's boxes, and tells a Fiefdom town with townspeople and no town stock how to
    /// give them some. Runs in the serial slow step once a game second, and when a save has loaded or a new game started.
    /// </summary>
    void BuildStores(World world)
    {
        var squads = SquadTowns(world);
        if (squads.Count == 0)
        {
            _stores = TownStores.Empty;
            return;
        }

        var boxes = BoxTowns(world);
        _stores = new TownStores(squads, boxes);
        TellNoStock(world, squads, boxes);
    }

    /// <summary>
    /// Each living squad of the mod (a home's or a guardhouse's) with the number of the town its building stands in, whether or not that town is a
    /// Fiefdom town now (one switched off keeps its marks, and its people stay out of the player's kept boxes); <see cref="TownStores.NoTown"/>
    /// when the building is gone.
    /// </summary>
    static Dictionary<int, int> SquadTowns(World world)
    {
        var towns = new Dictionary<int, int>();
        foreach (var squad in world.Squads.All)
        {
            if (squad.Dead || squad.Faction != world.Player.Faction || !squad.ModTags.TryGetValue(ModId, out string? tag)) continue;
            if (SiteOfTag(tag) is not { } id) continue;

            towns[squad.Id] = world.Buildings.Find(id) is { } site && TownOf(world, site) is > 0 and var town ? town : TownStores.NoTown;
        }
        return towns;
    }

    /// <summary>
    /// The building id in a squad's tag (home, guardhouse or merchant shop), or null for another tag.
    /// </summary>
    static string? SiteOfTag(string tag)
    {
        if (tag.StartsWith(HomeTag, StringComparison.Ordinal)) return tag[HomeTag.Length..];
        if (tag.StartsWith(GuardTag, StringComparison.Ordinal)) return tag[GuardTag.Length..];
        if (tag.StartsWith(ShopTag, StringComparison.Ordinal)) return tag[ShopTag.Length..];
        return null;
    }

    /// <summary>
    /// Each of the player's complete storage boxes, with the number of the town whose town stock it is, or 0 when it is kept for the player or
    /// stands in no town.
    /// </summary>
    Dictionary<string, int> BoxTowns(World world)
    {
        var boxes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsStorage || m.Storage is null) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;

            boxes[site.Id] = IsTownStock(site) ? TownOf(world, site) : 0;
        }
        return boxes;
    }

    /// <summary>
    /// Whether a box is town stock: marked so, or standing in a building whose boxes are all town stock.
    /// </summary>
    bool IsTownStock(BuildSite box) =>
        _marks.GetValueOrDefault(box.Id) == StorageMark.TownStock
        || box.Parent is { } parent && (_rules.GetValueOrDefault(parent) & BuildingRule.TownStock) != 0;

    /// <summary>
    /// Tells each Fiefdom town that has townspeople but no town stock box that its people take nothing from the player's kept boxes, unless its
    /// news already says so.
    /// </summary>
    void TellNoStock(World world, Dictionary<int, int> squads, Dictionary<string, int> boxes)
    {
        foreach (var (town, state) in LiveTowns(world))
        {
            if (!squads.ContainsValue(town.Number) || boxes.ContainsValue(town.Number)) continue;
            if (state.News.Exists(n => n.EndsWith(NoStockLine, StringComparison.Ordinal))) continue;

            state.Tell(world.GameHours, NoStockLine);
            Log($"{town.Name}: no town stock box; townspeople keep out of the player's boxes");
        }
    }

    #endregion
}
