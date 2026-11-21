using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;

namespace Fiefdom;

/// <summary>
/// Who lives in a building of a Fiefdom town.
/// </summary>
public enum Living : byte
{
    /// <summary>Nobody the town places.</summary>
    Nobody,

    /// <summary>Settlers, one per bed.</summary>
    Residents,

    /// <summary>The town's guards, one per bed.</summary>
    Guards,
}

/// <summary>
/// Sets who lives in the player's building <paramref name="Site"/>: residents, guards or nobody the town places. Refused while the other kind
/// still lives there.
/// </summary>
public sealed record SetLiving(string Site, Living Living) : SimCommand;

/// <summary>
/// Sets a guardhouse's duty: watching the gates (<paramref name="Gate"/>) or patrolling the town.
/// </summary>
public sealed record SetDuty(string Site, bool Gate) : SettlerCommand;

/// <summary>
/// Sends one guard back to live as a resident (in a home with a free bed).
/// </summary>
public sealed record ReleaseGuard(CharacterId Character) : SettlerCommand;

/// <summary>
/// One guardhouse of a town: its building, its beds, its duty and the guard squad living there.
/// </summary>
sealed class Guardhouse
{
    public required BuildSite Site { get; init; }
    public required int Beds { get; init; }
    public required bool Gate { get; init; }
    public Squad? Squad { get; set; }
    public int Members { get; set; }
    public int Free => Math.Max(0, Beds - Members);
}

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// The guard squads' packages. Patrolling the town is Fiefdom's own (in Fiefdom.mod: the game's town guard day with "Patrol hometown" by
    /// day, first aid, fighting inside the town, bed at night); without Fiefdom's data the game's "Guard town no shifts 24hr", which stands guard
    /// in the guardhouse by day instead. Watching the gates is the game's "Gate guard -no turrets" (the leader mans a gate, the others stand guard
    /// in town; fighting inside the town, first aid, alarms).
    /// </summary>
    public const string OwnPatrolPackage = "4-Fiefdom.mod", GamePatrolPackage = "62853-rebirth.mod", GatePackage = "52279-rebirth.mod";

    /// <summary>
    /// The package patrol guards run: Fiefdom's own when its data is loaded, else the game's.
    /// </summary>
    string PatrolPackage => _data.Db.Find(OwnPatrolPackage) is not null ? OwnPatrolPackage : GamePatrolPackage;

    /// <summary>
    /// A guard squad's tag value starts with this, then its guardhouse's building id.
    /// </summary>
    const string GuardTag = "guards:";

    /// <summary>
    /// A guard line's key starts with this, then its guardhouse's building id.
    /// </summary>
    public const string GuardLine = "guards|";

    /// <summary>
    /// Whether a resident is one of the guards: they live in a guardhouse.
    /// </summary>
    bool IsGuard(ulong id) => _residents.TryGetValue(id, out var r) && (_rules.GetValueOrDefault(r.Home) & BuildingRule.Guards) != 0;

    /// <summary>
    /// Sets who lives in a building, when it is the player's, stands in a Fiefdom town and can take rules. Turning a home with residents into a
    /// guardhouse, or a guardhouse with guards into anything else, is refused with a message.
    /// </summary>
    void Apply(World world, SetLiving command)
    {
        if (OwnedInTown(world, command.Site) is not { } site || !RuleRecords.Contains(site.Record)) return;

        var rules = _rules.GetValueOrDefault(site.Id);
        bool guards = (rules & BuildingRule.Guards) != 0;
        if (guards && command.Living != Living.Guards && GuardSquadOf(world, site.Id) is { } squad && LivingIn(world, squad) > 0)
        {
            world.Post($"Guards still live in the {site.Name}: take them off in the Fiefdom window's Work tab first.");
            return;
        }
        if (command.Living == Living.Guards && !guards && HomeSquadOf(world, site.Id) is { } home && LivingIn(world, home) > 0)
        {
            world.Post($"Residents still live in the {site.Name}: it can house guards once they have moved out.");
            return;
        }

        rules &= ~(BuildingRule.Residents | BuildingRule.Guards);
        if (command.Living == Living.Residents) rules |= BuildingRule.Residents;
        if (command.Living == Living.Guards) rules |= BuildingRule.Guards;
        else rules &= ~BuildingRule.GateDuty;
        if (rules == BuildingRule.None) _rules.Remove(site.Id);
        else _rules[site.Id] = rules;
        _dirty = true;
    }

    /// <summary>
    /// The living members of a squad.
    /// </summary>
    static int LivingIn(World world, Squad squad) => squad.Members.Count(m => world.Characters.IsAlive(m));

    /// <summary>
    /// The squad the mod made for a home, if any.
    /// </summary>
    static Squad? HomeSquadOf(World world, string site) => TaggedSquad(world, HomeTag + site);

    /// <summary>
    /// The guard squad the mod made for a guardhouse, if any.
    /// </summary>
    static Squad? GuardSquadOf(World world, string site) => TaggedSquad(world, GuardTag + site);

    /// <summary>
    /// The living squad of the player's faction carrying the mod's tag <paramref name="tag"/>.
    /// </summary>
    static Squad? TaggedSquad(World world, string tag)
    {
        foreach (var squad in world.Squads.All)
            if (!squad.Dead && squad.Faction == world.Player.Faction && squad.ModTags.TryGetValue(ModId, out string? t) && t == tag) return squad;
        return null;
    }

    /// <summary>
    /// The town's guardhouses: its complete buildings guards live in, each with its complete beds, its duty and its squad.
    /// </summary>
    List<Guardhouse> Guardhouses(World world, PlayerTown town)
    {
        var houses = new List<Guardhouse>();
        foreach (var (id, rules) in _rules)
        {
            if ((rules & BuildingRule.Guards) == 0 || world.Buildings.Find(id) is not { Complete: true, Destroyed: false, Ruined: false } site) continue;
            if (TownOf(world, site) != town.Number) continue;
            var house = new Guardhouse { Site = site, Beds = BedsIn(world, id), Gate = (rules & BuildingRule.GateDuty) != 0 };
            if (GuardSquadOf(world, id) is { } squad)
            {
                house.Squad = squad;
                house.Members = LivingIn(world, squad);
            }
            houses.Add(house);
        }
        return houses;
    }

    /// <summary>
    /// How many complete beds stand in a building.
    /// </summary>
    static int BedsIn(World world, string building)
    {
        int beds = 0;
        foreach (var site in world.Buildings.All)
            if (site.Parent == building && site.Complete && !site.Destroyed && world.Fixtures.Find(site.Id + "@bed0") is { Kind: Meitou.Simulation.Locks.FixtureKind.Bed }) beds++;
        return beds;
    }

    /// <summary>
    /// The guard squad living in a guardhouse, made when it has none: led by the player (it has a squad tab and takes orders, which come first),
    /// run by the AI on the duty's guard package between orders, outside the faction's size count, with its own purse, at home there. Null when
    /// the player already has the most squads the game allows, or a system is missing.
    /// </summary>
    Squad? GuardSquad(World world, PlayerTown town, Guardhouse house)
    {
        if (house.Squad is { } known) return known;
        if (_ai is null || _player is null) return null;
        if (SquadManagement.LedSquads(world).Count >= _data.Bodies.Constants.MaxSquads)
        {
            world.Post("Maximum number of squads reached: the guards need a squad of their own.");
            return null;
        }

        var squad = SquadManagement.NewSquad(world, _data.Db, house.Site.Position, "Fiefdom");
        squad.Name = $"{town.Name} guards";
        squad.ModTags[ModId] = GuardTag + house.Site.Id;
        string package = house.Gate ? GatePackage : PatrolPackage;
        List<SimCommand> ai = [new SetSquadAutonomy(squad.Id, true, Independent: false, Owner: ModId) { Tick = world.Tick }];
        if (_data.Db.Find(package) is not null) ai.Add(new ChangeAi(squad.Id, package) { Tick = world.Tick });
        else Log($"the guards' package {package} is not in the game data; guards get none");
        ai.Add(new SetSquadHome(squad.Id, house.Site.Id) { Tick = world.Tick });
        _ai.Inputs(world, ai);
        _player.Inputs(world, [new SetSquadUncounted(squad.Id, true, ModId) { Tick = world.Tick }, new SetSquadPurse(squad.Id, true, ModId) { Tick = world.Tick }]);
        house.Squad = squad;
        house.Members = 0;
        Log($"{town.Name}: guard squad {squad.Id} made for the {house.Site.Name} ({house.Site.Id}), {(house.Gate ? "gates" : "patrol")}");
        return squad;
    }

    /// <summary>
    /// Applies a guard command, or a line command on a guard line; false when it is neither.
    /// </summary>
    bool ApplyGuard(World world, SettlerCommand command)
    {
        switch (command)
        {
            case SetDuty duty:
                SetGuardDuty(world, duty);
                return true;
            case ReleaseGuard release:
                if (ResidentLasting(world, release.Character) is var rid and not 0 && IsGuard(rid) && FindTown(world, _residents[rid].Town) is { } rtown)
                    Release(world, rtown, rid);
                return true;
            case AddWorker add when add.Line.StartsWith(GuardLine, StringComparison.Ordinal):
                if (world.Buildings.FindTown(add.Town) is not { } town || !IsFiefdom(world, town) || KeyOf(town) is not { } key) return true;
                ulong id = add.Character.IsNone ? IdleResidents(world, key).FirstOrDefault() : ResidentLasting(world, add.Character);
                if (id == 0 || _residents[id].Town != key || IsGuard(id)) return true;
                Enlist(world, town, key, add.Line[GuardLine.Length..], id);
                return true;
            case RemoveWorker remove when remove.Line.StartsWith(GuardLine, StringComparison.Ordinal):
                if (world.Buildings.FindTown(remove.Town) is not { } rt || !IsFiefdom(world, rt)) return true;
                if (GuardSquadOf(world, remove.Line[GuardLine.Length..]) is not { } squad) return true;
                var table = world.Characters;
                for (int i = squad.Members.Count - 1; i >= 0; i--)
                {
                    if (!table.TryResolveNext(squad.Members[i], out int slot) || !table.Next[slot].Alive || table.Cold(slot) is not { LastingId.IsNone: false } cold) continue;
                    if (_residents.ContainsKey(cold.LastingId.Value)) Release(world, rt, cold.LastingId.Value);
                    break;
                }
                return true;
        }
        return false;
    }

    /// <summary>
    /// Makes a resident a guard: they leave any post and move into the guardhouse's squad, when it has a free bed.
    /// </summary>
    void Enlist(World world, PlayerTown town, string key, string site, ulong id)
    {
        if (_player is null || Guardhouses(world, town).Find(h => h.Site.Id == site) is not { } house) return;
        var r = _residents[id];
        if (house.Free <= 0)
        {
            world.Post($"No free bed for another guard in the {house.Site.Name}.");
            return;
        }
        var c = world.LastingIds.Find(new LastingId(id));
        if (c.IsNone || GuardSquad(world, town, house) is not { } squad) return;

        if (_holders.Remove(id, out var was))
        {
            var table = world.Characters;
            if (table.TryResolveNext(c, out int slot) && table.Cold(slot) is { } cold) TakeJob(world, c, cold, was.Site);
        }
        if (!MoveTo(world, c, squad))
        {
            Log($"{town.Name}: {r.Name} could not join the guards (squad {squad.Id} full?)");
            return;
        }
        _residents[id] = r with { Home = site };
        TownOf(key).Tell(world.GameHours, $"{r.Name} joined the guards at the {house.Site.Name}");
        Log($"{town.Name}: {r.Name} joined the guards at the {house.Site.Name} ({site}), squad {squad.Id}");
        _dirty = true;
    }

    /// <summary>
    /// Moves one of the player's characters into another of the player's squads with the game's own squad move (the squad screen's drag). The
    /// game moves only between squads the player leads, so an independent squad at either end is led for that one move and independent again
    /// at once (only its independence changes; its AI, home and minds stay). True when the character is in <paramref name="to"/> after.
    /// </summary>
    bool MoveTo(World world, CharacterId c, Squad to)
    {
        var table = world.Characters;
        if (_player is null || _ai is null || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { } cold) return false;
        if (world.Squads.Find(cold.SquadId) is not { } from) return false;
        var flipped = new[] { from, to }.Where(s => s.Independent).Distinct().ToList();
        if (flipped.Count > 0) _ai.Inputs(world, [.. flipped.Select(s => (SimCommand)new SetSquadAutonomy(s.Id, true, Independent: false, Owner: ModId) { Tick = world.Tick })]);
        _player.Inputs(world, [new MoveToSquadCommand(c, to.Id) { Tick = world.Tick }]);
        if (flipped.Count > 0) _ai.Inputs(world, [.. flipped.Select(s => (SimCommand)new SetSquadAutonomy(s.Id, true, Independent: true, Owner: ModId) { Tick = world.Tick })]);
        return table.TryResolveNext(c, out slot) && table.Cold(slot)?.SquadId == to.Id;
    }

    /// <summary>
    /// Sends a guard back to live as a resident, in the first home with a free bed; refused with a message when none has one.
    /// </summary>
    void Release(World world, PlayerTown town, ulong id)
    {
        if (_player is null) return;
        var r = _residents[id];
        var c = world.LastingIds.Find(new LastingId(id));
        if (c.IsNone) return;
        if (Homes(world, town).FirstOrDefault(h => h.Free > 0) is not { } home || HomeSquad(world, town, home) is not { } homeSquad)
        {
            world.Post($"{r.Name} has no free bed in a home to go back to: build a bed in a building residents live in.");
            return;
        }
        if (!MoveTo(world, c, homeSquad)) return;
        _residents[id] = r with { Home = home.Site.Id };
        _pay.Remove(id);
        TownOf(r.Town).Tell(world.GameHours, $"{r.Name} left the guards and lives in the {home.Site.Name}");
        Log($"{town.Name}: {r.Name} left the guards for the {home.Site.Name} ({home.Site.Id})");
        _dirty = true;
    }

    /// <summary>
    /// Sets a guardhouse's duty, and its squad's package with it.
    /// </summary>
    void SetGuardDuty(World world, SetDuty duty)
    {
        if (OwnedInTown(world, duty.Site) is not { } site || (_rules.GetValueOrDefault(site.Id) & BuildingRule.Guards) == 0) return;
        var rules = _rules[site.Id];
        _rules[site.Id] = duty.Gate ? rules | BuildingRule.GateDuty : rules & ~BuildingRule.GateDuty;
        string package = duty.Gate ? GatePackage : PatrolPackage;
        if (GuardSquadOf(world, site.Id) is { } squad && _ai is not null && _data.Db.Find(package) is not null)
            _ai.Inputs(world, [new ChangeAi(squad.Id, package) { Tick = world.Tick }]);
        if (world.Buildings.FindTown(TownOf(world, site)) is { } town && KeyOf(town) is { } key)
            TownOf(key).Tell(world.GameHours, $"The guards of the {site.Name} now {(duty.Gate ? "watch the gates" : "patrol the town")}");
        _dirty = true;
    }

    /// <summary>
    /// At a day change: every guard is paid the guard's daily wage from the player's money into their squad's purse, in lasting-id order while
    /// the money lasts. Three day changes unpaid in a row and a guard leaves the town. Adds to each town's bill.
    /// </summary>
    void PayGuards(World world)
    {
        long wage = FiefdomSettings.Current.GuardWagePerDay;
        var table = world.Characters;
        foreach (var (id, r) in _residents.ToList())
        {
            if (!IsGuard(id)) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            var pay = PayOf(id);
            var bill = _lastBill.GetValueOrDefault(r.Town);
            if (wage <= 0) continue;
            if (world.Player.Money >= wage)
            {
                world.Player.Money -= wage;
                if (world.Squads.Find(cold.SquadId) is { OwnPurse: true } squad) squad.Money += wage;
                if (pay.Unpaid > 0) TownOf(r.Town).Tell(world.GameHours, $"{r.Name} was paid and the guards are content");
                pay.Unpaid = 0;
                _lastBill[r.Town] = (bill.Paid + wage, bill.Unpaid);
                continue;
            }
            pay.Unpaid++;
            _lastBill[r.Town] = (bill.Paid, bill.Unpaid + 1);
            if (pay.Unpaid >= 3)
            {
                TownOf(r.Town).Tell(world.GameHours, $"{r.Name} left the guards and the town: unpaid three days running");
                Log($"{r.Name}: guard unpaid three days, leaving");
                _pay.Remove(id);
                Evict(world, c);
            }
            else
            {
                TownOf(r.Town).Tell(world.GameHours, $"guard {r.Name} went unpaid ({pay.Unpaid} day{(pay.Unpaid > 1 ? "s" : "")}); three and they leave");
                Log($"{r.Name}: guard unpaid ({pay.Unpaid} day(s)), player has {world.Player.Money}");
            }
        }
    }

    /// <summary>
    /// Once a game day: a guard whose guardhouse is gone (dismantled, destroyed, or no longer housing guards) goes back to live in a home with a
    /// free bed, if one has room.
    /// </summary>
    void CheckGuards(World world)
    {
        var table = world.Characters;
        foreach (var (id, r) in _residents.ToList())
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            if (GuardSquadOf(world, r.Home) is not { } squad || cold.SquadId != squad.Id) continue;
            bool standing = world.Buildings.Find(r.Home) is { Complete: true, Destroyed: false, Ruined: false } && IsGuard(id);
            if (!standing && FindTown(world, r.Town) is { } town) Release(world, town, id);
        }
    }
}
