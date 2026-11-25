using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;

namespace Fiefdom;

/// <summary>
/// The kinds of goods a shop counter sells, read from each item's own function and type in the game data, so items other mods add fall under a
/// kind by themselves. The numbers are saved; never renumber one, only add new bits.
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
    Robotics = 128,
}

/// <summary>
/// Sets what kinds of goods a shop counter in a Fiefdom town sells. Changing a staffed shop's goods wipes its current stock so new goods trickle
/// in slowly from baseline.
/// </summary>
public sealed record SetSells(string Site, Goods Goods) : SettlerCommand;

/// <summary>
/// Sets whether the merchant at shop counter <paramref name="Site"/> buys excess goods from town stock at wholesale to resell to visitors
/// (default off: stocks only from outside and never touches town stock).
/// </summary>
public sealed record SetBuySurplus(string Site, bool On) : SettlerCommand;

/// <summary>
/// Per-counter merchant shop state (saved in format 12): whether the merchant buys town surplus, when the next outside stock trickle is due, and
/// the deterministic roll sequence number.
/// </summary>
sealed class ShopState
{
    public bool BuySurplus;
    public double NextTrickle = double.NaN;
    public int TrickleSeq;
}

public sealed partial class FiefdomSystem : IChooserOwner
{
    /// <summary>
    /// The task row of the merchant's job (claimed with <c>AddJobType</c>), which its walks also run as.
    /// </summary>
    public const int MerchantRow = 7311;

    /// <summary>
    /// A merchant shop squad's tag value starts with this, then its counter's building id.
    /// </summary>
    const string ShopTag = "shop:";

    /// <summary>
    /// A shop counter's work line key starts with this, then its counter's building id, so every counter is its own shop and work line.
    /// </summary>
    public const string ShopLine = "shop|";

    /// <summary>
    /// Fiefdom's merchant shop SQUAD_TEMPLATE (in Fiefdom.mod): 0.85 buy mult (15% player discount when buying, 15% lower payout when selling) and
    /// no vendor lists, so Fiefdom manages trickle-in stock itself without wiping player-sold items.
    /// </summary>
    public const string MerchantTemplate = "5-Fiefdom.mod";

    /// <summary>
    /// What a counter sells when the player has not chosen.
    /// </summary>
    public const Goods DefaultSells = Goods.Food;

    /// <summary>
    /// How close, in world units, a merchant must stand to a box or counter to move goods or tend their stall.
    /// </summary>
    const float Reach = 60;

    /// <summary>
    /// Baseline target stock value (in cats) that a brand-new merchant shop slowly accumulates toward before purse growth.
    /// </summary>
    const long BaseStockTarget = 2500;

    /// <summary>
    /// Maximum additional stock value target gained from the merchant's own purse.
    /// </summary>
    const long MaxPurseStockBonus = 25000;

    /// <summary>
    /// The inventory section a merchant carries goods in (the main grid, so worn gear and bags are never taken for stock).
    /// </summary>
    const string Carried = "main";

    /// <summary>
    /// What each counter sells, by the counter's building id (saved; a counter not here sells <see cref="DefaultSells"/>).
    /// </summary>
    readonly SortedDictionary<string, Goods> _sells = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-counter shop state (saved in format 12): BuySurplus flag, next outside trickle time, and deterministic roll counter.
    /// </summary>
    readonly SortedDictionary<string, ShopState> _shops = new(StringComparer.Ordinal);

    /// <summary>
    /// For each counter that wants town-stock surplus now, the town-stock box to fetch it from; rebuilt in the serial step, read by the job's pure checks.
    /// </summary>
    readonly Dictionary<string, string> _restockFrom = new(StringComparer.Ordinal);

    /// <summary>
    /// Where each merchant on our walk is headed, by lasting id (not saved: after a load the job starts again).
    /// </summary>
    readonly Dictionary<ulong, string> _trips = [];

    /// <summary>
    /// The town-stock goods each merchant carries for their shop, by lasting id, then item record and units (saved). Only these go into shop stock,
    /// so a merchant never sells their own belongings.
    /// </summary>
    readonly SortedDictionary<ulong, SortedDictionary<string, int>> _carrying = [];

