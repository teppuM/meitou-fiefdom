using Meitou.Simulation;
using Meitou.Simulation.Factions;

namespace Fiefdom;

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// A resident counts as starving below this hunger (the game's own line: 0 to 3, below 1 starving).
    /// </summary>
    const float Starving = 1;

    /// <summary>
    /// The pull score counts residents who left hungry within this many game hours.
    /// </summary>
    const double LeavingMemory = 72;

    /// <summary>
    /// The last game day residents were paid their outside income (saved, so a load does not pay the day twice).
    /// </summary>
    int _paidDay = -1;

    /// <summary>
    /// The game hour each starving resident started starving, by lasting id (saved).
    /// </summary>
    readonly SortedDictionary<ulong, double> _starvingSince = [];

    /// <summary>
    /// Once a game day: workers are paid their wages (<see cref="PayWages"/>), and every resident without a post gets their outside income in
    /// their home's purse. The first day the mod sees is only noted.
    /// </summary>
    void PayOutsideIncome(World world)
    {
        int day = TownState.DayOf(world.GameHours);
        if (_paidDay < 0)
        {
            _paidDay = day;
            return;
        }
        if (day <= _paidDay) return;

        int days = day - _paidDay;
        _paidDay = day;
        PayWages(world);
        long each = (long)FiefdomSettings.Current.OutsideIncome * days;
        if (each <= 0) return;

        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (squad.Dead || squad.Faction != world.Player.Faction || !squad.OwnPurse) continue;
            if (!squad.ModTags.TryGetValue(ModId, out string? tag) || !tag.StartsWith(HomeTag, StringComparison.Ordinal)) continue;

            int living = squad.Members.Count(m => table.IsAlive(m) && table.TryResolveNext(m, out int s) && table.Cold(s) is { LastingId.IsNone: false } c
                && _residents.ContainsKey(c.LastingId.Value) && !HasRole(c.LastingId.Value));
            squad.Money += each * living;
            if (living > 0) Log($"{squad.Name}: {living} resident(s) without a post paid {each * living} outside income for {days} day(s), purse now {squad.Money}");
        }
    }

    /// <summary>
    /// Residents who have been starving for the set number of days leave the town (dismissed, as a dismissed character leaves the player's
    /// faction) rather than starve in the street; their town's pull falls for three days.
    /// </summary>
    void CheckHunger(World world)
    {
        double now = world.GameHours;
        double limit = FiefdomSettings.Current.StarvingDaysBeforeLeaving * 24;
        var table = world.Characters;
        foreach (var (id, r) in _residents.ToList())
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true, Medical: { Dead: false } medical })
            {
                _starvingSince.Remove(id);
                continue;
            }

            if (medical.Hunger >= Starving)
            {
                _starvingSince.Remove(id);
                continue;
            }
            if (!_starvingSince.TryGetValue(id, out double since))
            {
                _starvingSince[id] = now;
                continue;
            }
            if (now - since < limit || _player is null) continue;

            _player.Inputs(world, [new DismissCommand(c) { Tick = world.Tick }]);
            if (table.Cold(slot) is { IsPlayer: true }) continue;

            _residents.Remove(id);
            _starvingSince.Remove(id);
            var state = TownOf(r.Town);
            state.Leavings.Add(now);
            state.Tell(now, $"{r.Name} left the town hungry");
            Log($"{r.Name} left {FindTown(world, r.Town)?.Name ?? r.Town} hungry");
            _dirty = true;
        }
    }
}
