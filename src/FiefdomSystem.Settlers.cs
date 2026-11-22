using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Weather;

namespace Fiefdom;

/// <summary>
/// A Fiefdom command about settlers, a town's settings or its residents.
/// </summary>
public abstract record SettlerCommand : SimCommand;

/// <summary>
/// Opens a town to settlers or closes it.
/// </summary>
public sealed record SetOpen(int Town, bool Open) : SettlerCommand;

/// <summary>
/// Sets a town's population cap.
/// </summary>
public sealed record SetCap(int Town, int Cap) : SettlerCommand;

/// <summary>
/// Sets how fast settlers come to a town.
/// </summary>
public sealed record SetPace(int Town, ArrivalPace Pace) : SettlerCommand;

/// <summary>
/// Lets settlers the town's weather would hurt come, or keeps them away.
/// </summary>
public sealed record SetAllowHarmed(int Town, bool Allow) : SettlerCommand;

/// <summary>
/// Keeps a race out of a town, or lets it in again.
/// </summary>
public sealed record SetRaceBlocked(int Town, string Race, bool Blocked) : SettlerCommand;

/// <summary>
/// Makes one of the player's characters a town's steward; <see cref="CharacterId.None"/> leaves the town without one.
/// </summary>
public sealed record AppointSteward(int Town, CharacterId Character) : SettlerCommand;

/// <summary>
/// The player's answer to a group waiting at a town.
/// </summary>
public sealed record AnswerGroup(int Serial, bool Accept) : SettlerCommand;

/// <summary>
/// Sends a resident away from their town.
/// </summary>
public sealed record EvictResident(CharacterId Character) : SettlerCommand;

/// <summary>
/// Testing: the town's next group is due now (an Open town with a free bed still decides who comes).
/// </summary>
public sealed record BringSettlers(int Town) : SettlerCommand;

/// <summary>
/// Testing: plans <paramref name="Count"/> beds of record <paramref name="Bed"/> inside each Home of the town (built as any site is), since the
/// test channel's build command places only outdoors.
/// </summary>
public sealed record PlanBeds(int Town, string Bed, int Count) : SettlerCommand;

/// <summary>
/// For testing: plans two of the game's Bed in every guardhouse of the town, at its middle and 35 toward its front, for the selection to build.
/// </summary>
public sealed record PlanGuardBeds(int Town) : SettlerCommand;

/// <summary>
/// For testing: the town's next visitors are due now (they come once the town is open to the public).
/// </summary>
public sealed record BringVisitors(int Town) : SettlerCommand;

/// <summary>
/// For bug reports: logs what every resident and guard is doing (squad role, goal, task), once. Changes nothing in the game.
/// </summary>
public sealed record LogMinds(int Town) : SettlerCommand;

/// <summary>
/// One home of a town as the settlers see it: its building, its beds and the squad living in it.
/// </summary>
sealed class Home
{
    public required BuildSite Site { get; init; }
    public required int Beds { get; init; }
    public Squad? Squad { get; set; }
    public int Members { get; set; }
    public int Free => Math.Max(0, Beds - Members);
}

public sealed partial class FiefdomSystem
{
    #region Settings

    /// <summary>
    /// How often, in ticks, the settlers are stepped (a second of game time).
    /// </summary>
    const int SettlerEvery = 30;

    /// <summary>
    /// The AI package residents run when Fiefdom's own data is not loaded: the game's town civilians' (they stay home, sit about, go shopping and
    /// sleep at night). Read from the merged data, so a mod that changes it changes the residents too.
    /// </summary>
    public const string CivilianPackage = "3107-gamedata.base";

    /// <summary>
    /// Fiefdom's own residents' package (in Fiefdom.mod): the civilians' day with wandering the town by day and sitting about in its public
    /// buildings in the evening.
    /// </summary>
    public const string TownLifePackage = "2-Fiefdom.mod";

    /// <summary>
    /// The package residents run: Fiefdom's own when its data is loaded, else the civilians'.
    /// </summary>
    string ResidentPackage => _data.Db.Find(TownLifePackage) is not null ? TownLifePackage : CivilianPackage;

    /// <summary>
    /// A home squad's tag starts with this, then the home building's id.
    /// </summary>
    const string HomeTag = "home:";

    /// <summary>
    /// The pull score's pace: one group every this many game hours at a pull of 100, slower as the pull falls, none below
    /// <see cref="NoArrivalsBelow"/>.
    /// </summary>
    const double HoursAtFullPull = 72;

    /// <summary>
    /// No settlers come while the pull is below this.
    /// </summary>
    const int NoArrivalsBelow = 20;

    /// <summary>
    /// A group that has not reached the town after this many game hours gives up.
    /// </summary>
    const double TravelHours = 72;

    /// <summary>
    /// A group waits this many game hours at the town for the player's answer.
    /// </summary>
    const double WaitHours = 24;

    /// <summary>
    /// The pull score counts residents killed within this many game hours.
    /// </summary>
    const double KillingMemory = 72;

    /// <summary>
    /// The most walks in a row that bring a group's leader no nearer the town before the group gives up (or counts as arrived when it is inside
    /// the town anyway).
    /// </summary>
    const int MaxWalks = 30;

    /// <summary>
    /// A walk that brings the leader at least this much nearer counts as progress.
    /// </summary>
    const float Progress = 100;

    /// <summary>
    /// Groups are made this far beyond the town's edge, out of the loaded ground as a rule, so they come in by the roads as the game's own
    /// wanderers do (one made on loaded ground can stand on a height with no way down).
    /// </summary>
    const float SpawnNear = 7000, SpawnFar = 10000;

    /// <summary>
    /// Where a group brought by a testing row is made instead: just beyond the town's edge, so a test does not wait for a walk of kilometres.
    /// </summary>
    const float HurryNear = 1200, HurryFar = 2000;

    /// <summary>
    /// The towns whose next group or visitors a testing row asked for now (by key, not saved): that group is made close by.
    /// </summary>
    readonly HashSet<string> _hurry = new(StringComparer.Ordinal);

    /// <summary>
    /// The mod's random purpose (set when the mod starts).
    /// </summary>
    public static RngPurpose RollPurpose { get; set; }

    /// <summary>
    /// Each group's platoon id in this world, by serial (found again by tag after a load).
    /// </summary>
    readonly Dictionary<int, int> _platoons = [];

    /// <summary>
    /// Each group's platoon state last written to the log (for the log only: not saved or hashed).
    /// </summary>
    readonly Dictionary<int, PlatoonState> _seenStates = [];

    /// <summary>
    /// The leaders the mod ordered to walk into a town (a move order, which holds them until it ends; not saved: after a load the order is
    /// given again).
    /// </summary>
    readonly HashSet<CharacterId> _walking = [];

    /// <summary>
    /// Each group leader's nearest distance to the town so far, for the walks' progress (not saved: after a load the count starts again).
    /// </summary>
    readonly Dictionary<int, float> _lastDistance = [];

