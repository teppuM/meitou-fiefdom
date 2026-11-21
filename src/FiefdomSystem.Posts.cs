using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Fiefdom;

/// <summary>
/// When a post holder works: by day, by night, or around the clock.
/// </summary>
public enum Shift : byte
{
    Day,
    Night,
    Always,
}

/// <summary>
/// What kind of post a piece of furniture makes.
/// </summary>
public enum PostKind : byte
{
    /// <summary>A machine people work: a research bench, farm, mine or well, refinery, crafting bench (the game's operate job).</summary>
    Machine,

    /// <summary>A gun turret (the game's turret guard job).</summary>
    Turret,

    /// <summary>A shop counter inside a building (the mod's merchant job: keeps it stocked from town stock).</summary>
    Counter,

    /// <summary>A guard's bed in a guardhouse (the guard squad's own package: patrols or the gates).</summary>
    Guard,

    /// <summary>A food runner (the mod's job: carries meals from town stock to hungry workers and guards).</summary>
    Runner,
}

/// <summary>
/// The hours a line of work keeps: around the clock (the default, like a squad job), by day, by night, or in two shifts (one person by day and
/// another by night on each place).
/// </summary>
public enum Hours : byte
{
    DayAndNight,
    Day,
    Night,
    TwoShifts,
}

/// <summary>
/// A resident's post: the town, the furniture's building id and the shift.
/// </summary>
public sealed record PostHolder(string Town, string Site, Shift Shift);

/// <summary>
/// Puts someone on a line of work (the same furniture in the same building): <paramref name="Character"/>, or with none the first resident
/// without a post.
/// </summary>
public sealed record AddWorker(int Town, string Line, CharacterId Character) : SettlerCommand;

/// <summary>
/// Takes the most recently added worker off a line of work.
/// </summary>
public sealed record RemoveWorker(int Town, string Line) : SettlerCommand;

/// <summary>
/// Sets the hours a line of work keeps; its workers' shifts follow, and any who no longer fit lose the post.
/// </summary>
public sealed record SetLineHours(int Town, string Line, Hours Hours) : SettlerCommand;

/// <summary>
/// Puts every resident of the town without a post on the first line with a place free.
/// </summary>
public sealed record PutIdleToWork(int Town) : SettlerCommand;

/// <summary>
/// Puts a resident of the town on a post (a work machine or turret of the town with a place free on the shift).
/// </summary>
public sealed record AssignPost(int Town, CharacterId Character, string Site) : SettlerCommand;

/// <summary>
/// Takes a resident off their post.
/// </summary>
public sealed record ClearPost(CharacterId Character) : SettlerCommand;

/// <summary>
/// Changes the shift of a resident's post.
/// </summary>
public sealed record SetShift(CharacterId Character, Shift Shift) : SettlerCommand;

/// <summary>
/// For testing: every resident of the town without a post takes an open one.
/// </summary>
public sealed record TakePosts(int Town) : SettlerCommand;

/// <summary>
/// For testing: plans one piece of furniture (<paramref name="Record"/>) in every home of the town, at <paramref name="Offset"/> from the home's
/// middle, for the selection to build.
/// </summary>
public sealed record PlanFurniture(int Town, string Record, Vector3 Offset) : SettlerCommand;

/// <summary>
/// For testing: puts <paramref name="Count"/> of item <paramref name="Record"/> into the town's first town-stock box.
/// </summary>
public sealed record FillTownStock(int Town, string Record, int Count) : SettlerCommand;

public sealed partial class FiefdomSystem
{
    /// <summary>
    /// The hours the day shift starts and the night shift starts.
    /// </summary>
    public const int DayShiftStarts = 7, NightShiftStarts = 19;

    /// <summary>
    /// The building function of a shop counter (the game's player shops look for it inside a building).
    /// </summary>
    const int ShopCounterFunction = 9;

    /// <summary>
    /// A worker below this hunger leaves the job to eat (the game's own "hungry" line in dialogue), and goes back once at or above
    /// <see cref="Fed"/> (where the game stops a character eating more).
    /// </summary>
    const float Hungry = 1.7f, Fed = 2.5f;

    /// <summary>
    /// A worker's pay record: which shifts they worked today (bit 1 day, 2 night), how many day changes in a row went unpaid, and whether they are
    /// off eating (saved, but for the eating flag, which the next step works out again).
    /// </summary>
    sealed class PayState
    {
        public byte Worked;
        public byte Unpaid;
        public bool Eating;
    }

