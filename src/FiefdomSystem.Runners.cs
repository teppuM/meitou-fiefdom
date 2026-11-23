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

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// The task row of the food runner's job (claimed with <c>AddJobType</c>), which its walks also run as.
    /// </summary>
    public const int RunnerRow = 7312;

    /// <summary>
    /// The food runners' line key starts with this.
    /// </summary>
    public const string RunnerLine = "runners|";

    /// <summary>
    /// Someone at work below this hunger, carrying no food, is brought a meal (above the eating break's line, so a fed worker never has to leave).
    /// </summary>
    const float BringFoodBelow = 2.2f;

    /// <summary>
    /// The most meals a runner carries on one round.
    /// </summary>
    const int MealsPerRound = 3;

    /// <summary>
    /// The food runners, by lasting id, with their town's key (saved).
    /// </summary>
    readonly SortedDictionary<ulong, string> _runners = [];

    /// <summary>
    /// A runner's round: the town-stock box to fetch meals from (null once they carry them), and the people to bring them to, in order.
    /// Not saved: after a load the job plans afresh.
    /// </summary>
    sealed class Round
    {
        public string? Box;
        public List<ulong> To = [];
        public bool Walking;
    }

    /// <summary>
    /// Each runner's round now, by lasting id.
    /// </summary>
    readonly Dictionary<ulong, Round> _rounds = [];

    /// <summary>
    /// How many food runners a town may have: one for every five people at work or on guard, and at least one.
    /// </summary>
    int RunnerPlaces(string key) => Math.Max(1, (_holders.Values.Count(h => h.Town == key) + _residents.Count(r => r.Value.Town == key && IsGuard(r.Key))) / 5 + 1);

    /// <summary>
    /// Applies a food runner line command; false when it is not one.
    /// </summary>
    bool ApplyRunners(World world, SettlerCommand command)
    {
        switch (command)
        {
            case AddWorker add when add.Line.StartsWith(RunnerLine, StringComparison.Ordinal):
                if (world.Buildings.FindTown(add.Town) is not { } town || !IsFiefdom(world, town) || KeyOf(town) is not { } key) return true;
                ulong id = add.Character.IsNone ? IdleResidents(world, key).FirstOrDefault() : ResidentLasting(world, add.Character);
                if (id == 0 || _residents[id].Town != key || IsGuard(id) || _runners.Count(r => r.Value == key) >= RunnerPlaces(key)) return true;
                if (_holders.Remove(id, out var was))
                {
                    var c = world.LastingIds.Find(new LastingId(id));
                    if (!c.IsNone && world.Characters.TryResolveNext(c, out int slot) && world.Characters.Cold(slot) is { } cold) TakeJob(world, c, cold, was.Site);
                }
                _runners[id] = key;
                TownOf(key).Tell(world.GameHours, $"{_residents[id].Name} now carries meals to the town's hungry people");
                Log($"{town.Name}: {_residents[id].Name} is a food runner");
                _dirty = true;
                return true;
            case RemoveWorker remove when remove.Line.StartsWith(RunnerLine, StringComparison.Ordinal):
                if (world.Buildings.FindTown(remove.Town) is not { } rt || KeyOf(rt) is not { } rkey) return true;
                var last = _runners.Where(r => r.Value == rkey).Select(r => r.Key).DefaultIfEmpty(0UL).Last();
                if (last != 0) StopRunner(world, last);
                return true;
            case ClearPost clear when ResidentLasting(world, clear.Character) is var cid and not 0 && _runners.ContainsKey(cid):
                StopRunner(world, cid);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Takes a runner off the line and their job off their list.
    /// </summary>
    void StopRunner(World world, ulong id)
    {
        if (!_runners.Remove(id, out var key)) return;
        _rounds.Remove(id);
        var c = world.LastingIds.Find(new LastingId(id));
        if (!c.IsNone && world.Characters.TryResolveNext(c, out int slot) && world.Characters.Cold(slot) is { } cold) DropRunnerJob(world, c, slot, cold);
        if (_residents.TryGetValue(id, out var r)) TownOf(key).Tell(world.GameHours, $"{r.Name} no longer carries meals");
        _dirty = true;
    }

    /// <summary>
    /// Takes the runner's job off a character's list, ending its walk if it is on one.
    /// </summary>
    void DropRunnerJob(World world, CharacterId c, int slot, CharacterCold cold)
    {
        if (cold.Running is { Row: (TaskType)RunnerRow }) TaskStart.End(world, slot, TaskEnd.Stopped);
        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
            if (cold.Jobs[i].Type == (TaskType)RunnerRow) _production?.Inputs(world, [new Meitou.Simulation.Production.RemoveJob(c, i) { Tick = world.Tick }]);
    }

    /// <summary>
    /// The people of a town to bring a meal to now: its residents and guards (not an unpaid worker), in town, alive and awake, below
    /// <see cref="BringFoodBelow"/> and carrying no food, nobody already being brought one; in lasting-id order.
    /// </summary>
    List<ulong> NeedMeals(World world, PlayerTown town, string key, HashSet<ulong> served)
    {
        var list = new List<ulong>();
        var table = world.Characters;
        var centre = Centre(town);
        float r2 = town.Radius * town.Radius;
        foreach (var (id, r) in _residents)
        {
            if (r.Town != key || served.Contains(id)) continue;
            // Everyone of the town is served, at work or not (the runner is the town's food carrier, not only the workers'); a worker who has
            // stopped for want of pay is left to fend for themselves.
            if (_pay.GetValueOrDefault(id)?.Unpaid > 0) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || !table.Next[slot].Alive) continue;
            if (table.Cold(slot) is not { Medical: { Dead: false, Incapacitated: false } medical, Inventory: { } inv } || medical.Hunger >= BringFoodBelow) continue;
            if (inv.Items.Any(i => i.IsFood)) continue;
            var at = table.Next[slot].Position;
            if (Vector2.DistanceSquared(new(at.X, at.Z), centre) > r2) continue;
            list.Add(id);
        }
        return list;
    }

    /// <summary>
    /// The serial part of the runners: gives each runner the job (they keep it day and night), plans a round for a runner with none (the box
    /// with food, and up to <see cref="MealsPerRound"/> hungry people), and acts on arrival: at the box they take a meal for each person, at a
    /// person they hand one over. A runner who loses their town or their life leaves the line.
    /// </summary>
    void StepRunners(World world)
    {
        if (_runners.Count == 0) return;
        var table = world.Characters;
        var live = new Dictionary<string, PlayerTown>(StringComparer.Ordinal);
        foreach (var (town, state) in LiveTowns(world)) live[state.Key] = town;

        var served = new HashSet<ulong>(_rounds.Values.SelectMany(r => r.To));
        foreach (var (id, key) in _runners.ToList())
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true, Medical.Dead: false } cold
                || !_residents.ContainsKey(id) || !live.TryGetValue(key, out var town))
            {
                if (!c.IsNone && table.TryResolveNext(c, out int s2) && table.Cold(s2) is { } gone) DropRunnerJob(world, c, s2, gone);
                _runners.Remove(id);
                _rounds.Remove(id);
                continue;
            }
            if (!cold.Jobs.Any(j => j.Type == (TaskType)RunnerRow))
            {
                cold.Jobs.Add(new PermaJob((TaskType)RunnerRow, ""));
                cold.Orders |= CharacterOrders.Jobs;
                DecisionSystem.Request(cold);
            }
            if (cold.Inventory is not { } inv) continue;

            if (!_rounds.TryGetValue(id, out var round))
            {
                if (PayOf(id).Unpaid > 0) continue;
                var hungry = NeedMeals(world, town, key, served);
                if (hungry.Count == 0) continue;
                round = new Round { To = [.. hungry.Take(MealsPerRound)] };
                if (CarriedFood(id, inv) < round.To.Count)
                {
                    var box = TownStock(world, town.Number).FirstOrDefault(b => b.Stock.Items.Any(i => i.IsFood));
                    if (box.Site is null)
                    {
                        if (CarriedFood(id, inv) == 0) continue;
                    }
                    else round.Box = box.Site.Id;
                }
                _rounds[id] = round;
                served.UnionWith(round.To);
                Log($"{cold.Name}: food round to {string.Join(", ", round.To.Select(t => _residents[t].Name))}{(round.Box is { } b ? $" via {b}" : "")}");
                DecisionSystem.Request(world, slot);
                continue;
            }

            if (!round.Walking || cold.Running is not { Row: (TaskType)RunnerRow }) continue;
            var at = table.Next[slot].Position;
            if (round.Box is { } boxId)
            {
                if (world.Buildings.Find(boxId) is not { } site || world.Machines.Find(boxId)?.Storage is not { } stock)
                {
                    _rounds.Remove(id);
                    continue;
                }
                if (Vector2.Distance(new(at.X, at.Z), new(site.Position.X, site.Position.Z)) > Reach) continue;
                int want = round.To.Count - CarriedFood(id, inv), taken = 0;
                // The most filling meals first (an item's full charges are its nutrition, as the eating rule scores it), so cooked food goes out
                // before raw produce rather than whatever the box lists first.
                var meals = stock.Items.Where(i => i.IsFood).OrderByDescending(i => i.MaxCharges).ToList();
                foreach (var item in meals)
                {
                    if (taken >= want) break;
                    if (!inv.HasRoomIn(Carried, item)) continue;

                    int units = ItemMoves.Move(stock, item, want - taken, inv, Carried);
                    Carry(id, item.Record, units);
                    taken += units;
                }
                if (taken > 0) world.Machines.Touch();
                Log($"{cold.Name}: took {taken} meal(s) from town stock {boxId}; carrying {Contents(inv, Carried)}");
                round.Box = null;
                if (CarriedFood(id, inv) == 0) _rounds.Remove(id);
            }
            else
            {
                ulong to = round.To[0];
                var tc = world.LastingIds.Find(new LastingId(to));
                if (tc.IsNone || !table.TryResolveNext(tc, out int ts) || table.Cold(ts) is not { Inventory: { } theirs } them)
                {
                    round.To.RemoveAt(0);
                }
                else
                {
                    var there = table.Next[ts].Position;
                    if (Vector2.Distance(new(at.X, at.Z), new(there.X, there.Z)) > Reach)
                    {
                        // They moved on: walk to where they are now.
                        if (table.Next[slot].Task != (byte)CharacterTask.GoTo)
                        {
                            round.Walking = false;
                            DecisionSystem.Request(world, slot);
                        }
                        continue;
                    }
                    if (inv.Items.FirstOrDefault(i => i.Section == Carried && i.IsFood && CarriedUnits(id, i.Record) > 0) is { } meal)
                    {
                        var one = meal.Quantity == 1 ? meal : meal.WithQuantity(1);
                        if (theirs.HasRoomIn(Carried, one))
                        {
                            if (one == meal) inv.Remove(meal);
                            else meal.Quantity -= 1;
                            theirs.AddTo(Carried, one);
                            Carry(id, one.Record, -1);
                            Log($"{cold.Name}: brought {them.Name} a {one.Name} (hunger {them.Medical?.Hunger ?? 0:0.00})");
                        }
                        else Log($"{cold.Name}: {them.Name} has no room for a meal");
                    }
                    round.To.RemoveAt(0);
                }
                if (round.To.Count == 0 || CarriedFood(id, inv) == 0) _rounds.Remove(id);
            }
            round.Walking = false;
            TaskStart.End(world, slot, TaskEnd.Done);
            DecisionSystem.Request(world, slot);
        }
    }

    /// <summary>
    /// At a day change: every food runner is paid two shifts' wages (they work day and night) from the player's money into their home's purse,
    /// in lasting-id order while the money lasts. One left unpaid stops carrying meals until paid; three day changes unpaid and they leave.
    /// </summary>
    void PayRunners(World world)
    {
        long wage = FiefdomSettings.Current.WagePerShift * 2L;
        var table = world.Characters;
        foreach (var (id, key) in _runners.ToList())
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || wage <= 0) continue;
            var pay = PayOf(id);
            var bill = _lastBill.GetValueOrDefault(key);
            string name = _residents.TryGetValue(id, out var r) ? r.Name : cold.Name;
            if (world.Player.Money >= wage)
            {
                world.Player.Money -= wage;
                if (world.Squads.Find(cold.SquadId) is { OwnPurse: true } squad) squad.Money += wage;
                if (pay.Unpaid > 0) TownOf(key).Tell(world.GameHours, $"{name} was paid and carries meals again");
                pay.Unpaid = 0;
                _lastBill[key] = (bill.Paid + wage, bill.Unpaid);
                continue;
            }
            pay.Unpaid++;
            _lastBill[key] = (bill.Paid, bill.Unpaid + 1);
            if (pay.Unpaid >= 3)
            {
                TownOf(key).Tell(world.GameHours, $"{name} left the town: unpaid three days running");
                StopRunner(world, id);
                _pay.Remove(id);
                Evict(world, c);
            }
            else TownOf(key).Tell(world.GameHours, $"{name} went unpaid and stopped carrying meals");
        }
    }

    /// <summary>
    /// The units of town food a runner carries.
    /// </summary>
    int CarriedFood(ulong id, Inventory inv) =>
        inv.Items.Where(i => i.Section == Carried && i.IsFood).Sum(i => Math.Min(i.Quantity, CarriedUnits(id, i.Record)));

    /// <summary>
    /// Starts a runner's next walk: to the box while the round still fetches, else to the first person on it.
    /// </summary>
    void StartRunner(World world, int slot, int k)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { LastingId.IsNone: false } cold || !_rounds.TryGetValue(cold.LastingId.Value, out var round)) return;
        Vector3 point;
        string what;
        if (round.Box is { } box && world.Buildings.Find(box) is { } site)
        {
            point = site.Position;
            what = site.Name;
        }
        else if (round.To.Count > 0 && world.LastingIds.Find(new LastingId(round.To[0])) is { IsNone: false } to && table.TryResolveNext(to, out int ts))
        {
            point = table.Next[ts].Position;
            what = table.Cold(ts)?.Name ?? "someone";
        }
        else return;

        var result = TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, (TaskType)RunnerRow, TaskSource.Job)
        {
            Job = k, Point = point, Mode = (byte)Meitou.Simulation.Movement.SpeedMode.Walk,
        });
        round.Walking = true;
        Log($"{cold.Name}: runner walk to {what}: {result}");
    }

    /// <summary>
    /// The food runner's job for the decision step: it runs while the runner has a round; otherwise the runner keeps the town's own day.
    /// </summary>
    sealed class RunnerJob(FiefdomSystem owner) : IJobSource
    {
        /// <summary>
        /// Whether the runner's job <paramref name="k"/> can run now (a pure read).
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var cold = world.Characters.Cold(slot);
            if (cold is null || cold.LastingId.IsNone) return DecisionCheck.No("no runner");
            if (cold.Running is { Row: (TaskType)RunnerRow, Source: TaskSource.Job } run && run.Job == k
                && world.Characters.Next[slot].Task == (byte)CharacterTask.GoTo) return DecisionCheck.Runs;
            return owner._rounds.ContainsKey(cold.LastingId.Value) ? DecisionCheck.Run : DecisionCheck.No("nobody at work is hungry");
        }

        /// <summary>
        /// Starts the runner's next walk.
        /// </summary>
        public void Start(World world, int slot, int k) => owner.StartRunner(world, slot, k);
    }
}