    /// <summary>
    /// Whether the groups' platoons must be found again by their tags (after a load).
    /// </summary>
    bool _reattach = true;

    /// <summary>
    /// The game day the residents were last checked (not saved: checked again after a load).
    /// </summary>
    int _checkedDay = -1;

    /// <summary>
    /// Whether the missing resident package has been logged.
    /// </summary>
    static bool s_packageLogged;

    #endregion

    #region Commands

    /// <summary>
    /// Applies a settler command after checking it against the world.
    /// </summary>
    void Apply(World world, SettlerCommand command)
    {
        double now = world.GameHours;
        switch (command)
        {
            case AnswerGroup answer:
                Answer(world, answer);
                break;
            case EvictResident evict:
                Evict(world, evict.Character);
                break;
            case SetSells sells:
                Apply(world, sells);
                break;
            default:
                if (ApplyGuard(world, command) || ApplyRunners(world, command) || ApplyPost(world, command) || ApplyLine(world, command)) break;
                if (TownCommandTarget(world, command) is not { } target) return;
                ApplyToTown(world, target.Town, target.State, command, now);
                break;
        }
        _dirty = true;
    }

    /// <summary>
    /// The Fiefdom town a town command names, with its state; null when it is not one of the player's Fiefdom towns.
    /// </summary>
    (PlayerTown Town, TownState State)? TownCommandTarget(World world, SettlerCommand command)
    {
        int number = command switch
        {
            SetOpen c => c.Town,
            SetCap c => c.Town,
            SetPace c => c.Town,
            SetAllowHarmed c => c.Town,
            SetRaceBlocked c => c.Town,
            SetAttackOnSight a => a.Town,
            AppointSteward c => c.Town,
            BringSettlers c => c.Town,
            PlanBeds c => c.Town,
            PlanGuardBeds c => c.Town,
            BringVisitors c => c.Town,
            LogMinds c => c.Town,
            TakePosts c => c.Town,
            PlanFurniture c => c.Town,
            FillTownStock c => c.Town,
            _ => -1,
        };
        if (world.Buildings.FindTown(number) is not { } town || !IsFiefdom(world, town) || KeyOf(town) is not { } key) return null;
        return (town, TownOf(key));
    }

    /// <summary>
    /// Applies a command to one town's settings.
    /// </summary>
    void ApplyToTown(World world, PlayerTown town, TownState state, SettlerCommand command, double now)
    {
        switch (command)
        {
            case SetOpen open when open.Open != state.Open:
                state.Open = open.Open;
                state.NextArrival = double.NaN;
                state.Tell(now, open.Open ? "opened to settlers" : "closed to settlers");
                break;
            case SetCap cap:
                state.Cap = Math.Clamp(cap.Cap, 1, 200);
                break;
            case SetPace pace:
                state.Pace = pace.Pace;
                state.NextArrival = double.NaN;
                break;
            case SetAllowHarmed harmed:
                state.AllowHarmed = harmed.Allow;
                break;
            case SetRaceBlocked race when SettlerData.Races.ContainsKey(race.Race):
                if (race.Blocked) state.BlockedRaces.Add(race.Race);
                else state.BlockedRaces.Remove(race.Race);
                break;
            case AppointSteward appoint:
                Appoint(world, town, state, appoint.Character, now);
                break;
            case BringSettlers:
                ReplaceTravelling(world, state, visiting: false);
                state.NextArrival = now;
                _hurry.Add(state.Key);
                Log($"{town.Name}: next group due now, made close by (testing)");
                break;
            case TakePosts:
                TakeOpenPosts(world, town, state);
                break;
            case LogMinds:
                LogTownspeople(world);
                break;
            case BringVisitors:
                ReplaceTravelling(world, state, visiting: true);
                state.NextVisit = world.GameHours;
                _hurry.Add(state.Key);
                Log($"{town.Name}: visitors due now (testing){(town.Public ? "" : "; the town is not open to the public")}");
                break;
            case PlanGuardBeds when _construction is not null:
                var beds = new List<Meitou.Simulation.Construction.PlannedBuilding>();
                foreach (var house in Guardhouses(world, town))
                    foreach (var offset in new[] { Vector3.Zero, new Vector3(0, 0, -35) })
                        beds.Add(Plans.Make("3460-otto.mod", house.Site.Position + offset, Quaternion.Identity, 0, house.Site.Id));
                _construction.Inputs(world, [new Meitou.Simulation.Construction.PlaceBuildings(beds) { Tick = world.Tick }]);
                Log($"{town.Name}: planned {beds.Count} beds in its guardhouses (testing)");
                break;
            case SetAttackOnSight attack:
                Apply(world, town, state, attack);
                break;
            case PlanFurniture plan:
                PlanInHomes(world, town, plan);
                break;
            case FillTownStock fill:
                FillStock(world, town, fill);
                break;
            case PlanBeds plan when _construction is not null && _data.Db.Find(plan.Bed) is not null:
                var plans = new List<Meitou.Simulation.Construction.PlannedBuilding>();
                // Two beds a row either side of the middle, rows 60 apart: wider rows put the outer beds outside a small shack's walls, where
                // the builder takes the order and does nothing.
                // Homes only: guardhouses have their own row ("plan two beds in each guardhouse").
                foreach (var site in Homes(world, town).Select(h => h.Site))
                    for (int i = 0; i < Math.Clamp(plan.Count, 1, 8); i++)
                    {
                        var at = site.Position + new Vector3(-20 + 40 * (i % 2), 0, -30 + 60 * (i / 2));
                        plans.Add(Plans.Make(plan.Bed, at, Quaternion.Identity, 0, site.Id));
                    }
                _construction.Inputs(world, [new Meitou.Simulation.Construction.PlaceBuildings(plans) { Tick = world.Tick }]);
                Log($"{town.Name}: planned {plans.Count} beds in its homes (testing)");
                break;
        }
    }

    /// <summary>
    /// Makes a living character of the player's standing in the town its steward (no longer steward of any other town), or leaves the town without
    /// one.
    /// </summary>
    void Appoint(World world, PlayerTown town, TownState state, CharacterId character, double now)
    {
        if (character.IsNone)
        {
            if (state.Steward != 0) state.Tell(now, $"{NameOfLasting(world, state.Steward)} is no longer steward");
            state.Steward = 0;
            return;
        }

        var table = world.Characters;
        if (!table.TryResolveNext(character, out int slot) || !table.Next[slot].Alive || table.Cold(slot) is not { IsPlayer: true, DiedTick: < 0 } cold)
            return;
        var at = table.Next[slot].Position;
        if (Vector2.Distance(new Vector2(at.X, at.Z), Centre(town)) > town.Radius) return;

        ulong id = world.LastingIds.GetOrGive(character).Value;
        foreach (var other in _towns.Values)
            if (other.Steward == id) other.Steward = 0;
        state.Steward = id;
        int personality = PersonalityOf(world, slot, cold);
        state.Tell(now, $"{cold.Name} is now steward ({Steward.NameOf(personality)})");
    }