    /// <summary>
    /// The workers' pay records, by lasting id.
    /// </summary>
    readonly SortedDictionary<ulong, PayState> _pay = [];

    /// <summary>
    /// A worker's pay record, made when first asked for.
    /// </summary>
    PayState PayOf(ulong id) => _pay.TryGetValue(id, out var p) ? p : _pay[id] = new PayState();

    /// <summary>
    /// What each town's workers were last paid at a day change, by town key (for the window only, not saved).
    /// </summary>
    readonly Dictionary<string, (long Paid, int Unpaid)> _lastBill = new(StringComparer.Ordinal);

    /// <summary>
    /// At a day change: every worker is paid the wage for each shift they worked that day, from the player's money into their home's purse, in
    /// lasting-id order while the money lasts. One left unpaid stops working until paid; three day changes unpaid in a row and they leave the town.
    /// </summary>
    void PayWages(World world)
    {
        long wage = FiefdomSettings.Current.WagePerShift;
        var table = world.Characters;
        _lastBill.Clear();
        foreach (var (id, holder) in _holders.ToList())
        {
            var pay = PayOf(id);
            int shifts = (pay.Worked & 1) + (pay.Worked >> 1 & 1);
            pay.Worked = 0;
            // One who stopped working unpaid is owed a shift's back pay, which puts them back to work.
            long owed = wage * (shifts > 0 ? shifts : pay.Unpaid > 0 ? 1 : 0);
            var bill = _lastBill.GetValueOrDefault(holder.Town);
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            string name = _residents.TryGetValue(id, out var r) ? r.Name : cold.Name;
            Log($"{name}: worked {shifts} shift(s) today, owed {owed}, hunger {cold.Medical?.Hunger ?? 0:0.00}{(pay.Unpaid > 0 ? $", unpaid {pay.Unpaid} day(s)" : "")}");

            if (owed > 0 && world.Player.Money >= owed)
            {
                world.Player.Money -= owed;
                if (world.Squads.Find(cold.SquadId) is { OwnPurse: true } home) home.Money += owed;
                if (pay.Unpaid > 0) TownOf(holder.Town).Tell(world.GameHours, $"{name} was paid and is back at work");
                pay.Unpaid = 0;
                _lastBill[holder.Town] = (bill.Paid + owed, bill.Unpaid);
            }
            else if (owed > 0 || pay.Unpaid > 0)
            {
                pay.Unpaid++;
                _lastBill[holder.Town] = (bill.Paid, bill.Unpaid + 1);
                if (pay.Unpaid >= 3)
                {
                    TownOf(holder.Town).Tell(world.GameHours, $"{name} left the town: unpaid three days running");
                    Log($"{name}: unpaid three days, leaving");
                    _holders.Remove(id);
                    _pay.Remove(id);
                    Evict(world, c);
                }
                else
                {
                    TownOf(holder.Town).Tell(world.GameHours, $"{name} went unpaid and stopped working");
                    Log($"{name}: unpaid ({pay.Unpaid} day(s)), owed {owed}, player has {world.Player.Money}");
                }
            }
        }
        PayRunners(world);
        PayGuards(world);
        foreach (var id in _pay.Keys.Where(k => !HasRole(k)).ToList()) _pay.Remove(id);
        foreach (var (town, (paid, unpaid)) in _lastBill) Log($"wages for {town}: {paid} cats paid, {unpaid} unpaid, player has {world.Player.Money}");
        _dirty = true;
    }

    /// <summary>
    /// The residents' posts, by lasting id (saved).
    /// </summary>
    readonly SortedDictionary<ulong, PostHolder> _holders = [];

    /// <summary>
    /// The hours each line of work keeps, by town key and line key; a line not listed keeps the default, day and night (saved).
    /// </summary>
    readonly SortedDictionary<string, Hours> _lineHours = new(StringComparer.Ordinal);

    /// <summary>
    /// A post: the furniture's building, what kind it is, and how many may work it on each shift (the data's max operators; a turret takes one).
    /// </summary>
    public readonly record struct WorkPost(BuildSite Site, PostKind Kind, int Places);

    /// <summary>
    /// A post's line of work: the building it stands in (or none, outside) and the furniture's name, so the same furniture in one building is one
    /// line on the work list.
    /// </summary>
    static string LineOf(BuildSite site) => $"{site.Parent ?? ""}|{site.Name}";

