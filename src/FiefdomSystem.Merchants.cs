using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Fiefdom;

/// <summary>
/// The kinds of goods a shop counter sells, read from each item's own function in the game data, so items other mods add fall under a kind by
/// themselves. The numbers are saved; never renumber one, only add new bits.
/// </summary>
[Flags]
public enum Goods : byte
{
    None = 0,
    Food = 1,
    Medicine = 2,
    Drugs = 4,
    Weapons = 8,
    Armour = 16,
    Bags = 32,
    Other = 64,
}

/// <summary>
/// Sets what kinds of goods a shop counter in a Fiefdom town sells.
/// </summary>
public sealed record SetSells(string Site, Goods Goods) : SettlerCommand;

public sealed partial class FiefdomSystem : IChooserOwner
{
    /// <summary>
    /// The task row of the merchant's job (claimed with <c>AddJobType</c>), which its walks also run as.
    /// </summary>
    public const int MerchantRow = 7311;

    /// <summary>
    /// What a counter sells when the player has not chosen.
    /// </summary>
    public const Goods DefaultSells = Goods.Food;

    /// <summary>
    /// How close, in world units, a merchant must stand to a box or counter to move goods.
    /// </summary>
    const float Reach = 60;

    /// <summary>
    /// The inventory section a merchant carries goods in (the main grid, so worn gear and bags are never taken for stock).
    /// </summary>
    const string Carried = "main";

    /// <summary>
    /// What each counter sells, by the counter's building id (saved; a counter not here sells <see cref="DefaultSells"/>).
    /// </summary>
    readonly SortedDictionary<string, Goods> _sells = new(StringComparer.Ordinal);

    /// <summary>
    /// For each counter that wants stock now, the town-stock box to fetch it from; rebuilt in the serial step, read by the job's pure checks.
    /// </summary>
    readonly Dictionary<string, string> _restockFrom = new(StringComparer.Ordinal);

    /// <summary>
    /// Where each merchant on our walk is headed, by lasting id (not saved: after a load the job starts again).
    /// </summary>
    readonly Dictionary<ulong, string> _trips = [];

    /// <summary>
    /// The town-stock goods each merchant carries for their counter, by lasting id, then item record and units (saved). Only these go on the
    /// counter, so a merchant never sells their own belongings.
    /// </summary>
    readonly SortedDictionary<ulong, SortedDictionary<string, int>> _carrying = [];

    /// <summary>
    /// The units of <paramref name="record"/> a merchant carries for their counter.
    /// </summary>
    int CarriedUnits(ulong id, string record) => _carrying.TryGetValue(id, out var goods) ? goods.GetValueOrDefault(record) : 0;

    /// <summary>
    /// Adds (or with a negative count takes away) units a merchant carries for their counter.
    /// </summary>
    void Carry(ulong id, string record, int units)
    {
        if (!_carrying.TryGetValue(id, out var goods)) _carrying[id] = goods = new(StringComparer.Ordinal);
        int left = goods.GetValueOrDefault(record) + units;
        if (left > 0) goods[record] = left;
        else goods.Remove(record);
        if (goods.Count == 0) _carrying.Remove(id);
    }

    /// <summary>
    /// The kind an item is, from its item function: 3 food and 15 raw meat, 1, 2 and 12 the medical kits, 8 drugs, 5 weapons, 6 armour, 4 bags;
    /// everything else (trade goods, materials, books) is Other.
    /// </summary>
    public static Goods KindOf(ItemInstance item) => item.ItemFunction switch
    {
        3 or 15 => Goods.Food,
        1 or 2 or 12 => Goods.Medicine,
        8 => Goods.Drugs,
        5 => Goods.Weapons,
        6 => Goods.Armour,
        4 => Goods.Bags,
        _ => Goods.Other,
    };

    /// <summary>
    /// What a counter sells.
    /// </summary>
    Goods SellsAt(string counter) => _sells.TryGetValue(counter, out var g) ? g : DefaultSells;

    /// <summary>
    /// Sets what a counter sells, when it is a counter of the player's in a Fiefdom town.
    /// </summary>
    void Apply(World world, SetSells command)
    {
        if (OwnedInTown(world, command.Site) is not { } site || world.Machines.Find(site.Id) is not { Data.Function: ShopCounterFunction }) return;
        if (command.Goods == DefaultSells) _sells.Remove(site.Id);
        else _sells[site.Id] = command.Goods;
        _dirty = true;
    }