    /// <summary>
    /// The player's answer to a waiting group: accept (it moves in now, or once it is back in the world) or turn it away.
    /// </summary>
    void Answer(World world, AnswerGroup answer)
    {
        if (!_groups.TryGetValue(answer.Serial, out var group) || group.Stage != GroupStage.Waiting || group.Visiting) return;

        var state = TownOf(group.Town);
        if (!answer.Accept)
        {
            Release(world, group, state, "turned away by you");
            return;
        }
        group.Accepted = true;
        if (FindTown(world, group.Town) is { } town && PlatoonOf(world, group) is { State: PlatoonState.Loaded } p && world.Squads.Find(p.SquadId) is { } squad)
            Decide(world, town, state, group, p, squad);
    }

    /// <summary>
    /// Sends a resident away: they leave the player's faction as a dismissed character does.
    /// </summary>
    void Evict(World world, CharacterId character)
    {
        var table = world.Characters;
        if (_player is null || !table.TryResolveNext(character, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) return;
        if (cold.LastingId.IsNone || !_residents.TryGetValue(cold.LastingId.Value, out var resident)) return;

        _player.Inputs(world, [new DismissCommand(character) { Tick = world.Tick }]);
        if (cold.IsPlayer) return;

        _residents.Remove(cold.LastingId.Value);
        TownOf(resident.Town).Tell(world.GameHours, $"{cold.Name} was evicted");
    }

    #endregion

    #region The settler step

    /// <summary>
    /// One step of the settlers: find the groups again after a load, move each group on, bring new groups to open towns, and once a game day
    /// check the residents.
    /// </summary>
    void StepSettlers(World world)
    {
        if (_population is null) return;
        if (_reattach) Reattach(world);

        foreach (var group in _groups.Values.ToList()) StepGroup(world, group);
        foreach (var (town, state) in LiveTowns(world))
        {
            Arrivals(world, town, state);
            Visits(world, town, state);
        }
        PayOutsideIncome(world);
        StepPosts(world);
        StepMerchants(world);
        StepRunners(world);
        StepMedics(world);
        StepLife(world);
        if (world.Tick % (SettlerEvery * 30) == 0)
        {
            CheckHunger(world);
            KeepPackages(world);
        }

        int day = TownState.DayOf(world.GameHours);
        if (day != _checkedDay)
        {
            _checkedDay = day;
            CheckResidents(world);
            CheckGuards(world);
        }
    }

    /// <summary>
    /// Finds every group's platoon by its tag, drops groups whose platoon is gone, and holds and sends the rest again (holds are not saved).
    /// </summary>
    void Reattach(World world)
    {
        _reattach = false;
        _platoons.Clear();
        var byTag = new Dictionary<string, Platoon>(StringComparer.Ordinal);
        foreach (var p in world.Platoons.All)
            if (p.ModTag.StartsWith(ModId + ":group:", StringComparison.Ordinal)) byTag[p.ModTag] = p;

        foreach (var group in _groups.Values.ToList())
        {
            if (!byTag.TryGetValue(group.Tag, out var p))
            {
                _groups.Remove(group.Serial);
                Log($"group {group.Serial} not found after the load; dropped");
                continue;
            }
            _platoons[group.Serial] = p.Id;
            PopulationSystem.HoldPlatoon(world, p.Id, true);
            if (FindTown(world, group.Town) is { } town) PopulationSystem.SendPlatoon(world, p.Id, Centre(town));
        }
    }

    /// <summary>
    /// The group's platoon in this world, or null when it is gone.
    /// </summary>
    Platoon? PlatoonOf(World world, SettlerGroup group) =>
        _platoons.TryGetValue(group.Serial, out int id) && world.Platoons.Find(id) is { } p && p.ModTag == group.Tag ? p : null;

    /// <summary>
    /// A town's centre on the ground.
    /// </summary>
    static Vector2 Centre(PlayerTown town) => new(town.Position.X, town.Position.Z);

    /// <summary>
    /// Moves one group on: on its way, walks its leader in once it is in the world; at the town, the steward judges it, or it waits for the
    /// player's answer.
    /// </summary>
    void StepGroup(World world, SettlerGroup group)
    {
        if (group.Visiting)
        {
            StepVisit(world, group);
            return;
        }
        double now = world.GameHours;
        var state = TownOf(group.Town);
        if (FindTown(world, group.Town) is not { } town || !state.Open && !group.Accepted && group.Stage == GroupStage.Travelling)
        {
            Release(world, group, state, "turned back: the town is closed");
            return;
        }
        if (PlatoonOf(world, group) is not { Dead: false } p)
        {
            _groups.Remove(group.Serial);
            _platoons.Remove(group.Serial);
            state.NextArrival = double.NaN;
            state.Tell(now, "a group on its way never arrived");
            return;
        }

        var squad = p.State == PlatoonState.Loaded ? world.Squads.Find(p.SquadId) : null;
        if (p.State == PlatoonState.Loaded && squad is null && world.Tick % (SettlerEvery * 30) == 0)
            Log($"group {group.Serial}: platoon {p.Id} loaded but its squad {p.SquadId} is not found");
        if (!_seenStates.TryGetValue(group.Serial, out var seen) || seen != p.State)
        {
            _seenStates[group.Serial] = p.State;
            Log($"group {group.Serial}: platoon {p.Id} {p.State} at {p.Position.X:0} {p.Position.Y:0}, held {p.Held}, goal {p.ModGoal}");
        }
        if (group.Stage == GroupStage.Travelling)
        {
            if (now - group.Since > TravelHours)
            {
                Release(world, group, state, "gave up on the way");
                return;
            }
            if (squad is null || !Arrived(world, town, group, squad)) return;

            group.Stage = GroupStage.Waiting;
            group.ArrivedAt = now;
            if (StewardHere(world, town, state) is null && !group.Accepted)
                world.Post($"Settlers at {town.Name} ask to stay. Answer them in the Town window.");
        }

        if (squad is null)
        {
            if (!group.Accepted && now - group.ArrivedAt > WaitHours) Release(world, group, state, "waited a day for an answer and moved on");
            return;
        }
        Decide(world, town, state, group, p, squad);
        if (_groups.ContainsKey(group.Serial) && !group.Accepted && now - group.ArrivedAt > WaitHours)
            Release(world, group, state, "waited a day for an answer and moved on");
    }

    /// <summary>
    /// Whether the group's leader has reached the town (or, given <paramref name="to"/>, come within <paramref name="reach"/> of that point);
    /// walks it there when it is not walking already.
    /// </summary>
    bool Arrived(World world, PlayerTown town, SettlerGroup group, Squad squad, Vector2? to = null, float reach = -1)
    {
        if (LeaderSlot(world, squad) is not { } slot)
        {
            if (world.Tick % (SettlerEvery * 30) == 0) Log($"group {group.Serial}: no leader awake among {squad.Members.Count}");
            return false;
        }

        var centre = to ?? Centre(town);
        var at = world.Characters.Next[slot].Position;
        float d = Vector2.Distance(new Vector2(at.X, at.Z), centre);
        if (d <= (reach > 0 ? reach : Math.Min(800, town.Radius))) return true;
        if (OnOurWalk(world, slot)) return false;
        if (!_lastDistance.TryGetValue(group.Serial, out float last) || d < last - Progress)
        {
            _lastDistance[group.Serial] = d;
            group.Walks = 0;
        }
        if (group.Walks >= MaxWalks)
        {
            if (d <= town.Radius) return true;
            Release(world, group, TownOf(group.Town), "could not find a way to the town");
            return false;
        }

        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!FreeToWalk(cold))
        {
            if (world.Tick % (SettlerEvery * 30) == 0)
                Log($"group {group.Serial}: leader {cold.Name} on {(CharacterTask)table.Next[slot].Task}, not free to walk (fight {cold.InCombat}, fleeing {cold.Fleeing}, carried {!cold.CarriedBy.IsNone})");
            return false;
        }

        var id = table.IdOf(slot);
        var result = world.GiveOrder(id, TaskType.MoveCusOrdered, point: new Vector3(centre.X, at.Y, centre.Y));
        _walking.Add(id);
        group.Walks++;
        Log($"group {group.Serial}: walk {group.Walks} from {at.X:0} {at.Y:0} {at.Z:0}, {d:0} from {town.Name}: {result}");
        return false;
    }

