using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Fiefdom;

/// <summary>
/// Guards patch people up. Every second the mod sends a guard who carries a first aid kit to each person of the player's faction in the town who
/// needs first aid (the game's own test), residents, guards and the player's own people alike: the game's own first aid order, the one the player
/// gives, so it gets a guard out of bed and the game does the walking and bandaging. Nearest free guard first, one patient each. A guard carrying
/// no kit fetches one from a town-stock box (the mod's own job, as the food runners fetch meals); merchants leave two kits per guard in town stock.
/// </summary>
public sealed partial class FiefdomSystem
{
    /// <summary>
    /// The task row of the guard's kit fetch (claimed with <c>AddJobType</c>), which its walk also runs as.
    /// </summary>
    public const int KitRow = 7313;

    /// <summary>
    /// Ticks after which a guard sent to a patient who is not treating them is free again (the order failed or the treatment ended).
    /// </summary>
    const int TreatStartTicks = 90;

    /// <summary>
    /// Ticks a guard sent to a patient may spend still on the way (its move or treat order waiting or walking) before the orders are cleared
    /// and the guard is free again.
    /// </summary>
    const int TreatWalkTicks = 1800;

    /// <summary>
    /// Ticks a patient whose treatment did not start waits before a guard is sent again.
    /// </summary>
    const int TreatRetryTicks = 900;

    /// <summary>
    /// The town-stock box each guard is fetching a kit from, by lasting id. Not saved: after a load the job plans afresh.
    /// </summary>
    readonly Dictionary<ulong, string> _kitTrips = [];

    /// <summary>
    /// The guards whose kit walk is under way.
    /// </summary>
    readonly HashSet<ulong> _kitWalking = [];

    /// <summary>
    /// Each guard sent to a patient: the patient's lasting id and the tick the guard was sent. Not saved.
    /// </summary>
    readonly Dictionary<ulong, (ulong Patient, long Sent)> _treating = [];

    /// <summary>
    /// Patients no guard is sent to before this tick, by lasting id (their last treatment did not start). Not saved.
    /// </summary>
    readonly Dictionary<ulong, long> _treatRetry = [];

    /// <summary>
    /// Whether an item is a first aid kit (the game's item function 1).
    /// </summary>
    static bool IsFirstAidKit(ItemInstance item) => TownReserve.IsFirstAidKit(item);

    /// <summary>
    /// The first aid kits a town keeps in stock for its guards: two for each guard (a basic kit is used up after a patient or two).
    /// </summary>
    int KitReserve(string key) => 2 * _residents.Count(r => r.Value.Town == key && IsGuard(r.Key));

    /// <summary>
    /// The serial part of the guards' first aid: the kit job and kit fetches, then sending guards to patients.
    /// </summary>
    void StepMedics(World world)
    {
        if (_residents.Count == 0) return;
        var live = new Dictionary<string, PlayerTown>(StringComparer.Ordinal);
        foreach (var (town, state) in LiveTowns(world)) live[state.Key] = town;
        StepKits(world, live);
        foreach (var (key, town) in live) SendMedics(world, key, town);
    }

    /// <summary>
    /// Gives every guard the kit job and takes it off residents who are not guards; plans a kit fetch for an awake guard in town who carries no
    /// kit when a town-stock box holds one; on arrival at the box the guard takes one kit.
    /// </summary>
    void StepKits(World world, Dictionary<string, PlayerTown> live)
    {
        var table = world.Characters;
        foreach (var (id, resident) in _residents)
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || !table.Next[slot].Alive
                || table.Cold(slot) is not { IsPlayer: true, Medical.Dead: false } cold) continue;

            if (!IsGuard(id))
            {
                DropKitJob(world, c, slot, cold);
                continue;
            }

            if (!cold.Jobs.Any(j => j.Type == (TaskType)KitRow))
            {
                cold.Jobs.Add(new PermaJob((TaskType)KitRow, ""));
                cold.Orders |= CharacterOrders.Jobs;
                DecisionSystem.Request(cold);
            }
            if (cold.Inventory is not { } inv || !live.TryGetValue(resident.Town, out var town)) continue;

            if (!_kitTrips.TryGetValue(id, out var boxId))
            {
                if (inv.Items.Any(IsFirstAidKit) || cold.Medical is not { Incapacitated: false } || cold.InCombat
                    || table.Next[slot].Task == (byte)CharacterTask.Sleep || _pay.TryGetValue(id, out var pay) && pay.Unpaid > 0) continue;
                if (!InTown(table.Next[slot].Position, town)) continue;
                var box = TownStock(world, town.Number).FirstOrDefault(b => b.Stock.Items.Any(IsFirstAidKit));
                if (box.Site is null) continue;
                _kitTrips[id] = box.Site.Id;
                Log($"{cold.Name}: carries no first aid kit; fetching one from town stock {box.Site.Id}");
                DecisionSystem.Request(world, slot);
                continue;
            }