    /// <summary>
    /// The storage of each town-stock box of the town: complete boxes of the player's marked town stock, or standing in a building whose boxes are
    /// all town stock.
    /// </summary>
    IEnumerable<(BuildSite Site, Inventory Stock)> TownStock(World world, int town)
    {
        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsStorage || m.Storage is not { } stock) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction || TownOf(world, site) != town) continue;
            bool marked = _marks.GetValueOrDefault(site.Id) == StorageMark.TownStock;
            bool inStore = site.Parent is { } parent && (_rules.GetValueOrDefault(parent) & BuildingRule.TownStock) != 0;
            if (marked || inStore) yield return (site, stock);
        }
    }

    /// <summary>
    /// How many food items the town keeps back in town stock for meals: the setting per resident.
    /// </summary>
    int FoodReserve(string key) => FiefdomSettings.Current.FoodKeptPerResident * ResidentsOf(key);

    /// <summary>
    /// Finds, for every merchant's counter, a town-stock box holding goods the counter sells and has room for (food only above the town's
    /// reserve). Run in the serial step; the job's checks read the result.
    /// </summary>
    void PlanRestocks(World world)
    {
        _restockFrom.Clear();
        foreach (var (town, state) in LiveTowns(world))
        {
            List<(BuildSite Site, Inventory Stock)>? boxes = null;
            int food = -1, kits = -1;
            foreach (var holder in _holders.Values)
            {
                if (holder.Town != state.Key || _restockFrom.ContainsKey(holder.Site)) continue;
                if (world.Machines.Find(holder.Site) is not { Data.Function: ShopCounterFunction, Storage: { } counter }) continue;

                boxes ??= [.. TownStock(world, town.Number)];
                if (food < 0) food = boxes.Sum(b => b.Stock.Items.Where(i => i.IsFood).Sum(i => i.Quantity)) - FoodReserve(state.Key);
                if (kits < 0) kits = boxes.Sum(b => b.Stock.Items.Where(IsFirstAidKit).Sum(i => i.Quantity)) - KitReserve(state.Key);
                var sells = SellsAt(holder.Site);
                foreach (var (site, stock) in boxes)
                {
                    if (stock.Items.Any(i => Wanted(i, sells, food, kits) && counter.HasRoomFor(i)))
                    {
                        _restockFrom[holder.Site] = site.Id;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether an item is one the counter sells, food only while the town has food above its reserve.
    /// </summary>
    static bool Wanted(ItemInstance item, Goods sells, int spareFood, int spareKits) =>
        (KindOf(item) & sells) != 0 && (!item.IsFood || spareFood > 0) && (!IsFirstAidKit(item) || spareKits > 0);

    /// <summary>
    /// Gives a merchant on shift their counter's job (a mod job: the jobs list names it), or takes it away off shift.
    /// </summary>
    static void GiveMerchantJob(CharacterCold cold, string counter)
    {
        if (cold.Jobs.Any(j => j.Type == (TaskType)MerchantRow && j.Site == counter)) return;
        cold.Jobs.Add(new PermaJob((TaskType)MerchantRow, counter));
        cold.Orders |= CharacterOrders.Jobs;
        DecisionSystem.Request(cold);
    }

    /// <summary>
    /// Registers the merchant's, the food runner's and the kit fetcher's jobs with the chooser.
    /// </summary>
    public void Register(ChooserRegistry chooser)
    {
        chooser.Jobs((TaskType)MerchantRow, new MerchantJob(this));
        chooser.Jobs((TaskType)RunnerRow, new RunnerJob(this));
        chooser.Jobs((TaskType)KitRow, new KitJob(this));
    }

    /// <summary>
    /// The goods each merchant carries for their counter: true when <paramref name="cold"/> carries any it sells.
    /// </summary>
    bool CarriesFor(CharacterCold cold, string counter) =>
        !cold.LastingId.IsNone && cold.Inventory is { } inv
        && inv.Items.Any(i => i.Section == Carried && CarriedUnits(cold.LastingId.Value, i.Record) > 0);

    /// <summary>
    /// The serial part of the merchant's job: a merchant on its walk who has reached the box takes the goods its counter sells (as many as it can
    /// carry and the counter has room for, food above the reserve), and one who has reached the counter puts them in. Each then ends the walk, and
    /// the decision step starts the next one.
    /// </summary>
    void StepMerchants(World world)
    {
        PlanRestocks(world);
        var table = world.Characters;
        foreach (var (id, holder) in _holders)
        {
            if (!_trips.TryGetValue(id, out string? target)) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (cold.Running is not { Row: (TaskType)MerchantRow } || cold.Inventory is not { } inv)
            {
                _trips.Remove(id);
                continue;
            }
            if (world.Buildings.Find(target) is not { } site) continue;
            var at = table.Next[slot].Position;
            if (Vector2.Distance(new(at.X, at.Z), new(site.Position.X, site.Position.Z)) > Reach) continue;

            var sells = SellsAt(holder.Site);
            if (target == holder.Site && world.Machines.Find(target)?.Storage is { } counter)
            {
                int moved = 0;
                foreach (var item in inv.Items.Where(i => i.Section == Carried).ToList())
                {
                    // Only what the merchant took from town stock, even of a kind the counter no longer sells: it is the town's.
                    int units = Math.Min(item.Quantity, CarriedUnits(id, item.Record));
                    if (units <= 0) continue;
                    var part = units == item.Quantity ? item : item.WithQuantity(units);
                    if (!counter.HasRoomFor(part)) continue;
                    if (part == item) inv.Remove(item);
                    else item.Quantity -= units;
                    counter.Add(part);
                    Carry(id, part.Record, -units);
                    moved += units;
                }
                if (moved > 0) world.Machines.Touch();
                Log($"{_residents.GetValueOrDefault(id)?.Name}: put {moved} on the counter {target}; counter now {Contents(counter)}; still carrying {Contents(inv, Carried)}");
            }
            else if (world.Machines.Find(target)?.Storage is { } box && world.Machines.Find(holder.Site)?.Storage is { } counterRoom)
            {
                int spare = TownStock(world, TownOf(world, site)).Sum(b => b.Stock.Items.Where(i => i.IsFood).Sum(i => i.Quantity)) - FoodReserve(holder.Town);
                int spareKits = TownStock(world, TownOf(world, site)).Sum(b => b.Stock.Items.Where(IsFirstAidKit).Sum(i => i.Quantity)) - KitReserve(holder.Town);
                int taken = 0;
                foreach (var item in box.Items.ToList())
                {
                    if (!Wanted(item, sells, spare, spareKits)) continue;

                    // Food and first aid kits leave only down to their reserves: a stack larger than what is spare is split.
                    int units = item.IsFood ? Math.Min(item.Quantity, spare) : IsFirstAidKit(item) ? Math.Min(item.Quantity, spareKits) : item.Quantity;
                    var part = units == item.Quantity ? item : item.WithQuantity(units);
                    if (!counterRoom.HasRoomFor(part) || !inv.HasRoomIn(Carried, part)) continue;
                    if (part == item) box.Remove(item);
                    else item.Quantity -= units;
                    inv.AddTo(Carried, part);
                    Carry(id, part.Record, units);
                    taken += units;
                    if (part.IsFood) spare -= units;
                    if (IsFirstAidKit(part)) spareKits -= units;
                }
                if (taken > 0) world.Machines.Touch();
                Log($"{_residents.GetValueOrDefault(id)?.Name}: took {taken} from town stock {target} for {holder.Site}; box now {Contents(box)}; carrying {Contents(inv, Carried)}; food above reserve {spare}");
            }

            _trips.Remove(id);
            TaskStart.End(world, slot, TaskEnd.Done);
            DecisionSystem.Request(world, slot);
        }
    }

    /// <summary>
    /// What an inventory holds, for the log: each item's name and units, by name ("nothing" when empty); only <paramref name="section"/> when
    /// given.
    /// </summary>
    static string Contents(Inventory inv, string? section = null)
    {
        var lines = inv.Items.Where(i => section is null || i.Section == section)
            .GroupBy(i => i.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} x{g.Sum(i => i.Quantity)}").ToList();
        return lines.Count == 0 ? "nothing" : string.Join(", ", lines);
    }

    /// <summary>
    /// Starts a merchant's next walk: to the counter with the goods it carries, else to the box its counter's restock comes from.
    /// </summary>
    void StartMerchant(World world, int slot, int k)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { LastingId.IsNone: false } cold || k >= cold.Jobs.Count || cold.Jobs[k].Site is not { } counter) return;

        string? target = CarriesFor(cold, counter) ? counter : _restockFrom.GetValueOrDefault(counter);
        if (target is null || world.Buildings.Find(target) is not { } site) return;

        var result = TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, (TaskType)MerchantRow, TaskSource.Job)
        {
            Job = k, Point = site.Position, Mode = (byte)Meitou.Simulation.Movement.SpeedMode.Walk,
        });
        _trips[cold.LastingId.Value] = target;
        Log($"{cold.Name}: merchant walk to {site.Name} ({target}): {result}");
    }

    /// <summary>
    /// The merchant's job for the decision step: it can run while the merchant carries goods for its counter or the counter wants stock that
    /// town stock holds; otherwise the merchant keeps the town's own day.
    /// </summary>
    sealed class MerchantJob(FiefdomSystem owner) : IJobSource
    {
        /// <summary>
        /// Whether the merchant's job <paramref name="k"/> can run now (a pure read).
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var cold = world.Characters.Cold(slot);
            if (cold is null || k >= cold.Jobs.Count || cold.Jobs[k].Site is not { } counter) return DecisionCheck.No("no counter");
            if (cold.Running is { Row: (TaskType)MerchantRow, Source: TaskSource.Job } run && run.Job == k
                && world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo) return DecisionCheck.Runs;
            if (owner.CarriesFor(cold, counter) || owner._restockFrom.ContainsKey(counter)) return DecisionCheck.Run;
            return DecisionCheck.No("counter stocked, or nothing in town stock for it");
        }

        /// <summary>
        /// Starts the merchant's next walk.
        /// </summary>
        public void Start(World world, int slot, int k) => owner.StartMerchant(world, slot, k);
    }
}
