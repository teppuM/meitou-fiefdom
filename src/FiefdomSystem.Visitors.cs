using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Fiefdom;

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// Fiefdom's visitors' package (in Fiefdom.mod): shopping at the town's counters, sitting about in its public buildings and wandering the
    /// town, all day.
    /// </summary>
    public const string VisitorPackage = "3-Fiefdom.mod";

    /// <summary>
    /// How long, in game hours, visitors stay once in town.
    /// </summary>
    const double VisitHours = 8;

    /// <summary>
    /// Where visitors go in a town: its public building with a shop counter inside, or a counter standing outdoors (the one with the most for
    /// sale), else none (they stay at the town's middle).
    /// </summary>
    BuildSite? ShopBuilding(World world, PlayerTown town) =>
        PublicCounters(world, town).GroupBy(c => c.Site.Parent ?? c.Site.Id).OrderByDescending(g => g.Sum(c => c.Stock.Items.Count))
            .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => world.Buildings.Find(g.Key)).FirstOrDefault();

    /// <summary>
    /// The town's shop counters of the player's that sell to visitors, with their goods: complete, and inside a complete public building of the
    /// town or standing outdoors in it (where the placement rule lets furniture stand; such a counter is open while the town is).
    /// </summary>
    IEnumerable<(BuildSite Site, Meitou.Simulation.Items.Inventory Stock)> PublicCounters(World world, PlayerTown town)
    {
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != ShopCounterFunction || m.Storage is not { } stock) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction) continue;
            if (!SellsToVisitors(world, town, site)) continue;
            yield return (site, stock);
        }
    }

    /// <summary>
    /// Whether a counter of the town sells to its visitors: its building is complete, public and in the town, or it stands outdoors in the town.
    /// </summary>
    static bool SellsToVisitors(World world, PlayerTown town, BuildSite counter) => counter.Parent is { } parent
        ? world.Buildings.Find(parent) is { Complete: true, Public: true } building && TownOf(world, building) == town.Number
        : TownOf(world, counter) == town.Number;

    /// <summary>
    /// The visitors buy at the town's counters: each member buys one item, the first on the counters (food first) the group's purse can pay
    /// for and the member can carry, at the item's own value from the group's purse into the player's money, as a sale at a player's counter
    /// pays, the counter's merchant owed their share as for any sale there. The news says what they spent.
    /// </summary>
    void VisitorsBuy(World world, PlayerTown town, TownState state, SettlerGroup group, Squad squad)
    {
        var counters = PublicCounters(world, town).ToList();
        var table = world.Characters;
        int bought = 0;
        long spent = 0;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int slot) || !table.Next[slot].Alive || table.Cold(slot) is not { Medical.Incapacitated: false, Inventory: { } inv }) continue;
            var goods = counters.SelectMany(c => c.Stock.Items.Select(i => (c.Stock, Item: i)))
                .OrderByDescending(p => p.Item.IsFood).ThenBy(p => p.Item.Record, StringComparer.Ordinal).ToList();
            if (goods.Count == 0) break;
            if (Affordable(goods, squad.Money, inv) is not { } choice) continue;

            var item = choice.Item;
            long price = PriceOf(item);
            var one = item.Quantity == 1 ? item : item.WithQuantity(1);
            if (one == item) choice.Stock.Remove(item);
            else item.Quantity -= 1;
            inv.AddTo(Carried, one);
            squad.Money -= price;
            world.Player.Money += price;
            OweShare(world, counters.First(c => c.Stock == choice.Stock).Site.Id, price, $"{one.Name} to a visitor, group {group.Serial}");
            bought++;
            spent += price;
            Log($"group {group.Serial}: {table.Cold(slot)!.Name} bought {one.Name} for {price}");
        }
        if (bought > 0) world.Machines.Touch();
        string name = _data.Db.Find(group.Template)?.Name ?? "travellers";
        state.Tell(world.GameHours, bought > 0 ? $"visitors ({name}) bought {bought} item{(bought > 1 ? "s" : "")} for {spent} cats" : $"visitors ({name}) found nothing they could buy");
        Log($"group {group.Serial}: bought {bought} for {spent}; purse now {squad.Money}, player has {world.Player.Money}");
    }

    /// <summary>
    /// The first of <paramref name="goods"/> the group can pay for out of <paramref name="purse"/> and the member can carry in
    /// <paramref name="carried"/>; null when there is none.
    /// </summary>
    (Meitou.Simulation.Items.Inventory Stock, Meitou.Simulation.Items.ItemInstance Item)? Affordable(List<(Meitou.Simulation.Items.Inventory Stock, Meitou.Simulation.Items.ItemInstance Item)> goods,
        long purse, Meitou.Simulation.Items.Inventory carried)
    {
        foreach (var g in goods)
        {
            if (PriceOf(g.Item) > purse) continue;

            var one = g.Item.Quantity == 1 ? g.Item : g.Item.WithQuantity(1);
            if (carried.HasRoomIn(Carried, one)) return g;
        }

        return null;
    }

    /// <summary>
    /// What one of <paramref name="item"/> costs a visitor: its record's value, at least 1.
    /// </summary>
    long PriceOf(Meitou.Simulation.Items.ItemInstance item) => Math.Max(1, _data.Db.Find(item.Record)?.GetInt("value") ?? 1);

    /// <summary>
    /// Brings visitors to a town open to the public when they are due: one group at a time, every so often (the setting, give or take half),
    /// drawn from the region's own wandering groups as settlers are, under the same race and weather rules.
    /// </summary>
    void Visits(World world, PlayerTown town, TownState state)
    {
        double every = FiefdomSettings.Current.VisitorHours;
        if (!town.Public || every <= 0 || _groups.Values.Any(g => g.Town == state.Key && g.Visiting)) return;
        double now = world.GameHours;
        if (double.IsNaN(state.NextVisit))
        {
            ulong roll = Rng.Hash(world.Seed, (ulong)_serial + 1, RollPurpose, 0x7151);
            state.NextVisit = now + every * (0.5 + Rng.Float(roll));
            _dirty = true;
            return;
        }
        if (now < state.NextVisit) return;
        if (!Bring(world, town, state, 99, visiting: true)) state.NextVisit = now + every;
    }

    /// <summary>
    /// Moves one visiting group on: on its way, walks its leader in once it is in the world; in town, it runs the visitors' package until its
    /// stay is over, then gets its own package back and goes on.
    /// </summary>
    void StepVisit(World world, SettlerGroup group)
    {
        double now = world.GameHours;
        var state = TownOf(group.Town);
        if (FindTown(world, group.Town) is not { } town || !town.Public)
        {
            EndVisit(world, group, state, "turned back: the town is not open to the public");
            return;
        }
        if (PlatoonOf(world, group) is not { Dead: false } p)
        {
            _groups.Remove(group.Serial);
            _platoons.Remove(group.Serial);
            state.NextVisit = double.NaN;
            return;
        }

        var squad = p.State == PlatoonState.Loaded ? world.Squads.Find(p.SquadId) : null;
        if (group.Stage == GroupStage.Travelling)
        {
            if (now - group.Since > TravelHours)
            {
                EndVisit(world, group, state, "gave up on the way");
                return;
            }
            if (squad is null || !Arrived(world, town, group, squad)) return;

            group.Stage = GroupStage.Waiting;
            group.ArrivedAt = now;
            group.Walks = 0;
            _lastDistance.Remove(group.Serial);
            foreach (var id in squad.Members)
                if (world.Characters.TryResolveNext(id, out int slot)) StopWalk(world, slot);
            group.Package = squad.Ai?.Current?.Id ?? "";
            if (_ai is not null && _data.Db.Find(VisitorPackage) is not null)
                _ai.Inputs(world, [new ChangeAi(squad.Id, VisitorPackage) { Tick = world.Tick }]);
            string name = _data.Db.Find(group.Template)?.Name ?? "travellers";
            state.Tell(now, $"visitors ({name}) came to town");
            Log($"group {group.Serial}: visitors in {town.Name}, package {group.Package} -> {VisitorPackage}, squad {squad.Id} of {squad.Members.Count}, purse {squad.Money}");
            _dirty = true;
            return;
        }
        if (now - group.ArrivedAt > VisitHours)
        {
            EndVisit(world, group, state, "finished their visit and went on");
            return;
        }
        if (squad is null || group.Shopped) return;
        if (ShopBuilding(world, town) is { } shop)
        {
            if (!Arrived(world, town, group, squad, new System.Numerics.Vector2(shop.Position.X, shop.Position.Z), 80)) return;
            foreach (var id in squad.Members)
                if (world.Characters.TryResolveNext(id, out int slot)) StopWalk(world, slot);
            VisitorsBuy(world, town, state, group, squad);
        }
        group.Shopped = true;
        _dirty = true;
    }

    /// <summary>
    /// Ends a visit: the group gets its own package back (when it was in the world) and is given back to the population.
    /// </summary>
    void EndVisit(World world, SettlerGroup group, TownState state, string reason)
    {
        if (PlatoonOf(world, group) is { State: PlatoonState.Loaded } p && world.Squads.Find(p.SquadId) is { } squad && _ai is not null
            && group.Package.Length > 0 && _data.Db.Find(group.Package) is not null)
        {
            _ai.Inputs(world, [new ChangeAi(squad.Id, group.Package) { Tick = world.Tick }]);
            Log($"group {group.Serial}: back on its own package {group.Package}, purse {squad.Money}");
        }
        Release(world, group, state, reason);
        state.NextVisit = double.NaN;
    }
}
