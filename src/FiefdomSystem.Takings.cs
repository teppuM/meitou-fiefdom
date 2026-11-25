using Meitou.Simulation;
using Meitou.Simulation.Trading;

namespace Fiefdom;

public sealed partial class FiefdomSystem : ITradeObserver
{
    #region Merchant sales cuts

    /// <summary>
    /// Legacy format 11 owed shares (kept for save-load compatibility with older saves; new merchant sales pay the player's cut directly).
    /// </summary>
    readonly SortedDictionary<ulong, long> _shareOwed = [];

    /// <summary>
    /// Legacy format 11 daily merchant shares (kept for save-load compatibility).
    /// </summary>
    readonly SortedDictionary<string, long> _sharesToday = new(StringComparer.Ordinal);

    /// <summary>
    /// What each town's merchants paid the player as the player's cut of visitor sales since the last day change, by town key (saved in format 12).
    /// </summary>
    readonly SortedDictionary<string, long> _playerCutToday = new(StringComparer.Ordinal);

    /// <summary>
    /// Hears completed deals. Unstaffed player shop counters pay 100% directly to the player; staffed merchant shops run as faction shops where
    /// visitor sales pay the player's cut directly in <see cref="VisitorsBuy"/>.
    /// </summary>
    public void Traded(World world, in TradeSale sale)
    {
        // Nothing to deduct from player-owned unstaffed counters; faction-shop visitor sales settle their cut directly.
    }

    /// <summary>
    /// Whether <paramref name="holder"/> keeps a shop counter (the counter's own post, not another mod's job offered on it).
    /// </summary>
    static bool IsMerchant(World world, PostHolder holder) =>
        holder.Offer == 0 && world.Machines.Find(holder.Site) is { Data.Function: ShopCounterFunction };

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
    /// Pays any remaining legacy format 11 owed shares so old saves settle cleanly.
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
            if (world.Squads.Find(cold.SquadId) is not { OwnPurse: true } squad) continue;

            long cats = owed / 100;
            if (cats == 0 || world.Player.Money < cats) continue;

            world.Player.Money -= cats;
            squad.Money += cats;
            _shareOwed[id] = owed - cats * 100;
            if (_shareOwed[id] < 100) _shareOwed.Remove(id);
        }
    }

    /// <summary>
    /// Records the player's cut from a merchant's visitor sale for the day's news.
    /// </summary>
    void RecordPlayerCut(string townKey, long cut)
    {
        if (cut > 0) _playerCutToday[townKey] = _playerCutToday.GetValueOrDefault(townKey) + cut;
    }

    /// <summary>
    /// At a day change: each town's news says what its merchants paid the player from visitor sales.
    /// </summary>
    void TellShares(World world)
    {
        foreach (var (key, cats) in _playerCutToday)
            if (cats > 0) TownOf(key).Tell(world.GameHours, $"merchants paid you {cats} cats ({FiefdomSettings.Current.PlayerSalesCutPercent}% of their visitor sales)");
        _playerCutToday.Clear();
        _sharesToday.Clear();
    }

    #endregion
}