    /// <summary>
    /// The hours a line keeps.
    /// </summary>
    Hours HoursOf(string town, string line) => _lineHours.GetValueOrDefault(town + "#" + line, Hours.DayAndNight);

    /// <summary>
    /// The shift a new worker on a post of a line with <paramref name="hours"/> takes, or null when the post is full for those hours.
    /// </summary>
    Shift? FreeShift(WorkPost post, Hours hours, ulong except = 0) => hours switch
    {
        Hours.Day => HoldersOn(post.Site.Id, Shift.Day, except) < post.Places ? Shift.Day : null,
        Hours.Night => HoldersOn(post.Site.Id, Shift.Night, except) < post.Places ? Shift.Night : null,
        Hours.TwoShifts => HoldersOn(post.Site.Id, Shift.Day, except) < post.Places ? Shift.Day
            : HoldersOn(post.Site.Id, Shift.Night, except) < post.Places ? Shift.Night : null,
        _ => HoldersOn(post.Site.Id, Shift.Always, except) < post.Places ? Shift.Always : null,
    };

    /// <summary>
    /// Puts a worker on the first post of a line with a place free for its hours; false when the line is full.
    /// </summary>
    bool AddToLine(World world, PlayerTown town, string key, string line, ulong id, bool tell = true)
    {
        var hours = HoursOf(key, line);
        foreach (var post in WorkPosts(world, town.Number))
        {
            if (LineOf(post.Site) != line || FreeShift(post, hours, id) is not { } shift) continue;
            _holders[id] = new PostHolder(key, post.Site.Id, shift);
            if (tell) TownOf(key).Tell(world.GameHours, $"{_residents[id].Name} took the post at the {post.Site.Name} ({Names.Of(shift)})");
            Log($"{town.Name}: {_residents[id].Name} on the {post.Site.Name} ({post.Site.Id}), {Names.Of(shift)}");
            _dirty = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The residents of a town without a post, in lasting-id order (a lasting id of a living character).
    /// </summary>
    IEnumerable<ulong> IdleResidents(World world, string key) =>
        _residents.Where(p => p.Value.Town == key && !HasRole(p.Key) && !world.LastingIds.Find(new LastingId(p.Key)).IsNone).Select(p => p.Key);

    /// <summary>
    /// Whether a resident has work: a post, a guard's bed or the food runners.
    /// </summary>
    bool HasRole(ulong id) => _holders.ContainsKey(id) || IsGuard(id) || _runners.ContainsKey(id);

    /// <summary>
    /// Applies a line command; false when it is not one.
    /// </summary>
    bool ApplyLine(World world, SettlerCommand command)
    {
        int number = command switch { AddWorker a => a.Town, RemoveWorker r => r.Town, SetLineHours h => h.Town, PutIdleToWork p => p.Town, _ => -1 };
        if (number < 0) return false;
        if (world.Buildings.FindTown(number) is not { } town || !IsFiefdom(world, town) || KeyOf(town) is not { } key) return true;

        switch (command)
        {
            case AddWorker add:
                ulong id = add.Character.IsNone ? IdleResidents(world, key).FirstOrDefault() : ResidentLasting(world, add.Character);
                if (id == 0 || _residents[id].Town != key || IsGuard(id)) return true;
                if (_runners.ContainsKey(id)) StopRunner(world, id);
                _holders.Remove(id, out var before);
                if (!AddToLine(world, town, key, add.Line, id) && before is not null) _holders[id] = before;
                return true;
            case RemoveWorker remove:
                var last = _holders.Where(h => h.Value.Town == key && world.Buildings.Find(h.Value.Site) is { } s && LineOf(s) == remove.Line)
                    .Select(h => h.Key).DefaultIfEmpty(0UL).Last();
                if (last != 0 && _holders.Remove(last, out var was))
                {
                    TownOf(key).Tell(world.GameHours, $"{_residents[last].Name} left the post at the {NameOfSite(world, was.Site)}");
                    _dirty = true;
                }
                return true;
            case SetLineHours set:
                if (set.Hours == Hours.DayAndNight) _lineHours.Remove(key + "#" + set.Line);
                else _lineHours[key + "#" + set.Line] = set.Hours;
                var workers = _holders.Where(h => h.Value.Town == key && world.Buildings.Find(h.Value.Site) is { } ws && LineOf(ws) == set.Line)
                    .Select(h => h.Key).ToList();
                foreach (var w in workers) _holders.Remove(w);
                foreach (var w in workers)
                    if (!AddToLine(world, town, key, set.Line, w, tell: false))
                        TownOf(key).Tell(world.GameHours, $"{_residents[w].Name} has no post now: the new hours have no place for them");
                if (workers.Count > 0) TownOf(key).Tell(world.GameHours, $"The {set.Line[(set.Line.IndexOf('|') + 1)..]} work now keeps {set.Hours switch { Hours.Day => "day hours", Hours.Night => "night hours", Hours.TwoShifts => "two shifts", _ => "day and night" }}");
                Log($"{town.Name}: line {set.Line} keeps {set.Hours}; {workers.Count} worker(s) placed again");
                _dirty = true;
                return true;
            case PutIdleToWork:
                var lines = WorkPosts(world, town.Number).Select(p => LineOf(p.Site)).Distinct().ToList();
                foreach (var idle in IdleResidents(world, key).ToList())
                    foreach (var line in lines)
                        if (AddToLine(world, town, key, line, idle)) break;
                return true;
        }
        return true;
    }

    /// <summary>
    /// The complete posts of the player's in town <paramref name="town"/>, in the machine table's order: every machine people work and every gun
    /// turret, read from the furniture's own data, so furniture other mods add makes posts too.
    /// </summary>
    static IEnumerable<WorkPost> WorkPosts(World world, int town)
    {
        foreach (var m in world.Machines.All)
        {
            if (world.Buildings.Find(m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction || TownOf(world, site) != town) continue;
            if (m.Data.Function == ObjectRanges.TurretFunction) yield return new WorkPost(site, PostKind.Turret, 1);
            else if (m.Data.Function == ShopCounterFunction && m.Storage is not null && site.Parent is not null) yield return new WorkPost(site, PostKind.Counter, 1);
            else if (m.Data.Operable) yield return new WorkPost(site, PostKind.Machine, Math.Max(1, m.Data.MaxOperators));
        }
    }

    /// <summary>
    /// The post at <paramref name="site"/> in town <paramref name="town"/>, if it is one.
    /// </summary>
    static WorkPost? PostAt(World world, int town, string site)
    {
        foreach (var post in WorkPosts(world, town))
            if (post.Site.Id == site) return post;
        return null;
    }

    /// <summary>
    /// Whether a shift is working at <paramref name="hours"/> (game hours).
    /// </summary>
    public static bool OnShift(Shift shift, double hours)
    {
        double h = hours % 24;
        bool day = h >= DayShiftStarts && h < NightShiftStarts;
        return shift switch { Shift.Day => day, Shift.Night => !day, _ => true };
    }

    /// <summary>
    /// Whether a holder of <paramref name="shift"/> and one of <paramref name="other"/> work at the same time.
    /// </summary>
    static bool Overlap(Shift shift, Shift other) => shift == Shift.Always || other == Shift.Always || shift == other;

    /// <summary>
    /// How many holders of the post at <paramref name="site"/> work during <paramref name="shift"/>, leaving out <paramref name="except"/>.
    /// </summary>
    int HoldersOn(string site, Shift shift, ulong except = 0) =>
        _holders.Count(h => h.Key != except && h.Value.Site == site && Overlap(shift, h.Value.Shift));

    /// <summary>
    /// The lasting id of a living resident of the player's, or 0.
    /// </summary>
    ulong ResidentLasting(World world, CharacterId character)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(character, out int slot) || table.Cold(slot) is not { IsPlayer: true, Medical.Dead: false, LastingId.IsNone: false } cold)
            return 0;
        return _residents.ContainsKey(cold.LastingId.Value) ? cold.LastingId.Value : 0;
    }

    /// <summary>
    /// Applies a post command after checking it against the world; false when it is not one.
    /// </summary>
    bool ApplyPost(World world, SettlerCommand command)
    {
        switch (command)
        {
            case AssignPost assign:
                Assign(world, assign);
                return true;
            case ClearPost clear when ResidentLasting(world, clear.Character) is var id and not 0 && _holders.Remove(id, out var was):
                var r = _residents[id];
                TownOf(was.Town).Tell(world.GameHours, $"{r.Name} left the post at the {NameOfSite(world, was.Site)}");
                return true;
            case SetShift set when ResidentLasting(world, set.Character) is var id and not 0 && _holders.TryGetValue(id, out var holder)
                && world.Buildings.Find(holder.Site) is { } site && PostAt(world, TownOf(world, site), holder.Site) is { } post
                && HoldersOn(holder.Site, set.Shift, id) < post.Places:
                _holders[id] = holder with { Shift = set.Shift };
                return true;
            case ClearPost or SetShift:
                return true;
        }
        return false;
    }

    /// <summary>
    /// Puts a resident of the town on a post with a place free on the day shift (else the night shift); a resident who held another post leaves
    /// it.
    /// </summary>
    void Assign(World world, AssignPost assign)
    {
        if (world.Buildings.FindTown(assign.Town) is not { } town || !IsFiefdom(world, town) || KeyOf(town) is not { } key) return;
        ulong id = ResidentLasting(world, assign.Character);
        if (id == 0 || _residents[id].Town != key || IsGuard(id) || PostAt(world, town.Number, assign.Site) is not { } post) return;

        if (FreeShift(post, HoursOf(key, LineOf(post.Site)), id) is not { } s) return;

        _holders[id] = new PostHolder(key, post.Site.Id, s);
        TownOf(key).Tell(world.GameHours, $"{_residents[id].Name} took the post at the {post.Site.Name} ({Names.Of(s)})");
        Log($"{town.Name}: {_residents[id].Name} on the {post.Site.Name} ({post.Site.Id}), {Names.Of(s)}");
    }

    /// <summary>
    /// For testing: plans the furniture in every home of the town.
    /// </summary>
    void PlanInHomes(World world, PlayerTown town, PlanFurniture plan)
    {
        if (_construction is null || _data.Db.Find(plan.Record) is null) return;
        var plans = Homes(world, town).Select(h => new PlannedBuilding(plan.Record, h.Site.Position + plan.Offset, Quaternion.Identity, 0, h.Site.Id)).ToList();
        _construction.Inputs(world, [new PlaceBuildings(plans) { Tick = world.Tick }]);
        Log($"{town.Name}: planned {plans.Count} {plan.Record} in its homes at offset {plan.Offset} (testing)");
    }

    /// <summary>
    /// For testing: fills the town's first town-stock box with an item.
    /// </summary>
    void FillStock(World world, PlayerTown town, FillTownStock fill)
    {
        foreach (var (site, stock) in TownStock(world, town.Number))
        {
            int added = 0;
            for (int i = 0; i < fill.Count; i++)
                if (_data.Items.Create(fill.Record, 1) is { } item && stock.HasRoomFor(item) && stock.Add(item)) added++;
            world.Machines.Touch();
            Log($"{town.Name}: put {added} of {fill.Record} in town stock {site.Name} ({site.Id}) (testing)");
            return;
        }
        Log($"{town.Name}: no town-stock box to fill (testing)");
    }

    /// <summary>
    /// For testing: every resident of the town with no post takes the first post with a place free.
    /// </summary>
    void TakeOpenPosts(World world, PlayerTown town, TownState state)
    {
        var posts = WorkPosts(world, town.Number).ToList();
        int idle = 0;
        foreach (var (id, r) in _residents)
        {
            if (r.Town != state.Key || _holders.ContainsKey(id)) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone) continue;
            idle++;
            foreach (var post in posts)
            {
                if (HoldersOn(post.Site.Id, Shift.Day) >= post.Places && HoldersOn(post.Site.Id, Shift.Night) >= post.Places) continue;
                Assign(world, new AssignPost(town.Number, c, post.Site.Id) { Tick = world.Tick });
                break;
            }
        }
        Log($"{town.Name}: {posts.Count} post(s) ({string.Join(", ", posts.Select(p => $"{p.Site.Name} x{p.Places}"))}), {idle} resident(s) had none (testing)");
    }