    /// <summary>
    /// Gets or creates the shop state for counter <paramref name="counter"/>.
    /// </summary>
    ShopState ShopOf(string counter) => _shops.TryGetValue(counter, out var s) ? s : _shops[counter] = new ShopState();

    /// <summary>
    /// Whether counter <paramref name="counter"/> is set to buy surplus from town stock (default false).
    /// </summary>
    bool BuysSurplusAt(string counter) => _shops.TryGetValue(counter, out var s) && s.BuySurplus;

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
    /// The kind an item is, from its record type and item function: weapons/crossbows/ammo, armour, bags, robotic limbs/repair kits, food/raw meat,
    /// medical kits, drugs; everything else (trade goods, materials, books) is Other.
    /// </summary>
    public static Goods KindOf(ItemInstance item) => item.Type switch
    {
        FcsRecordType.Weapon or FcsRecordType.Crossbow => Goods.Weapons,
        FcsRecordType.Armour => Goods.Armour,
        FcsRecordType.Container => Goods.Bags,
        FcsRecordType.LimbReplacement => Goods.Robotics,
        _ => item.ItemFunction switch
        {
            3 or 15 => Goods.Food,
            1 or 12 => Goods.Medicine,
            2 => Goods.Robotics,
            8 => Goods.Drugs,
            5 => Goods.Weapons,
            6 => Goods.Armour,
            4 => Goods.Bags,
            _ => Goods.Other,
        },
    };

    /// <summary>
    /// What a counter sells.
    /// </summary>
    Goods SellsAt(string counter) => _sells.TryGetValue(counter, out var g) ? g : DefaultSells;

    /// <summary>
    /// The merchant shop squad for counter <paramref name="counter"/>, if one exists.
    /// </summary>
    static Squad? ShopSquadOf(World world, string counter) => TaggedSquad(world, ShopTag + counter);

    /// <summary>
    /// Clears every item out of a shop's stock so changing a shop's category or closing a shop never dumps free stock onto the ground.
    /// </summary>
    static int ClearShopStock(Shop shop)
    {
        var items = shop.Stock.Items.ToList();
        foreach (var item in items) shop.Stock.Remove(item);
        return items.Count;
    }

    /// <summary>
    /// Sets what a counter sells, when it is a counter of the player's in a Fiefdom town. Changing the goods wipes the merchant shop's current
    /// stock and resets its trickle timer so the new goods accumulate slowly from baseline.
    /// </summary>
    void Apply(World world, SetSells command)
    {
        if (OwnedInTown(world, command.Site) is not { } site || world.Machines.Find(site.Id) is not { Data.Function: ShopCounterFunction }) return;
        var before = SellsAt(site.Id);
        if (before == command.Goods) return;

        if (command.Goods == DefaultSells) _sells.Remove(site.Id);
        else _sells[site.Id] = command.Goods;

        var shopState = ShopOf(site.Id);
        shopState.NextTrickle = world.GameHours + Math.Max(1, FiefdomSettings.Current.ShopTrickleHours);
        if (ShopSquadOf(world, site.Id) is { Shop: { } shop })
        {
            int wiped = ClearShopStock(shop);
            Log($"shop {site.Id}: goods changed {before} -> {command.Goods}; wiped {wiped} stack(s), next trickle at {shopState.NextTrickle:0.0}h");
        }
        _dirty = true;
    }

    /// <summary>
    /// Sets whether the merchant at <paramref name="command"/>'s counter buys surplus from town stock.
    /// </summary>
    void Apply(World world, SetBuySurplus command)
    {
        if (OwnedInTown(world, command.Site) is not { } site || world.Machines.Find(site.Id) is not { Data.Function: ShopCounterFunction }) return;
        ShopOf(site.Id).BuySurplus = command.On;
        Log($"shop {site.Id}: buy town surplus {(command.On ? "on" : "off")}");
        _dirty = true;
    }

