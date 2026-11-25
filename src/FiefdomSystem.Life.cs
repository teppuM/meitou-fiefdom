using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Player;

namespace Fiefdom;

/// <summary>
/// Sets whether a town's guards attack anyone of a race (people or animals) on sight inside the town.
/// </summary>
public sealed record SetAttackOnSight(int Town, string Race, bool Attack) : SettlerCommand;

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// How long, in game hours, a race seen in or near town stays on the Policies tab after it was last seen.
    /// </summary>
    const double SeenFor = 24 * 7;

    /// <summary>
    /// How near, in world units, one of the player's own characters must come before a resident greets them.
    /// </summary>
    const float GreetingRange = 150;

    /// <summary>
    /// The greetings residents give the player's own people, by what the resident is doing; one is picked by the resident and the day.
    /// </summary>
    static readonly string[] Greetings = ["Good day to you.", "Welcome back.", "The town's in good order.", "Good to see you here.", "All well here."];
    static readonly string[] GuardGreetings = ["All quiet.", "Nothing to report.", "We're keeping watch.", "The walls are holding."];
    static readonly string[] WorkerGreetings = ["Hard at it.", "Work's going well.", "Busy day.", "Another day's work."];

    /// <summary>
    /// The day each resident last greeted the player's people, by lasting id (not saved: a reload may bring one more greeting), and each town's
    /// last greeting tick.
    /// </summary>
    readonly Dictionary<ulong, int> _greeted = [];
    readonly Dictionary<string, long> _lastGreeting = new(StringComparer.Ordinal);

    /// <summary>
    /// The outsider each patrol guard was sent after, by guard (not saved): a guard is ordered once per target, and called off when the target
    /// is down or gone from the town, so guards do not swing between the chase and their duty.
    /// </summary>
    readonly Dictionary<CharacterId, CharacterId> _hunts = [];

    /// <summary>
    /// How far past the town's edge a hunted outsider may run before its guards are called back.
    /// </summary>
    const float HuntBeyond = 300;

    /// <summary>
    /// Sets a town's attack-on-sight rule for a race.
    /// </summary>
    void Apply(World world, PlayerTown town, TownState state, SetAttackOnSight command)
    {
        if (command.Attack) state.AttackOnSight.Add(command.Race);
        else state.AttackOnSight.Remove(command.Race);
        state.Tell(world.GameHours, $"guards {(command.Attack ? "now attack" : "no longer attack")} {SettlerData.RaceName(command.Race)} on sight");
        _dirty = true;
    }

    /// <summary>
    /// Every two seconds: notes the races of everyone not of the player's faction in or near each town (for the Policies tab), sends the guards
    /// after anyone of a race the town attacks on sight, and lets a resident greet one of the player's own people walking by.
    /// </summary>
    void StepLife(World world)
    {
        if (world.Tick % 60 != 0) return;
        foreach (var (town, state) in LiveTowns(world))
        {
            var centre = Centre(town);
            float r2 = town.Radius * town.Radius, near2 = (town.Radius + 500) * (town.Radius + 500);
            var table = world.Characters;
            var outsiders = new List<(int Slot, string Race, float D2)>();
            var mine = new List<Vector3>();
            for (int i = 0; i < table.Next.Length; i++)
            {
                if (!table.Next[i].Alive || table.Cold(i) is not { } cold || cold.Medical is { Dead: true }) continue;
                var at = table.Next[i].Position;
                float d2 = Vector2.DistanceSquared(new(at.X, at.Z), centre);
                if (d2 > near2) continue;
                if (cold.Race?.StringId is { } race && (!state.Seen.TryGetValue(race, out double last) || world.GameHours - last > 1))
                    state.Seen[race] = world.GameHours;
                if (cold.IsPlayer)
                {
                    if (d2 <= r2 && world.Squads.Find(cold.SquadId) is { Independent: false } squad && !squad.ModTags.ContainsKey(ModId)) mine.Add(at);
                    continue;
                }
                if (cold.Race?.StringId is { } outsiderRace && d2 <= r2) outsiders.Add((i, outsiderRace, d2));
            }
            foreach (var race in state.Seen.Where(p => world.GameHours - p.Value > SeenFor && !state.AttackOnSight.Contains(p.Key)).Select(p => p.Key).ToList())
                state.Seen.Remove(race);

            if (_hunts.Count > 0) CallOffHunts(world, town);
            if (state.AttackOnSight.Count > 0 && outsiders.Count > 0) Enforce(world, town, state, outsiders);
            if (mine.Count > 0) Greet(world, state, mine);
        }
    }

    /// <summary>
    /// Sends the town's free patrol guards (awake, not fighting, not already sent after someone) after the nearest outsider in town of a race the
    /// town attacks on sight, once. Gate guards keep their gates: their own package fights what comes at them without leaving town.
    /// </summary>
    void Enforce(World world, PlayerTown town, TownState state, List<(int Slot, string Race, float D2)> outsiders)
    {
        var table = world.Characters;
        var target = outsiders.Where(o => state.AttackOnSight.Contains(o.Race) && table.Cold(o.Slot) is { Medical.Incapacitated: false })
            .OrderBy(o => o.D2).Select(o => (int?)o.Slot).FirstOrDefault();
        if (target is not { } slot) return;
        var id = table.IdOf(slot);

        var guards = new List<CharacterId>();
        foreach (var house in Guardhouses(world, town))
        {
            if (house.Gate || house.Squad is not { } squad) continue;
            foreach (var m in squad.Members)
                if (!_hunts.ContainsKey(m) && table.TryResolveNext(m, out int g) && table.Next[g].Alive
                    && table.Cold(g) is { InCombat: false, Medical.Incapacitated: false })
                    guards.Add(m);
        }
        if (guards.Count == 0) return;
        foreach (var g in guards) _hunts[g] = id;
        world.Commands.Enqueue(new AttackOrder(guards, id) { Tick = world.Tick + 1 });
        string race = SettlerData.RaceName(table.Cold(slot)!.Race!.StringId);
        state.Tell(world.GameHours, $"the guards went after {table.Cold(slot)!.Name} ({race}) on sight");
        Log($"{town.Name}: {guards.Count} guard(s) ordered to attack {table.Cold(slot)!.Name} ({race})");
        _dirty = true;
    }

    /// <summary>
    /// Ends the hunts of this town's guards whose target is down, dead, gone or more than <see cref="HuntBeyond"/> past the town's edge: a guard
    /// still on the order is stopped, so it goes back to its duty once rather than chasing out of town.
    /// </summary>
    void CallOffHunts(World world, PlayerTown town)
    {
        var table = world.Characters;
        var centre = Centre(town);
        float reach = town.Radius + HuntBeyond;
        var guards = Guardhouses(world, town).Where(h => h.Squad is not null).SelectMany(h => h.Squad!.Members).ToHashSet();
        foreach (var (guard, target) in _hunts.ToList())
        {
            if (!guards.Contains(guard)) continue;
            bool on = table.TryResolveNext(target, out int t) && table.Next[t].Alive && table.Cold(t) is { Medical.Incapacitated: false }
                && Vector2.Distance(new(table.Next[t].Position.X, table.Next[t].Position.Z), centre) <= reach;
            if (on) continue;
            _hunts.Remove(guard);
            if (table.TryResolveNext(guard, out int g) && PlayerOrders.Holds(world, g)) world.Commands.Enqueue(new StopCommand([guard]) { Tick = world.Tick + 1 });
        }
    }

    /// <summary>
    /// A resident near one of the player's own people, awake and not fighting, who has not greeted anyone today, says a greeting aloud: at most
    /// one greeting in a town every twenty seconds. A worker unpaid or hungry says so instead.
    /// </summary>
    void Greet(World world, TownState state, List<Vector3> mine)
    {
        if (_lastGreeting.TryGetValue(state.Key, out long last) && world.Tick - last < 600) return;
        int day = TownState.DayOf(world.GameHours);
        var table = world.Characters;
        foreach (var (id, r) in _residents)
        {
            if (r.Town != state.Key || _greeted.GetValueOrDefault(id, -1) == day) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || !table.Next[slot].Alive) continue;
            if (table.Cold(slot) is not { InCombat: false, Medical: { Incapacitated: false } medical } cold) continue;
            var at = table.Next[slot].Position;
            if (!mine.Any(p => Vector2.DistanceSquared(new(p.X, p.Z), new(at.X, at.Z)) <= GreetingRange * GreetingRange)) continue;

            string line = _pay.GetValueOrDefault(id)?.Unpaid > 0 ? "My wages are late."
                : medical.Hunger < Hungry ? "I could do with a meal."
                : Pick(IsGuard(id) ? GuardGreetings : _holders.ContainsKey(id) || _runners.ContainsKey(id) ? WorkerGreetings : Greetings, id, day);
            world.Bark(c, line);
            Log($"{r.Name} greeted your people: \"{line}\"");
            _greeted[id] = day;
            _lastGreeting[state.Key] = world.Tick;
            return;
        }
    }

    /// <summary>
    /// One of the lines, the same for a resident all day.
    /// </summary>
    static string Pick(string[] lines, ulong id, int day) => lines[(int)((id * 31 + (ulong)day) % (ulong)lines.Length)];
}