    /// <summary>
    /// Keeps the game's job list in step with the posts: a holder on shift has the post's job, one off shift (or whose town is no longer a town)
    /// does not, so off shift the town's own day (home, meals, bed) runs. A holder who is no longer a resident, or whose post is gone, loses it.
    /// </summary>
    void StepPosts(World world)
    {
        DropStaleJobs(world);
        if (_production is null || _holders.Count == 0) return;

        var live = new Dictionary<string, PlayerTown>(StringComparer.Ordinal);
        foreach (var (town, state) in LiveTowns(world)) live[state.Key] = town;

        var table = world.Characters;
        double now = world.GameHours;
        foreach (var (id, holder) in _holders.ToList())
        {
            var c = world.LastingIds.Find(new LastingId(id));
            int slot = -1;
            var cold = !c.IsNone && table.TryResolveNext(c, out slot) ? table.Cold(slot) : null;
            bool resident = _residents.TryGetValue(id, out var r) && r.Town == holder.Town && cold is { IsPlayer: true, Medical.Dead: false };
            bool standing = live.TryGetValue(holder.Town, out var town) && PostAt(world, town.Number, holder.Site) is not null;
            if (!resident || !standing)
            {
                _holders.Remove(id);
                if (cold is { IsPlayer: true }) TakeJob(world, c, cold, holder.Site);
                if (resident && town is not null)
                {
                    TownOf(holder.Town).Tell(now, $"{r!.Name} has no post now: the {NameOfSite(world, holder.Site)} is gone");
                    Log($"{town.Name}: {r.Name} lost the post at {holder.Site} (gone)");
                }
                _dirty = true;
                continue;
            }

            bool has = cold!.Jobs.Any(j => j.Site == holder.Site);
            var pay = PayOf(id);

            // A hungry worker leaves the job until fed, so the town's own day gives them a meal; an unpaid one has stopped working.
            bool hungry = cold.Medical is { } medical && medical.Hunger < (pay.Eating ? Fed : Hungry);
            if (hungry != pay.Eating)
            {
                pay.Eating = hungry;
                Log($"{r!.Name}: {(hungry ? $"hungry ({cold.Medical!.Hunger:0.00}), off to eat" : $"fed ({cold.Medical!.Hunger:0.00}), back to work")}");
            }
            bool on = OnShift(holder.Shift, now) && !hungry && pay.Unpaid == 0;
            if (on && has) pay.Worked |= (byte)(OnShift(Shift.Day, now) ? 1 : 2);
            var post = PostAt(world, town!.Number, holder.Site)!.Value;
            if (on && !has && post.Kind == PostKind.Counter)
            {
                GiveMerchantJob(cold, holder.Site);
                Log($"{r!.Name}: on shift at the {post.Site.Name} ({Names.Of(holder.Shift)}), merchant");
            }
            else if (on && !has)
            {
                var type = post.Kind == PostKind.Turret ? TaskType.UseTurret : TaskType.OperateMachinery;
                _production.Inputs(world, [new AddJob([c], type, holder.Site) { Tick = world.Tick }]);
                Log($"{r!.Name}: on shift at the {post.Site.Name} ({Names.Of(holder.Shift)}), jobs {cold.Jobs.Count}");
            }
            else if (!on && has)
            {
                TakeJob(world, c, cold, holder.Site);
                Log($"{r!.Name}: off shift ({Names.Of(holder.Shift)}), jobs {cold.Jobs.Count}");
            }
        }
    }