    /// <summary>
    /// Creates or reuses the dedicated independent merchant squad for counter <paramref name="counter"/>, sets its home (the shop building if it
    /// has a bed, else the merchant's own home), gives it its own purse, and opens its faction shop with Fiefdom's 0.85 buy-mult template.
    /// </summary>
    Squad? EnsureShopSquad(World world, PlayerTown town, BuildSite counter, string fallbackHome)
    {
        if (_ai is null || _player is null || _trade is null) return null;

        string homeId = counter.Parent is { } parent && BedsIn(world, parent) > 0 ? parent : fallbackHome;
        var squad = ShopSquadOf(world, counter.Id);
        if (squad is null)
        {
            squad = SquadManagement.NewSquad(world, _data.Db, counter.Position, "Fiefdom");
            squad.Name = $"{town.Name} merchant";
            squad.ModTags[ModId] = ShopTag + counter.Id;
            List<SimCommand> ai = [new SetSquadAutonomy(squad.Id, true, Independent: true, Owner: ModId) { Tick = world.Tick }];
            if (_data.Db.Find(ResidentPackage) is not null) ai.Add(new ChangeAi(squad.Id, ResidentPackage) { Tick = world.Tick });
            if (homeId.Length > 0) ai.Add(new SetSquadHome(squad.Id, homeId) { Tick = world.Tick });
            _ai.Inputs(world, ai);
            _player.Inputs(world,
            [
                new SetSquadUncounted(squad.Id, true, ModId) { Tick = world.Tick },
                new SetSquadPurse(squad.Id, true, ModId) { Tick = world.Tick },
            ]);
            Log($"{town.Name}: merchant squad {squad.Id} made for counter {counter.Id}");
        }

        if (squad.Shop is null)
        {
            string? template = _data.Db.Find(MerchantTemplate) is not null ? MerchantTemplate : null;
            _trade.Inputs(world, [new SetSquadShop(squad.Id, true, template, ModId) { Tick = world.Tick }]);
            Log($"{town.Name}: opened faction shop on squad {squad.Id} for counter {counter.Id} (template {template ?? "none"})");
        }
        return squad;
    }

    /// <summary>
    /// Keeps each staffed shop counter's merchant in their dedicated shop squad with an active faction shop, and closes any shop squad whose counter
    /// is gone or no longer staffed (clearing its stock first so closing a shop never drops free goods on the ground).
    /// </summary>
    void SyncMerchantSquads(World world)
    {
        if (_ai is null || _player is null || _trade is null) return;

        var staffedCounters = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var table = world.Characters;

        foreach (var (id, holder) in _holders)
        {
            if (!IsMerchant(world, holder)) continue;
            if (!_residents.TryGetValue(id, out var r)) continue;
            if (world.Buildings.Find(holder.Site) is not { Complete: true } counter) continue;
            if (FindTown(world, holder.Town) is not { } town) continue;

            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || !table.Next[slot].Alive || table.Cold(slot) is not { } cold) continue;

            staffedCounters[holder.Site] = id;
            var shopSquad = EnsureShopSquad(world, town, counter, r.Home);
            if (shopSquad is not null && cold.SquadId != shopSquad.Id)
            {
                if (MoveTo(world, c, shopSquad))
                    Log($"{town.Name}: {r.Name} moved to merchant squad {shopSquad.Id} for counter {counter.Id}");
            }
        }