    /// <summary>
    /// Whether a leader may be ordered to walk: not fighting, fleeing, caged, carried, carrying someone or down. An order would pull it out of
    /// a fight, so the mod waits for the fight to end.
    /// </summary>
    static bool FreeToWalk(CharacterCold cold) =>
        !cold.InCombat && !cold.Fleeing && !cold.IsImprisoned && cold.CarriedBy.IsNone && cold.Carrying.IsNone
            && cold.Medical is not { Incapacitated: true };

    /// <summary>
    /// The slot of a squad's leader, or of its first member standing, when one is alive and awake.
    /// </summary>
    static int? LeaderSlot(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members.Prepend(squad.Leader))
        {
            if (id.IsNone || !table.TryResolveNext(id, out int slot) || !table.Next[slot].Alive) continue;
            if (table.Cold(slot) is { Medical.Incapacitated: false, DiedTick: < 0 }) return slot;
        }
        return null;
    }

    /// <summary>
    /// Whether the character still walks on the move order the mod gave it.
    /// </summary>
    bool OnOurWalk(World world, int slot) =>
        _walking.Contains(world.Characters.IdOf(slot)) && PlayerOrders.Holds(world, slot)
            && world.Characters.Cold(slot)!.OrderQueue.Front is { IsMove: true };

    /// <summary>
    /// Stops the character's walk when it is the mod's: the game's stop, which drops its orders, the next tick.
    /// </summary>
    void StopWalk(World world, int slot)
    {
        bool ours = OnOurWalk(world, slot);
        var id = world.Characters.IdOf(slot);
        _walking.Remove(id);
        if (ours) world.Commands.Enqueue(new StopCommand([id]) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// Decides on a group at the town: the player's acceptance, else the steward's verdict; with neither it keeps waiting.
    /// </summary>
    void Decide(World world, PlayerTown town, TownState state, SettlerGroup group, Platoon p, Squad squad)
    {
        var members = Applying(world, p, squad);
        if (members.Count == 0)
        {
            Release(world, group, state, "had no one left to settle");
            return;
        }
        var applicants = Assess(world, town, state, p, squad, members);
        if (group.Accepted)
        {
            if (applicants.HardRefusal is { } hard) Release(world, group, state, $"could not move in: {hard}");
            else Admit(world, town, state, group, p, squad, members, "taken in by you", 0, null);
            return;
        }
        if (StewardHere(world, town, state) is not { } steward) return;

        var cold = world.Characters.Cold(steward)!;
        int personality = PersonalityOf(world, steward, cold);
        var verdict = Steward.Judge(personality, applicants, Rng.Hash(world.Seed, (ulong)group.Serial, RollPurpose, 0x57E7));
        world.Bark(world.Characters.IdOf(steward), verdict.Says);
        if (verdict.Admit) Admit(world, town, state, group, p, squad, members, $"{verdict.Reason}, said {cold.Name}", verdict.Bribe, steward);
        else
        {
            Release(world, group, state, $"turned away by {cold.Name}: {verdict.Reason}");
            world.Post($"{cold.Name} turned settlers away from {town.Name}: {verdict.Reason}.");
        }
    }

    /// <summary>
    /// The group's members who would move in: alive, of the group's own faction (not its slaves or prisoners), not caged.
    /// </summary>
    static List<CharacterId> Applying(World world, Platoon p, Squad squad)
    {
        var table = world.Characters;
        var list = new List<CharacterId>();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int slot) || !table.Next[slot].Alive) continue;
            if (table.Cold(slot) is { DiedTick: < 0, IsImprisoned: false } cold && cold.Faction == p.Faction) list.Add(id);
        }
        return list;
    }

    /// <summary>
    /// The group as the steward or the player judges it, against the town's beds, cap, rules and residents.
    /// </summary>
    Applicants Assess(World world, PlayerTown town, TownState state, Platoon p, Squad squad, List<CharacterId> members)
    {
        var table = world.Characters;
        int free = Homes(world, town).Sum(h => h.Free);
        int room = state.Cap - ResidentsOf(state.Key);
        var harms = RegionOf(town)?.Harms ?? new HashSet<WeatherAffecting>();
        var blocked = new SortedSet<string>(StringComparer.Ordinal);
        var harmed = new SortedSet<string>(StringComparer.Ordinal);
        bool armed = false;
        foreach (var id in members)
        {
            table.TryResolveNext(id, out int slot);
            var cold = table.Cold(slot)!;
            if (cold.Race is { } race)
            {
                if (state.BlockedRaces.Contains(race.StringId)) blocked.Add(SettlerData.RaceName(race.StringId));
                if (!state.AllowHarmed)
                    foreach (var kind in harms)
                        if (!race.ImmuneTo(kind) && WeatherSystem.Protection(cold, kind) < 1) harmed.Add(SettlerData.NameOf(kind));
            }
            if (cold.Fighter is { Unarmed: false }) armed = true;
        }

        long purse = squad.Money;
        return new Applicants(members.Count, free, room, [.. blocked], [.. harmed], purse / members.Count, armed, BringsTrade(world, state, members),
            purse);
    }

    /// <summary>
    /// Whether one of the group is better at a trade than every resident of the town (true while it has none).
    /// </summary>
    bool BringsTrade(World world, TownState state, List<CharacterId> members)
    {
        var table = world.Characters;
        var best = new float[Steward.Trades.Length];
        bool any = false;
        foreach (var (id, r) in _residents)
        {
            if (r.Town != state.Key || world.LastingIds.Find(new LastingId(id)) is not { IsNone: false } c) continue;
            if (!table.TryResolveNext(c, out int slot) || table.Cold(slot)?.Stats is not { } stats) continue;
            any = true;
            for (int i = 0; i < best.Length; i++) best[i] = Math.Max(best[i], stats[Steward.Trades[i]]);
        }
        if (!any) return true;

        foreach (var id in members)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot)?.Stats is not { } stats) continue;
            for (int i = 0; i < best.Length; i++)
                if (stats[Steward.Trades[i]] > best[i]) return true;
        }
        return false;
    }

    /// <summary>
    /// Moves the group's members into the town's homes (a squad per home, made when the home has none), shares the group's money among them,
    /// lets the rest of the group go, and writes the news.
    /// </summary>
    void Admit(World world, PlayerTown town, TownState state, SettlerGroup group, Platoon p, Squad squad, List<CharacterId> members, string reason,
        long bribe, int? steward)
    {
        double now = world.GameHours;
        var table = world.Characters;
        if (LeaderSlot(world, squad) is { } leader) StopWalk(world, leader);

        long purse = Math.Max(0, squad.Money - bribe);
        if (bribe > 0 && steward is { } s && world.Squads.Find(table.Cold(s)!.SquadId) is { OwnPurse: true } stewardSquad) stewardSquad.Money += bribe;

        var homes = Homes(world, town);
        var names = new List<string>();
        var races = new SortedSet<string>(StringComparer.Ordinal);
        long share = purse / members.Count;
        foreach (var id in members)
        {
            if (homes.FirstOrDefault(h => h.Free > 0) is not { } home || HomeSquad(world, town, home) is not { } homeSquad) break;
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            StopWalk(world, slot);
            if (!_population!.JoinPlayerSquad(world, id, homeSquad.Id)) continue;
            GiveOwnName(world, id, cold);

            home.Members++;
            homeSquad.Money += share;
            purse -= share;
            names.Add(cold.Name);
            if (cold.Race is { } race) races.Add(SettlerData.RaceName(race.StringId));
            _residents[world.LastingIds.GetOrGive(id).Value] = new Resident(state.Key, home.Site.Id, now, cold.Name);
        }
        squad.Money = purse;

        Let(world, p);
        _groups.Remove(group.Serial);
        _platoons.Remove(group.Serial);
        state.NextArrival = double.NaN;
        string who = names.Count == 1 ? names[0] : $"{names.Count} settlers";
        state.Tell(now, names.Count == 0 ? "a group was accepted but no one could move in" : $"{who} ({string.Join(", ", races)}) moved in: {reason}");
        if (steward is not null && names.Count > 0)
            world.Post($"{who} ({string.Join(", ", races)}) moved into {town.Name}: {reason}. The Town window's news has the details.");
        Log($"{town.Name}: {names.Count} moved in ({string.Join(", ", names)}): {reason}");
    }

    /// <summary>
    /// A settler the game made without a name of their own (named after their record, as "Shek Fighter") takes a personal name from the game's own
    /// name lists on moving in, rolled from the world seed and their lasting id so a reload gives the same one. A named character keeps theirs.
    /// </summary>
    void GiveOwnName(World world, CharacterId id, CharacterCold cold)
    {
        if (_data.Db.Find(cold.RecordId) is not { } record || record.GetBool("named") || cold.Name != record.Name) return;
        if (_population!.Data.Appearances is not { } source) return;

        ulong roll = Rng.Hash(world.Seed, world.LastingIds.GetOrGive(id).Value, RollPurpose, 0x4A3E);
        if (source.RandomName(cold.Appearance?.Female ?? false, roll) is { Length: > 0 } name) cold.Name = name;
    }

    /// <summary>
    /// For the testing rows: lets go a group of the town still on its way (it goes back to wandering), so the row's new group, made close by,
    /// is not held up behind one kilometres out.
    /// </summary>
    void ReplaceTravelling(World world, TownState state, bool visiting)
    {
        foreach (var group in _groups.Values.Where(g => g.Town == state.Key && g.Visiting == visiting && g.Stage == GroupStage.Travelling).ToList())
            Release(world, group, state, "was sent on its way for a test");
    }

    /// <summary>
    /// Lets the group go on its way: its platoon is the population's again, as any wandering group.
    /// </summary>
    void Release(World world, SettlerGroup group, TownState state, string reason)
    {
        if (PlatoonOf(world, group) is { } p)
        {
            if (p.State == PlatoonState.Loaded && world.Squads.Find(p.SquadId) is { } squad)
                foreach (var id in squad.Members)
                    if (world.Characters.TryResolveNext(id, out int slot)) StopWalk(world, slot);
            Let(world, p);
        }
        _groups.Remove(group.Serial);
        _platoons.Remove(group.Serial);
        if (group.Visiting) state.NextVisit = double.NaN;
        else state.NextArrival = double.NaN;
        string name = _data.Db.Find(group.Template)?.Name ?? "travellers";
        state.Tell(world.GameHours, $"{(group.Visiting ? "visitors" : "a group")} ({name}) {reason}");
        Log($"group {group.Serial} ({name}) {reason}");
    }

    /// <summary>
    /// Gives a platoon back to the population: no hold, no tag, no longer the mod's spawn (kept or despawned as any wandering group).
    /// </summary>
    static void Let(World world, Platoon p)
    {
        PopulationSystem.HoldPlatoon(world, p.Id, false);
        p.ModSpawned = false;
        p.ModTag = "";
    }

    #endregion

    #region Arrivals

    /// <summary>
    /// Brings a group to an open town when one is due: a free bed, room under the cap, no group already coming, and a pull of at least
    /// <see cref="NoArrivalsBelow"/>; the next is due a while after the last one is settled or gone.
    /// </summary>
    void Arrivals(World world, PlayerTown town, TownState state)
    {
        double now = world.GameHours;
        if (!state.Open || _groups.Values.Any(g => g.Town == state.Key && !g.Visiting)) return;

        int free = Homes(world, town).Sum(h => h.Free);
        int room = state.Cap - ResidentsOf(state.Key);
        if (free <= 0 || room <= 0) return;

        var (pull, _) = Pull(world, town, state);
        if (pull < NoArrivalsBelow)
        {
            state.NextArrival = double.NaN;
            return;
        }
        if (double.IsNaN(state.NextArrival))
        {
            state.NextArrival = now + Interval(pull, state.Pace);
            return;
        }
        if (now < state.NextArrival) return;

        if (!Bring(world, town, state, Math.Min(free, room))) state.NextArrival = now + 6;
    }

    /// <summary>
    /// The game hours between groups at a pull and pace.
    /// </summary>
    static double Interval(int pull, ArrivalPace pace) =>
        HoursAtFullPull * 100 / Math.Max(pull, 1) * pace switch { ArrivalPace.Slow => 2, ArrivalPace.Fast => 0.5, _ => 1 };

    /// <summary>
    /// The region a town stands in.
    /// </summary>
    SettlerRegion? RegionOf(PlayerTown town) =>
        _data.BiomeGroupAt(Centre(town)) is { } id && SettlerData.Regions.TryGetValue(id, out var region) ? region : null;

    /// <summary>
    /// The groups of the town's region that may come now: their world states pass, their faction is not hostile to the player now, not all
    /// their races are kept out, the weather would not hurt them (unless the player allows it), and the free beds hold the most they can number.
    /// </summary>
    List<(SettlerTemplate Template, int Faction)> Candidates(World world, PlayerTown town, TownState state, int beds, bool visiting = false)
    {
        var list = new List<(SettlerTemplate, int)>();
        if (RegionOf(town) is not { } region || _aggro is null || _population is null) return list;

        int player = world.Player.Faction;
        foreach (var t in region.Templates)
        {
            if (_data.Db.Find(t.TemplateId) is not { } record || !SquadFactory.IsTemplate(record)) continue;
            var template = SquadTemplate.From(record);
            if (!template.WorldState.All(w => IWorldStates.Passes(_population.States, _data.Db, w.Id, w.V0))) continue;
            int faction = _data.SpawnFactionOf(template);
            if (faction < 0 || faction == player || _aggro.Relations.IsHostile(faction, player) || _aggro.Relations.AtWar(faction, player)) continue;
            // Bandit factions (the game's own faction type, the one it treats as lawless) send nobody, whatever their relation says: Skin Bandits,
            // Skeleton Bandits and Reavers start at 0 and are hostile by what they do. The hungry wanderers of the wastes are the exception.
            var data = _data.Factions[faction];
            if (data.FundamentalType == Meitou.Data.Gameplay.FundamentalType.Bandit && !DesperateIds.Contains(data.Id)) continue;
            // A faction that dislikes the player either way (a starving bandit mob is not hostile by the game's line, but robs and fights, and
            // the guards go for it): visitors keep their own faction in town, so they are left out; settlers come as Drifters, people who have
            // left their old band to settle, or are left out when the game has no Drifters at peace with the player.
            if (_aggro.Relations.Get(faction, player) < 0 || _aggro.Relations.Get(player, faction) < 0)
            {
                if (visiting || DriftersFaction(player) is not { } drifters) continue;
                faction = drifters;
            }
            if (t.Races.Count > 0 && t.Races.All(state.BlockedRaces.Contains)) continue;
            if (!state.AllowHarmed && t.Unprotected.Count > 0) continue;
            if (t.MaxSize > beds) continue;
            list.Add((t, faction));
        }
        return list;
    }

    /// <summary>
    /// The game's Drifters faction, which settlers from a faction unfriendly to the player come as.
    /// </summary>
    const string DriftersId = "51707-rebirth.mod";

    /// <summary>
    /// The bandit factions whose people may still come to settle (as Drifters): Starving Bandits and Vagrants, hungry people more than raiders.
    /// </summary>
    static readonly HashSet<string> DesperateIds = new(StringComparer.Ordinal) { "1305-gamedata.base", "96156-rebirth.mod" };

    /// <summary>
    /// The Drifters' faction index, when the game has it and it is at peace with the player both ways; null otherwise.
    /// </summary>
    int? DriftersFaction(int player)
    {
        if (_aggro is null) return null;
        for (int i = 0; i < _data.Factions.Count; i++)
        {
            if (_data.Factions[i].Id != DriftersId) continue;
            bool peace = _aggro.Relations.Get(i, player) >= 0 && _aggro.Relations.Get(player, i) >= 0;
            return peace ? i : null;
        }
        return null;
    }

    /// <summary>
    /// Picks a group by the region's shares and makes it out of sight beyond the town, walking toward it. False when none may come or no place
    /// was found.
    /// </summary>
    bool Bring(World world, PlayerTown town, TownState state, int beds, bool visiting = false)
    {
        var candidates = Candidates(world, town, state, beds, visiting);
        if (candidates.Count == 0)
        {
            Log($"{town.Name}: no group in {RegionOf(town)?.Name ?? "this region"} may come now");
            return false;
        }

        int serial = ++_serial;
        ulong roll = Rng.Hash(world.Seed, (ulong)serial, RollPurpose, 0x5E77);
        float total = candidates.Sum(c => (float)c.Template.Weight), pick = Rng.Float(roll) * total;
        var chosen = candidates[^1];
        foreach (var c in candidates)
        {
            if ((pick -= c.Template.Weight) < 0)
            {
                chosen = c;
                break;
            }
        }

        var centre = Centre(town);
        long money = (100 + Rng.Int(Rng.Hash(world.Seed, (ulong)serial, RollPurpose, 0x3011), 300)) * (long)chosen.Template.MinSize;
        bool hurry = _hurry.Remove(state.Key);
        float near = hurry ? HurryNear : SpawnNear, far = hurry ? HurryFar : SpawnFar;
        int id = _population!.SpawnPlatoon(world, chosen.Faction, chosen.Template.TemplateId, NearestSite(centre), centre, town.Radius + near,
            town.Radius + far, money);
        if (id < 0 || world.Platoons.Find(id) is not { } p)
        {
            Log($"{town.Name}: no place found for {chosen.Template.Name}");
            return false;
        }

        var group = new SettlerGroup { Serial = serial, Town = state.Key, Template = chosen.Template.TemplateId, Since = world.GameHours, Visiting = visiting };
        p.ModTag = group.Tag;
        PopulationSystem.HoldPlatoon(world, id, true);
        PopulationSystem.SendPlatoon(world, id, centre);
        _groups[serial] = group;
        _platoons[serial] = id;
        Log($"{town.Name}: {chosen.Template.Name} on its way (group {serial}, platoon {id}){(visiting ? ", visiting" : "")}");
        return true;
    }

    /// <summary>
    /// The placed town site nearest a point, for a spawned group's home.
    /// </summary>
    int NearestSite(Vector2 at)
    {
        int best = 0;
        float bestD = float.MaxValue;
        foreach (var site in _data.Sites)
        {
            float d = Vector2.DistanceSquared(at, new Vector2(site.Position.X, site.Position.Z));
            if (d < bestD) (best, bestD) = (site.Index, d);
        }
        return best;
    }

    #endregion

    #region Homes, residents, the steward and the pull

    /// <summary>
    /// The town's homes: its complete Home buildings, each with its complete beds and the squad living there.
    /// </summary>
    List<Home> Homes(World world, PlayerTown town)
    {
        var beds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in world.Buildings.All)
        {
            if (site.Parent is not { } parent || !site.Complete || site.Destroyed) continue;
            if (world.Fixtures.Find(site.Id + "@bed0") is { Kind: FixtureKind.Bed }) beds[parent] = beds.GetValueOrDefault(parent) + 1;
        }

        var homes = new List<Home>();
        foreach (var (id, rules) in _rules)
        {
            if ((rules & BuildingRule.Residents) == 0 || world.Buildings.Find(id) is not { Complete: true, Destroyed: false, Ruined: false } site) continue;
            if (TownOf(world, site) != town.Number) continue;
            homes.Add(new Home { Site = site, Beds = beds.GetValueOrDefault(id) });
        }

        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (squad.Dead || squad.Faction != world.Player.Faction || !squad.ModTags.TryGetValue(ModId, out string? tag)) continue;
            if (!tag.StartsWith(HomeTag, StringComparison.Ordinal) || homes.Find(h => h.Site.Id == tag[HomeTag.Length..]) is not { } home) continue;
            home.Squad = squad;
            home.Members = squad.Members.Count(m => table.IsAlive(m));
        }
        return homes;
    }

    /// <summary>
    /// The squad living in a home, made when it has none: independent of the player, outside the faction's size count, with its own purse, on
    /// the residents' package, at home there. False-y (null) when the game refuses a step.
    /// </summary>
    Squad? HomeSquad(World world, PlayerTown town, Home home)
    {
        if (home.Squad is { } known) return known;
        if (_ai is null || _player is null) return null;

        var squad = SquadManagement.NewSquad(world, _data.Db, home.Site.Position, "Fiefdom");
        squad.Name = $"{town.Name} residents";
        squad.ModTags[ModId] = HomeTag + home.Site.Id;
        List<SimCommand> ai = [new SetSquadAutonomy(squad.Id, true, Independent: true, Owner: ModId) { Tick = world.Tick }];
        if (_data.Db.Find(ResidentPackage) is not null) ai.Add(new ChangeAi(squad.Id, ResidentPackage) { Tick = world.Tick });
        else if (!s_packageLogged)
        {
            s_packageLogged = true;
            Log($"the residents' package {ResidentPackage} is not in the game data; residents get none");
        }
        ai.Add(new SetSquadHome(squad.Id, home.Site.Id) { Tick = world.Tick });
        _ai.Inputs(world, ai);
        _player.Inputs(world, [new SetSquadUncounted(squad.Id, true, ModId) { Tick = world.Tick }, new SetSquadPurse(squad.Id, true, ModId) { Tick = world.Tick }]);
        home.Squad = squad;
        home.Members = 0;
        return squad;
    }

    /// <summary>
    /// Every 30 seconds: each home squad runs the residents' package and each guard squad its duty's, so squads made before Fiefdom's own package
    /// existed, or changed by something else, come back to it. Only a squad whose AI already runs a package is changed.
    /// </summary>
    void KeepPackages(World world)
    {
        if (_ai is null) return;
        var commands = new List<SimCommand>();
        foreach (var squad in world.Squads.All)
        {
            if (squad.Dead || squad.Faction != world.Player.Faction || squad.Ai?.Current is not { } current) continue;
            if (!squad.ModTags.TryGetValue(ModId, out string? tag)) continue;
            string? want = tag.StartsWith(HomeTag, StringComparison.Ordinal) ? ResidentPackage
                : tag.StartsWith(GuardTag, StringComparison.Ordinal)
                    ? ((_rules.GetValueOrDefault(tag[GuardTag.Length..]) & BuildingRule.GateDuty) != 0 ? GatePackage : PatrolPackage)
                : null;
            // Guard squads made by earlier versions were led by the player; guards are not the player's to command, as residents are not.
            if (want is not null && !squad.Independent)
            {
                commands.Add(new SetSquadAutonomy(squad.Id, true, Independent: true, Owner: ModId) { Tick = world.Tick });
                Log($"{squad.Name}: no longer led by the player");
            }
            if (tag.StartsWith(GuardTag, StringComparison.Ordinal)) KeepGuardsJobsOn(world, squad);
            if (want is null || current.Id == want || _data.Db.Find(want) is null) continue;
            commands.Add(new ChangeAi(squad.Id, want) { Tick = world.Tick });
            Log($"{squad.Name}: package {current.Id} changed to {want}");
        }
        if (commands.Count > 0) _ai.Inputs(world, commands);
    }

    /// <summary>
    /// Keeps the jobs switch on for a guard squad's members. Version 0.1.1 turned it off to keep guards out of the faction-wide idle jobs (Sit when
    /// idle), but with it off a player-faction character's AI chooses no goal at all (tested headless: guards stood "Aimless" all day, and with
    /// it on the same guard patrolled at once), so it is turned back on for the guards that version touched. Guards cannot be selected, so the
    /// switch is never the player's choice for them.
    /// </summary>
    static void KeepGuardsJobsOn(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { } cold && (cold.Orders & CharacterOrders.Jobs) == 0)
                cold.Orders |= CharacterOrders.Jobs;
    }

    /// <summary>
    /// The F10 row "log what townspeople are doing": for each of the mod's squads, its package, autonomy and leader, then each member's
    /// squad role, goal (and duty), task and whether the chooser weighs 36 Patrolling town for it now. A read of the world only.
    /// </summary>
    void LogTownspeople(World world)
    {
        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (squad.Dead || !squad.ModTags.TryGetValue(ModId, out string? tag)) continue;
            string leader = table.TryResolveNext(squad.Leader, out int ls) ? table.Cold(ls)?.Name ?? "?" : "none";
            Log($"minds: {squad.Name} ({tag}): package {squad.Ai?.Current?.Id ?? "none"}, autonomous {squad.Autonomous}, independent {squad.Independent}, leader {leader}");
            foreach (var id in squad.Members)
            {
                if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
                var role = AiSystem.RoleOf(squad, id, cold);
                string goal = cold.Ai?.Goal?.Task.Type.ToString() ?? "none";
                string duty = cold.Ai?.Duty?.Task.Type.ToString() ?? "none";
                bool patrol = AiSystem.WeighsGoal(world, slot, TaskType.PatrolTown);
                Log($"minds:   {cold.Name}: role {role}, goal {goal}, duty {duty}, task {(CharacterTask)table.Next[slot].Task}, weighs patrol {patrol}");
            }
        }
    }

    /// <summary>
    /// How many residents a town has.
    /// </summary>
    int ResidentsOf(string key) => _residents.Values.Count(r => r.Town == key);

    /// <summary>
    /// The steward's slot when they are alive, awake, still the player's and within the town; else null.
    /// </summary>
    static int? StewardHere(World world, PlayerTown town, TownState state)
    {
        if (state.Steward == 0) return null;

        var table = world.Characters;
        var id = world.LastingIds.Find(new LastingId(state.Steward));
        if (id.IsNone || !table.TryResolveNext(id, out int slot) || !table.Next[slot].Alive) return null;
        if (table.Cold(slot) is not { IsPlayer: true, DiedTick: < 0, Medical.Incapacitated: false }) return null;

        var at = table.Next[slot].Position;
        return Vector2.Distance(new Vector2(at.X, at.Z), Centre(town)) <= town.Radius ? slot : null;
    }

    /// <summary>
    /// A character's personality tag (rolled from its record and faction the first time it is asked, as dialogue does).
    /// </summary>
    int PersonalityOf(World world, int slot, CharacterCold cold) =>
        Personality.Of(world, _data.Db, world.Characters.IdOf(slot), cold,
            cold.Faction >= 0 && cold.Faction < _data.Factions.Count ? _data.Factions[cold.Faction].Id : null);

    /// <summary>
    /// The name of the character with a lasting id, or "someone" when they are not in the world.
    /// </summary>
    static string NameOfLasting(World world, ulong id)
    {
        var c = world.LastingIds.Find(new LastingId(id));
        return c.IsNone ? "someone" : world.Characters.NameOf(c) is { Length: > 0 } name ? name : "someone";
    }

    /// <summary>
    /// How strongly the town draws settlers, 0 to 100, with the reasons: guarded by the player's own people, a bar and a shop raise it; no one of
    /// the player's in town and residents killed lately lower it.
    /// </summary>
    (int Score, List<string> Reasons) Pull(World world, PlayerTown town, TownState state)
    {
        var reasons = new List<string> { "50 to start" };
        int score = 50;
        int residents = ResidentsOf(state.Key);
        int defenders = Defenders(world, town);
        if (defenders == 0)
        {
            score -= 20;
            reasons.Add("-20 none of your people in town to guard it");
        }
        else if (residents == 0 || defenders * 5 >= residents)
        {
            score += 15;
            reasons.Add($"+15 guarded ({defenders} of your people in town)");
        }
        else
        {
            score += 5;
            reasons.Add($"+5 thinly guarded ({defenders} of your people for {residents} residents)");
        }

        // Shops are the shop counters (furniture function 9) standing complete inside the town's buildings and holding goods, not a label.
        bool goods = false, food = false;
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != ShopCounterFunction || m.Storage is not { } stock || stock.Items.Count == 0) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true, Parent: not null } site || site.Owner != world.Player.Faction) continue;
            if (TownOf(world, site) != town.Number) continue;
            goods = true;
            food |= stock.Items.Any(i => i.IsFood);
        }
        if (goods)
        {
            score += 10;
            reasons.Add("+10 goods for sale");
        }
        if (food)
        {
            score += 10;
            reasons.Add("+10 food for sale");
        }

        int killed = state.Killings.Count(at => world.GameHours - at <= KillingMemory);
        if (killed > 0)
        {
            int loss = Math.Min(45, killed * 15);
            score -= loss;
            reasons.Add($"-{loss} {killed} resident{(killed == 1 ? "" : "s")} killed in the last three days");
        }
        int left = state.Leavings.Count(at => world.GameHours - at <= LeavingMemory);
        if (left > 0)
        {
            int loss = Math.Min(30, left * 10);
            score -= loss;
            reasons.Add($"-{loss} {left} resident{(left == 1 ? "" : "s")} left hungry in the last three days");
        }
        return (Math.Clamp(score, 0, 100), reasons);
    }

    /// <summary>
    /// The player's own characters (not residents, not independent squads) alive, awake and inside the town.
    /// </summary>
    static int Defenders(World world, PlayerTown town)
    {
        var table = world.Characters;
        var centre = Centre(town);
        float r2 = town.Radius * town.Radius;
        int n = 0;
        for (int i = 0; i < table.Next.Length; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: true, DiedTick: < 0, Medical.Incapacitated: false } cold) continue;
            if (world.Squads.Find(cold.SquadId) is not { Independent: false }) continue;
            var at = table.Next[i].Position;
            if (Vector2.DistanceSquared(new Vector2(at.X, at.Z), centre) <= r2) n++;
        }
        return n;
    }

    /// <summary>
    /// Once a game day: drops residents gone for good or no longer the player's, forgets old killings and stewards gone for good.
    /// </summary>
    void CheckResidents(World world)
    {
        double now = world.GameHours;
        var kept = world.LastingIds.KeptIds();
        var table = world.Characters;
        foreach (var (id, r) in _residents.ToList())
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone)
            {
                if (kept.Contains(new LastingId(id))) continue;
                _residents.Remove(id);
                TownOf(r.Town).Tell(now, $"{r.Name} is gone");
            }
            else if (table.TryResolveNext(c, out int slot) && table.Cold(slot) is { IsPlayer: false })
            {
                _residents.Remove(id);
                TownOf(r.Town).Tell(now, $"{r.Name} is no longer one of the town's");
            }
        }
        foreach (var state in _towns.Values)
        {
            state.Killings.RemoveAll(at => now - at > KillingMemory);
            state.Leavings.RemoveAll(at => now - at > LeavingMemory);
            if (state.Steward != 0 && world.LastingIds.Find(new LastingId(state.Steward)).IsNone && !kept.Contains(new LastingId(state.Steward)))
                state.Steward = 0;
        }
        _dirty = true;
    }

    /// <summary>
    /// A death: a resident leaves the town's records (a killing by another faction lowers the pull for three days), and a steward leaves the
    /// post.
    /// </summary>
    public void Died(World world, in CharacterDeath death)
    {
        if (death.Victim.IsNone || !world.Characters.TryResolveNext(death.Victim, out int slot) || world.Characters.Cold(slot) is not { } cold) return;
        if (cold.LastingId.IsNone) return;

        ulong id = cold.LastingId.Value;
        double now = world.GameHours;
        if (_residents.Remove(id, out var r))
        {
            string killer = !death.Killer.IsNone && world.Characters.TryResolveNext(death.Killer, out int ks) && world.Characters.Cold(ks) is { } kc
                ? $"{kc.Name} of faction {death.KillerFaction}" : death.KillerFaction >= 0 ? $"faction {death.KillerFaction}" : "no one";
            Log($"{r.Name} died: {death.Cause}, killed by {killer}");
            var state = TownOf(r.Town);
            state.Tell(now, $"{r.Name} died");
            if (death.KillerFaction >= 0 && death.KillerFaction != world.Player.Faction) state.Killings.Add(now);
            _dirty = true;
        }
        foreach (var state in _towns.Values)
        {
            if (state.Steward != id) continue;
            state.Steward = 0;
            state.Tell(now, $"steward {cold.Name} died");
            _dirty = true;
        }
    }

    #endregion
}
