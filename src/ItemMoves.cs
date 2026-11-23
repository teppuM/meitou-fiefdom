using Meitou.Simulation.Items;

namespace Fiefdom;

/// <summary>
/// Moving goods between inventories. The game's add puts in as many units of a stack as fit and leaves the rest in the item; goods that do not
/// stack in a character's pack (flour, wheat, water, bread) fit far fewer than a storage box holds, so every move goes through here and puts
/// back what did not fit.
/// </summary>
static class ItemMoves
{
    /// <summary>
    /// Moves up to <paramref name="units"/> of <paramref name="item"/>, an item of <paramref name="from"/>, into <paramref name="to"/> (into its
    /// <paramref name="section"/> when given, else where its own add puts it). As many as fit move; the rest stays in <paramref name="from"/>.
    /// Returns how many moved.
    /// </summary>
    public static int Move(Inventory from, ItemInstance item, int units, Inventory to, string? section)
    {
        units = Math.Min(units, item.Quantity);
        if (units <= 0) return 0;

        var part = units == item.Quantity ? item : item.WithQuantity(units);
        if (part == item) from.Remove(item);
        else item.Quantity -= units;

        bool all = section is null ? to.Add(part) : to.AddTo(section, part);
        if (all) return units;

        // The add left the units that did not fit in the part: they go back where they came from.
        int left = part.Quantity;
        if (part != item) item.Quantity += left;
        else if (!from.AddTo(item.Section, item))
            Console.WriteLine($"mod       fiefdom: {left} {item.Name} did not fit back where they came from and were lost");

        return units - left;
    }
}
