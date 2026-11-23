using Meitou.Simulation;
using Meitou.Simulation.Trading;

namespace Fiefdom;

public sealed partial class FiefdomSystem : ITradeObserver
{
    #region The merchants' share of takings

    /// <summary>
    /// What each merchant is owed of the takings at their counter and not yet paid, in hundredths of a cat, by lasting id (saved).
    /// </summary>
    readonly SortedDictionary<ulong, long> _shareOwed = [];

    /// <summary>
    /// What each town's merchants have kept of the takings since the last day change, by town key (for the news only, not saved).
    /// </summary>
    readonly SortedDictionary<string, long> _sharesToday = new(StringComparer.Ordinal);

    /// <summary>
    /// Hears every completed deal: a shopper's purchase from one of the player's shop counters in a Fiefdom town owes the counter's merchant
    /// their share of the price. Deals in the trade window, buy-backs, sales from anywhere else, and a purchase by one of the player's own
    /// squads without a purse of its own (paid with the player's money, so nothing came in) are left alone. Called serially; changes only the
    /// mod's own state, and the share is paid in the next slow step.
    /// </summary>
    public void Traded(World world, in TradeSale sale)
    {
        if (sale.Kind != TradeSaleKind.Shopping || sale.Buyback || sale.Price <= 0) return;
        if (sale.Seller.Faction != world.Player.Faction || sale.Site.Length == 0) return;
        if (sale.Buyer.Faction == world.Player.Faction && world.Squads.Find(sale.Buyer.Squad) is not { OwnPurse: true }) return;

        OweShare(world, sale.Site, sale.Price, $"{sale.Units} {sale.Record} to {world.Characters.NameOf(sale.Buyer.Character)}, squad {sale.Buyer.Squad}");
    }

    /// <summary>
    /// Owes the merchant of the counter <paramref name="counter"/> their share of <paramref name="price"/>, when it is a counter of a Fiefdom
    /// town with a merchant on its line: the one on shift now, else the first of its holders. A counter nobody keeps owes nothing. Logs the
    /// sale (<paramref name="what"/> says what was sold to whom).
    /// </summary>
    void OweShare(World world, string counter, long price, string what)
    {
        int percent = FiefdomSettings.Current.MerchantSharePercent;
        if (percent <= 0 || _holders.Count == 0) return;
        if (OwnedInTown(world, counter) is null || world.Machines.Find(counter) is not { Data.Function: ShopCounterFunction }) return;
        if (MerchantOf(world, counter) is not { } id) return;

        _shareOwed[id] = _shareOwed.GetValueOrDefault(id) + price * percent;
        Log($"{_residents.GetValueOrDefault(id)?.Name}: owed {price * percent / 100.0:0.##} of a sale for {price} at {counter} ({what})");
    }

    /// <summary>
    /// The lasting id of the merchant keeping <paramref name="counter"/>: the holder on shift now, else the first holder; null with none.
    /// </summary>
    ulong? MerchantOf(World world, string counter)
    {
        ulong? first = null;
        foreach (var (id, holder) in _holders)
        {
            if (holder.Site != counter || holder.Offer != 0) continue;
            if (OnShift(holder.Shift, world.GameHours)) return id;
            first ??= id;
        }
        return first;
    }

    /// <summary>
    /// Pays each merchant the whole cats they are owed, from the player's money (where the takings went) into their home's purse, keeping the
    /// fraction for later. A share the player's money cannot cover waits, and so does one owed to a merchant who cannot be paid now (out of the
    /// world, or not in their home's squad); one owed to someone no longer a resident is dropped. Runs with the posts, once a game second.
    /// </summary>
    void PayShares(World world)
    {
        if (_shareOwed.Count == 0) return;

        var table = world.Characters;
        foreach (var (id, owed) in _shareOwed.ToList())
        {
            if (!_residents.TryGetValue(id, out var r))
            {
                _shareOwed.Remove(id);
                continue;
            }

            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (world.Squads.Find(cold.SquadId) is not { OwnPurse: true } home) continue;

            long cats = owed / 100;
            if (cats == 0 || world.Player.Money < cats) continue;

            world.Player.Money -= cats;
            home.Money += cats;
            _shareOwed[id] = owed - cats * 100;
            _sharesToday[r.Town] = _sharesToday.GetValueOrDefault(r.Town) + cats;
            Log($"{r.Name}: kept {cats} of the takings, home purse now {home.Money}");
        }
    }

    /// <summary>
    /// At a day change: each town's news says what its merchants kept of the day's takings.
    /// </summary>
    void TellShares(World world)
    {
        foreach (var (key, cats) in _sharesToday)
            if (cats > 0) TownOf(key).Tell(world.GameHours, $"merchants kept {cats} cats of the takings ({FiefdomSettings.Current.MerchantSharePercent}%)");
        _sharesToday.Clear();
    }

    #endregion
}