        // Close any merchant shop squad that no longer has its merchant, returning any living resident to their home squad.
        foreach (var squad in world.Squads.All.ToList())
        {
            if (squad.Dead || squad.Faction != world.Player.Faction || !squad.ModTags.TryGetValue(ModId, out string? tag)) continue;
            if (!tag.StartsWith(ShopTag, StringComparison.Ordinal)) continue;

            string counterId = tag[ShopTag.Length..];
            ulong expectedMerchant = staffedCounters.GetValueOrDefault(counterId);
            foreach (var m in squad.Members.ToList())
            {
                if (!table.TryResolveNext(m, out int slot) || !table.Next[slot].Alive || table.Cold(slot) is not { LastingId.IsNone: false } cold) continue;
                ulong mid = cold.LastingId.Value;
                if (mid == expectedMerchant) continue;
                ReturnMerchantToHome(world, m, mid);
            }

            if (expectedMerchant == 0 && squad.Shop is { } shop)
            {
                int wiped = ClearShopStock(shop);
                _trade.Inputs(world, [new SetSquadShop(squad.Id, false, Owner: ModId) { Tick = world.Tick }]);
                Log($"shop {counterId}: closed unstaffed faction shop on squad {squad.Id} (wiped {wiped} stack(s))");
            }
        }
    }

    /// <summary>
    /// Moves a former merchant back into their home squad (or the first home with a free bed in their town).
    /// </summary>
    void ReturnMerchantToHome(World world, CharacterId c, ulong id)
    {
        if (!_residents.TryGetValue(id, out var r) || FindTown(world, r.Town) is not { } town) return;
        var homes = Homes(world, town);
        var target = homes.Find(h => h.Site.Id == r.Home) ?? homes.FirstOrDefault(h => h.Free > 0) ?? homes.FirstOrDefault();
        if (target is null || HomeSquad(world, town, target) is not { } homeSquad) return;

        if (MoveTo(world, c, homeSquad))
        {
            if (r.Home != target.Site.Id) _residents[id] = r with { Home = target.Site.Id };
            Log($"{town.Name}: {r.Name} returned to home squad {homeSquad.Id} ({target.Site.Name})");
        }
    }

    /// <summary>
    /// Slowly trickles outside goods from <see cref="ShopCatalog"/> into each staffed merchant's faction shop every <c>ShopTrickleHours</c> game
    /// hours, scaling up with the merchant's purse and never wiping existing stock.
    /// </summary>
    void StepTrickle(World world)
    {
        int interval = Math.Max(1, FiefdomSettings.Current.ShopTrickleHours);
        double now = world.GameHours;
        foreach (var (id, holder) in _holders)
        {
            if (!IsMerchant(world, holder)) continue;
            if (ShopSquadOf(world, holder.Site) is not { Shop: { } shop } squad) continue;
            if (LivingIn(world, squad) == 0) continue;

            var state = ShopOf(holder.Site);
            if (double.IsNaN(state.NextTrickle))
            {
                state.NextTrickle = now + interval;
                continue;
            }
            if (now < state.NextTrickle) continue;

            state.NextTrickle = now + interval;
            var sells = SellsAt(holder.Site);
            if (sells == Goods.None) continue;

            long targetValue = BaseStockTarget + Math.Min(MaxPurseStockBonus, Math.Max(0, squad.Money) * 2);
            long currentValue = StockValueOf(shop.Stock);
            if (currentValue >= targetValue) continue;

            long budget = Math.Clamp((targetValue - currentValue) / 5, 350, 3000);
            long maxItemValue = Math.Max(1200, 1200 + Math.Max(0, squad.Money));
            long addedValue = 0;
            int addedCount = 0;

            for (int attempt = 0; attempt < 6 && addedCount < 3 && addedValue < budget; attempt++)
            {
                ulong roll = Rng.Hash(world.Seed, Rng.StableHash(holder.Site), RollPurpose, (ulong)(++state.TrickleSeq));
                if (ShopCatalog.Pick(sells, maxItemValue, roll) is not { } pick) break;

                var item = _data.Items.Create(pick.Record);
                if (item is null) continue;
                if (pick.Manufacturer.Length > 0) item.CompanyId = pick.Manufacturer;
                if (!shop.Stock.HasRoomIn("main", item)) break;

                shop.Stock.Add(item);
                addedValue += Math.Max(1, pick.Value);
                addedCount++;
            }

            if (addedCount > 0)
            {
                Log($"shop {holder.Site}: trickled in {addedCount} outside item(s) (~{addedValue} cats; stock now {shop.Stock.Items.Count} stack(s), ~{currentValue + addedValue}/{targetValue} cats)");
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// Total base value of all items currently in <paramref name="stock"/>.
    /// </summary>
    long StockValueOf(Inventory stock)
    {
        long total = 0;
        foreach (var item in stock.Items) total += PriceOf(item) * item.Quantity;
        return total;
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
            if (IsTownStock(site)) yield return (site, stock);
        }
    }

    /// <summary>
    /// How many food items the town keeps back in town stock for meals: the setting per resident.
    /// </summary>
    int FoodReserve(string key) => FiefdomSettings.Current.FoodKeptPerResident * ResidentsOf(key);

    /// <summary>
    /// How many times over town stock keeps what fills each machine's inputs: once for the fill an operator is carrying, once for the next.
    /// </summary>
    const int MachineFillsKept = 2;

    /// <summary>
    /// How many of each item a town's own machines keep back in town stock: two fills of every input each complete machine of the player's in
    /// the town takes now (a farm's water, a mill's grain, an oven's flour and water, the ingredients of a bench's current craft), so the
    /// machines are not left empty by a merchant. Read from the machines themselves, so other mods' machines count by themselves.
    /// </summary>
    Dictionary<string, int> MachineNeeds(World world, int town)
    {
        var needs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in world.Machines.All)
        {
            if (m.Inputs.Length == 0) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction || TownOf(world, site) != town) continue;

            foreach (var input in m.Inputs)
            {
                if (input.Idle || string.IsNullOrEmpty(input.Item)) continue;

                needs[input.Item] = needs.GetValueOrDefault(input.Item) + MachineFillsKept * input.Recipe.Capacity;
            }
        }

        return needs;
    }

    /// <summary>
    /// The reserve of a town's stock: meals for its residents, first aid kits for its guards, and what its machines use.
    /// </summary>
    TownReserve ReserveOf(World world, int town, string key, IEnumerable<(BuildSite Site, Inventory Stock)> boxes) =>
        new(boxes.Select(b => b.Stock), FoodReserve(key), KitReserve(key), MachineNeeds(world, town));

    /// <summary>
    /// Finds, for every merchant whose shop has <see cref="ShopState.BuySurplus"/> switched on, a town-stock box holding goods the shop sells, has
    /// room for, and can afford at wholesale above the town's reserve. Run in the serial step; the job's checks read the result.
    /// </summary>
    void PlanRestocks(World world)
    {
        _restockFrom.Clear();
        foreach (var (town, state) in LiveTowns(world))
        {
            List<(BuildSite Site, Inventory Stock)>? boxes = null;
            TownReserve? reserve = null;
            foreach (var holder in _holders.Values)
            {
                if (holder.Town != state.Key || holder.Offer != 0 || _restockFrom.ContainsKey(holder.Site)) continue;
                if (!BuysSurplusAt(holder.Site)) continue;
                if (ShopSquadOf(world, holder.Site) is not { Shop: { } shop } squad || squad.Money <= 0) continue;

                boxes ??= [.. TownStock(world, town.Number)];
                reserve ??= ReserveOf(world, town.Number, state.Key, boxes);
                var sells = SellsAt(holder.Site);
                foreach (var (site, stock) in boxes)
                {
                    if (stock.Items.Any(i => Wanted(i, sells, reserve) && shop.Stock.HasRoomIn("main", i) && WholesalePriceOf(i) <= squad.Money))
                    {
                        _restockFrom[holder.Site] = site.Id;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Wholesale price a merchant pays the player for one unit of <paramref name="item"/> bought from town stock.
    /// </summary>
    long WholesalePriceOf(ItemInstance item) =>
        Math.Max(1, PriceOf(item) * Math.Clamp(FiefdomSettings.Current.MerchantWholesalePercent, 1, 100) / 100);

    /// <summary>
    /// Whether an item is one the counter sells and some of it is above the town's reserve.
    /// </summary>
    static bool Wanted(ItemInstance item, Goods sells, TownReserve reserve) => (KindOf(item) & sells) != 0 && reserve.Spare(item) > 0;

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
    /// The goods each merchant carries for their shop: true when <paramref name="cold"/> carries any.
    /// </summary>
    bool CarriesFor(CharacterCold cold, string counter) =>
        !cold.LastingId.IsNone && cold.Inventory is { } inv
        && inv.Items.Any(i => i.Section == Carried && CarriedUnits(cold.LastingId.Value, i.Record) > 0);

    /// <summary>
    /// The serial part of the merchant's system: keeps merchant squads and faction shops in sync, runs trickle-in outside restocking, plans
    /// optional town-surplus wholesale pickups, and completes merchant walks.
    /// </summary>
    void StepMerchants(World world)
    {
        SyncMerchantSquads(world);
        StepTrickle(world);
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
            if (target == holder.Site && ShopSquadOf(world, holder.Site) is { Shop: { } shop })
            {
                int moved = 0;
                foreach (var item in inv.Items.Where(i => i.Section == Carried).ToList())
                {
                    int units = Math.Min(item.Quantity, CarriedUnits(id, item.Record));
                    if (units <= 0 || !shop.Stock.HasRoomIn("main", item)) continue;

                    int put = ItemMoves.Move(inv, item, units, shop.Stock, null);
                    Carry(id, item.Record, -put);
                    moved += put;
                }
                if (moved > 0)
                {
                    Log($"{_residents.GetValueOrDefault(id)?.Name}: moved {moved} surplus item(s) into shop {target}; shop now {shop.Stock.Items.Count} stack(s)");
                    _dirty = true;
                }
            }
            else if (target != holder.Site && BuysSurplusAt(holder.Site)
                && world.Machines.Find(target)?.Storage is { } box
                && ShopSquadOf(world, holder.Site) is { Shop: { } shopRoom } squad && squad.Money > 0)
            {
                int town = TownOf(world, site);
                var reserve = ReserveOf(world, town, holder.Town, TownStock(world, town));
                int taken = 0;
                long paid = 0;
                foreach (var item in box.Items.ToList())
                {
                    if (!Wanted(item, sells, reserve)) continue;
                    if (!shopRoom.Stock.HasRoomIn("main", item) || !inv.HasRoomIn(Carried, item)) continue;

                    long unitWholesale = WholesalePriceOf(item);
                    int affordable = (int)Math.Min(reserve.Spare(item), squad.Money / unitWholesale);
                    if (affordable <= 0) continue;

                    int units = ItemMoves.Move(box, item, affordable, inv, Carried);
                    if (units <= 0) continue;

                    long cost = unitWholesale * units;
                    squad.Money -= cost;
                    world.Player.Money += cost;
                    Carry(id, item.Record, units);
                    taken += units;
                    paid += cost;
                    reserve.Took(item, units);
                }
                if (taken > 0)
                {
                    world.Machines.Touch();
                    _dirty = true;
                    Log($"{_residents.GetValueOrDefault(id)?.Name}: bought {taken} surplus item(s) from town stock {target} for {paid} cats wholesale (purse now {squad.Money})");
                }
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
    /// Whether the character in <paramref name="slot"/> stands far enough from <paramref name="counter"/> to walk back and tend their stall.
    /// </summary>
    static bool FarFromCounter(World world, int slot, string counter)
    {
        if (world.Buildings.Find(counter) is not { } site) return false;
        var at = world.Characters.Next[slot].Position;
        return Vector2.Distance(new(at.X, at.Z), new(site.Position.X, site.Position.Z)) > Reach;
    }

    /// <summary>
    /// Starts a merchant's next walk: to the counter with carried surplus goods, else to a town-stock box when buying surplus, else back to their
    /// counter to tend the stall.
    /// </summary>
    void StartMerchant(World world, int slot, int k)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { LastingId.IsNone: false } cold || k >= cold.Jobs.Count || cold.Jobs[k].Site is not { } counter) return;

        string? target = CarriesFor(cold, counter) ? counter
            : _restockFrom.GetValueOrDefault(counter)
            ?? (FarFromCounter(world, slot, counter) ? counter : null);
        if (target is null || world.Buildings.Find(target) is not { } site) return;

        var result = TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, (TaskType)MerchantRow, TaskSource.Job)
        {
            Job = k, Point = site.Position, Mode = (byte)Meitou.Simulation.Movement.SpeedMode.Walk,
        });
        _trips[cold.LastingId.Value] = target;
        Log($"{cold.Name}: merchant walk to {site.Name} ({target}): {result}");
    }

    /// <summary>
    /// The merchant's job for the decision step: carries surplus goods to the shop, fetches affordable town surplus when enabled, or walks back to
    /// tend the stall during shift.
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
            if (owner.CarriesFor(cold, counter) || owner._restockFrom.ContainsKey(counter) || FarFromCounter(world, slot, counter))
                return DecisionCheck.Run;
            return DecisionCheck.No("at stall");
        }

        /// <summary>
        /// Starts the merchant's next walk.
        /// </summary>
        public void Start(World world, int slot, int k) => owner.StartMerchant(world, slot, k);
    }
}
