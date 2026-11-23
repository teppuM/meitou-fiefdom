using Meitou.Simulation.Items;

namespace Fiefdom;

/// <summary>
/// What a town keeps back in its town stock, so a merchant carries only the rest to a counter: meals for its residents, first aid kits for its
/// guards, and what its own machines use. Counted by item, so goods and machines other mods add count by themselves. Built fresh each time a
/// merchant plans or takes (once a game second per town at most), and lowered as goods leave.
/// </summary>
sealed class TownReserve
{
    /// <summary>
    /// Food units in town stock above the meals kept back.
    /// </summary>
    int _food;

    /// <summary>
    /// First aid kits in town stock above those kept back.
    /// </summary>
    int _kits;

    /// <summary>
    /// For each item the town's machines use, the units in town stock above what they keep back (negative when short).
    /// </summary>
    readonly Dictionary<string, int> _machines = new(StringComparer.Ordinal);

    /// <summary>
    /// The reserve of a town whose town stock is <paramref name="stock"/>: <paramref name="foodKept"/> food units and <paramref name="kitsKept"/>
    /// first aid kits kept back, and for each item <paramref name="machineNeeds"/> names, that many units.
    /// </summary>
    public TownReserve(IEnumerable<Inventory> stock, int foodKept, int kitsKept, IReadOnlyDictionary<string, int> machineNeeds)
    {
        _food = -foodKept;
        _kits = -kitsKept;
        foreach (var (record, units) in machineNeeds) _machines[record] = -units;

        foreach (var box in stock)
        {
            foreach (var item in box.Items)
            {
                if (item.IsFood) _food += item.Quantity;
                if (IsFirstAidKit(item)) _kits += item.Quantity;
                if (_machines.ContainsKey(item.Record)) _machines[item.Record] += item.Quantity;
            }
        }
    }

    /// <summary>
    /// Food units above the meals kept back, for the log.
    /// </summary>
    public int SpareFood => _food;

    /// <summary>
    /// How many units of <paramref name="item"/> may leave town stock: all of it, less whatever its reserves hold back (0 when none may).
    /// </summary>
    public int Spare(ItemInstance item)
    {
        int units = item.Quantity;
        if (item.IsFood) units = Math.Min(units, _food);
        if (IsFirstAidKit(item)) units = Math.Min(units, _kits);
        if (_machines.TryGetValue(item.Record, out int machines)) units = Math.Min(units, machines);

        return Math.Max(0, units);
    }

    /// <summary>
    /// Records that <paramref name="units"/> of <paramref name="item"/> left town stock.
    /// </summary>
    public void Took(ItemInstance item, int units)
    {
        if (item.IsFood) _food -= units;
        if (IsFirstAidKit(item)) _kits -= units;
        if (_machines.ContainsKey(item.Record)) _machines[item.Record] -= units;
    }

    /// <summary>
    /// Whether an item is a first aid kit (the game's item function 1).
    /// </summary>
    public static bool IsFirstAidKit(ItemInstance item) => item.ItemFunction == ItemInfo.FirstAidKitFunction;
}