            if (!_kitWalking.Contains(id) || cold.Running is not { Row: (TaskType)KitRow }) continue;
            if (world.Buildings.Find(boxId) is not { } site || world.Machines.Find(boxId)?.Storage is not { } stock)
            {
                EndKitTrip(world, slot, id);
                continue;
            }
            var here = table.Next[slot].Position;
            if (Vector2.Distance(new(here.X, here.Z), new(site.Position.X, site.Position.Z)) > Reach) continue;
            if (stock.Items.FirstOrDefault(IsFirstAidKit) is { } kit)
            {
                var one = kit.Quantity == 1 ? kit : kit.WithQuantity(1);
                if (inv.HasRoomIn(Carried, one))
                {
                    if (one == kit) stock.Remove(kit);
                    else kit.Quantity -= 1;
                    inv.AddTo(Carried, one);
                    world.Machines.Touch();
                    Log($"{cold.Name}: took a {one.Name} from town stock {boxId}");
                }
                else Log($"{cold.Name}: no room for a first aid kit");
            }
            EndKitTrip(world, slot, id);
        }

        foreach (var id in _kitTrips.Keys.Where(k => !_residents.ContainsKey(k) || !IsGuard(k)).ToList())
        {
            _kitTrips.Remove(id);
            _kitWalking.Remove(id);
        }
    }

    /// <summary>
    /// Sends the town's free guards with a kit to its people who need first aid: patients in lasting-id order, each the nearest free guard. A guard
    /// is free when awake, not fighting, carrying a first aid kit, inside the town, holding no order of the player's, not fetching a kit and paid.
    /// A patient is anyone of the player's faction inside the town, alive, not fighting, whom the game says needs first aid, no guard already
    /// treating them, and not waiting out a failed start. A guard may be their own patient.
    /// </summary>
    void SendMedics(World world, string key, PlayerTown town)
    {
        var table = world.Characters;

        // Guards sent before: free again once their treatment ended or never started.
        foreach (var (guard, job) in _treating.ToList())
        {
            var g = world.LastingIds.Find(new LastingId(guard));
            var p = world.LastingIds.Find(new LastingId(job.Patient));
            int gs = -1;
            bool alive = !g.IsNone && table.TryResolveNext(g, out gs);
            bool treating = alive && table.Next[gs].Task == (byte)CharacterTask.Treat;
            bool onWay = alive && OnTreatWay(world, gs);
            bool needs = !p.IsNone && table.TryResolveNext(p, out int ps) && table.Next[ps].Alive && table.Cold(ps) is { } pc
                && FirstAidSystem.NeedsFirstAid(world, pc);
            long age = world.Tick - job.Sent;
            if (treating && needs) continue;
            if (!treating && needs && (age < TreatStartTicks || onWay && age < TreatWalkTicks)) continue;
            if (needs) _treatRetry[job.Patient] = world.Tick + TreatRetryTicks;

            // Orders still waiting (a sleeper that never got up, a walk that got nowhere) would hold the guard for good: clear them.
            if (onWay) world.Commands.Enqueue(new StopCommand([g]) { Tick = world.Tick + 1 });
            _treating.Remove(guard);
        }
        foreach (var (patient, until) in _treatRetry.ToList())
            if (until <= world.Tick) _treatRetry.Remove(patient);

        var free = new List<(ulong Id, CharacterId C, Vector3 At)>();
        foreach (var (id, resident) in _residents)
        {
            if (resident.Town != key || !IsGuard(id) || _treating.ContainsKey(id) || _kitTrips.ContainsKey(id)) continue;
            if (_pay.TryGetValue(id, out var pay) && pay.Unpaid > 0) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || !table.Next[slot].Alive
                || table.Cold(slot) is not { IsPlayer: true, Medical: { Dead: false, Incapacitated: false } } cold) continue;
            if (cold.InCombat || cold.Inventory is not { } inv || !inv.Items.Any(IsFirstAidKit) || PlayerOrders.Holds(world, slot)) continue;
            var at = table.Next[slot].Position;
            if (InTown(at, town)) free.Add((id, c, at));
        }
        if (free.Count == 0) return;

        var busy = new HashSet<ulong>(_treating.Values.Select(v => v.Patient));
        var patients = new List<(ulong Id, CharacterId C, Vector3 At, string Name)>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { IsPlayer: true, LastingId.IsNone: false } pc || pc.InCombat) continue;
            ulong pid = pc.LastingId.Value;
            if (busy.Contains(pid) || _treatRetry.ContainsKey(pid) || !InTown(table.Next[s].Position, town)) continue;
            if (!FirstAidSystem.NeedsFirstAid(world, pc)) continue;
            patients.Add((pid, table.IdOf(s), table.Next[s].Position, pc.Name));
        }
        patients.Sort((a, b) => a.Id.CompareTo(b.Id));

        foreach (var patient in patients)
        {
            if (free.Count == 0) break;
            int best = 0;
            for (int i = 1; i < free.Count; i++)
                if (Vector3.DistanceSquared(free[i].At, patient.At) < Vector3.DistanceSquared(free[best].At, patient.At)) best = i;
            var guard = free[best];
            var check = SendToPatient(world, guard.C, patient.C, patient.At);
            Log($"{_residents[guard.Id].Name}: sent to bandage {patient.Name}: {check.Verdict}{(check.Why is { } why ? $" ({why})" : "")}");
            if (check.Verdict == OrderVerdict.Run)
            {
                free.RemoveAt(best);
                _treating[guard.Id] = (patient.Id, world.Tick);
                continue;
            }

            // Refused: the patient waits a while, and a blocked order is cleared so it does not hold the guard.
            _treatRetry[patient.Id] = world.Tick + TreatRetryTicks;
            if (check.Verdict == OrderVerdict.Blocked) world.Commands.Enqueue(new StopCommand([guard.C]) { Tick = world.Tick + 1 });
        }
    }

    /// <summary>
    /// Gives a guard the game's first aid order on a patient. A sleeping guard is first given the game's move order to the patient, which gets a
    /// sleeper up (a first aid order alone waits in a sleeper's queue), with the first aid order queued after it.
    /// </summary>
    static OrderCheck SendToPatient(World world, CharacterId guard, CharacterId patient, Vector3 at)
    {
        if (!world.Characters.TryResolveNext(guard, out int slot)) return OrderCheck.Gone;
        if (world.Characters.Next[slot].Task != (byte)CharacterTask.Sleep) return world.GiveOrder(guard, TaskType.FirstAidOrder, target: patient);

        var move = world.GiveOrder(guard, TaskType.MoveCusOrdered, point: at);
        if (move.Verdict != OrderVerdict.Run) return move;
        return world.GiveOrder(guard, TaskType.FirstAidOrder, target: patient, queued: true);
    }

    /// <summary>
    /// Whether the guard still holds the orders it was sent with (a move or a first aid order at the front of its queue).
    /// </summary>
    static bool OnTreatWay(World world, int slot) =>
        PlayerOrders.Holds(world, slot)
            && world.Characters.Cold(slot)!.OrderQueue.Front is { } front && (front.IsMove || front.Row == TaskType.FirstAidOrder);

    /// <summary>
    /// Whether a point lies inside a town's radius.
    /// </summary>
    static bool InTown(Vector3 at, PlayerTown town) =>
        Vector2.DistanceSquared(new(at.X, at.Z), Centre(town)) <= town.Radius * town.Radius;

    /// <summary>
    /// Ends a guard's kit trip and its walk, and asks the decision step for what comes next.
    /// </summary>
    void EndKitTrip(World world, int slot, ulong id)
    {
        _kitTrips.Remove(id);
        _kitWalking.Remove(id);
        if (world.Characters.Cold(slot) is { Running.Row: (TaskType)KitRow }) TaskStart.End(world, slot, TaskEnd.Done);
        DecisionSystem.Request(world, slot);
    }

    /// <summary>
    /// Takes the kit job off a resident who is no longer a guard.
    /// </summary>
    void DropKitJob(World world, CharacterId c, int slot, CharacterCold cold)
    {
        if (cold.Running is { Row: (TaskType)KitRow }) TaskStart.End(world, slot, TaskEnd.Stopped);
        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
            if (cold.Jobs[i].Type == (TaskType)KitRow) _production?.Inputs(world, [new RemoveJob(c, i) { Tick = world.Tick }]);
    }

    /// <summary>
    /// Starts a guard's walk to the town-stock box of its kit trip.
    /// </summary>
    void StartKitTrip(World world, int slot, int k)
    {
        if (world.Characters.Cold(slot) is not { LastingId.IsNone: false } cold || !_kitTrips.TryGetValue(cold.LastingId.Value, out var box)
            || world.Buildings.Find(box) is not { } site) return;
        var result = TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, (TaskType)KitRow, TaskSource.Job)
        {
            Job = k, Point = site.Position, Mode = (byte)SpeedMode.Walk,
        });
        _kitWalking.Add(cold.LastingId.Value);
        Log($"{cold.Name}: kit walk to {site.Name}: {result}");
    }

    /// <summary>
    /// The guard's kit job for the decision step: it runs while the guard has a kit trip; otherwise the guard keeps its duty.
    /// </summary>
    sealed class KitJob(FiefdomSystem owner) : IJobSource
    {
        /// <summary>
        /// Whether the guard's kit job <paramref name="k"/> can run now (a pure read).
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var cold = world.Characters.Cold(slot);
            if (cold is null || cold.LastingId.IsNone) return DecisionCheck.No("no guard");
            if (cold.Running is { Row: (TaskType)KitRow, Source: TaskSource.Job } run && run.Job == k
                && world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo) return DecisionCheck.Runs;
            return owner._kitTrips.ContainsKey(cold.LastingId.Value) ? DecisionCheck.Run : DecisionCheck.No("carries a kit, or town stock has none");
        }

        /// <summary>
        /// Starts the guard's walk to the box.
        /// </summary>
        public void Start(World world, int slot, int k) => owner.StartKitTrip(world, slot, k);
    }
}