    /// <summary>
    /// Takes a post's job off every resident who no longer works that post (taken off it, moved to another, or the line's hours changed), so no
    /// resident keeps a job the mod gave them for a post they have left. Every 30 ticks.
    /// </summary>
    void DropStaleJobs(World world)
    {
        if (_production is null || world.Tick % 30 != 0) return;
        var table = world.Characters;
        HashSet<string>? posts = null;
        foreach (var (id, r) in _residents)
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { IsPlayer: true, Jobs.Count: > 0 } cold) continue;
            posts ??= LiveTowns(world).SelectMany(t => WorkPosts(world, t.Town.Number)).Select(p => p.Site.Id).ToHashSet(StringComparer.Ordinal);
            string? mine = _holders.TryGetValue(id, out var h) ? h.Site : null;
            foreach (var site in cold.Jobs.Select(j => j.Site).Where(s => s is not null && s != mine && posts.Contains(s)).Distinct().ToList())
            {
                TakeJob(world, c, cold, site!);
                Log($"{r.Name}: dropped the job at {site} (no longer their post)");
            }
        }
    }

    /// <summary>
    /// Takes every job on <paramref name="site"/> off the character's list, through the production system's own command.
    /// </summary>
    void TakeJob(World world, CharacterId c, CharacterCold cold, string site)
    {
        if (cold.Running is { Row: (TaskType)MerchantRow } && world.Characters.TryResolveNext(c, out int slot)) TaskStart.End(world, slot, TaskEnd.Stopped);
        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
            if (cold.Jobs[i].Site == site) _production!.Inputs(world, [new RemoveJob(c, i) { Tick = world.Tick }]);
    }

    /// <summary>
    /// A building's name, or "building" when it is gone.
    /// </summary>
    static string NameOfSite(World world, string site) => world.Buildings.Find(site)?.Name ?? "building";
}
