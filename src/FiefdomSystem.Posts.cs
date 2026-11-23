using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
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

    /// <summary>A food runner (the mod's job: carries meals from town stock to hungry residents and guards).</summary>
    Runner,

    /// <summary>A building another code mod offers one of its jobs on, for people (the game's job offers): the holder gets that job there.</summary>
    Offer,
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
/// A resident's post: the town, the furniture's building id, the shift, and the offered job when the post is another mod's job on that building
/// (0 for the furniture's own job: operating a machine, a turret, a counter).
/// </summary>
public sealed record PostHolder(string Town, string Site, Shift Shift, int Offer = 0);

/// <summary>
/// A job Fiefdom put on a townsperson's job list: its type and building. Fiefdom only ever takes off the jobs it gave, so a job the player or
/// another mod gave the same person stays.
/// </summary>
public readonly record struct GivenJob(TaskType Type, string Site);

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
public sealed record AssignPost(int Town, CharacterId Character, string Site, int Offer = 0) : SettlerCommand;

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
    /// Merchants are paid no wage: they live on their share of the takings (<see cref="PayShares"/>).
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
            if (IsMerchant(world, holder))
            {
                pay.Unpaid = 0;
                continue;
            }

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
    /// A post: the furniture's building, what kind it is, how many may work it on each shift (the data's max operators; a turret takes one), the
    /// offered job for another mod's work (0 for the furniture's own), and the kind of work its holder also covers elsewhere in town while on
    /// shift (null for none; see <see cref="CoverOf"/>).
    /// </summary>
    public readonly record struct WorkPost(BuildSite Site, PostKind Kind, int Places, int Offer = 0, string? Cover = null)
    {
        /// <summary>
        /// The order that gives a holder of this post its job on the building.
        /// </summary>
        public TaskType Order => Kind switch
        {
            PostKind.Turret => TaskType.UseTurret,
            PostKind.Counter => (TaskType)MerchantRow,
            PostKind.Offer => (TaskType)Offer,
            _ => TaskType.OperateMachinery,
        };

        /// <summary>
        /// The job type the list then holds: the game stores some orders as another type (a turret's as its man-a-turret job).
        /// </summary>
        public TaskType Job => PermaJobs.JobOf(Order) ?? Order;

        /// <summary>
        /// Whether <paramref name="holder"/> holds this post.
        /// </summary>
        public bool HeldBy(PostHolder holder) => holder.Site == Site.Id && holder.Offer == Offer;
    }

    /// <summary>
    /// A post's line of work: the building it stands in (or none, outside) and the furniture's name, so the same furniture in one building is one
    /// line on the work list; another mod's job on it is a line of its own, named with the job's caption.
    /// </summary>
    static string LineOf(BuildSite site, int offer = 0) =>
        offer != 0 ? $"{site.Parent ?? ""}|{site.Name}: {CaptionOf(offer)}" : $"{site.Parent ?? ""}|{site.Name}";

    /// <summary>
    /// An offered job's caption, or its number when no mod offers it any more (its holders lose the post at the next step).
    /// </summary>
    static string CaptionOf(int offer) => JobOffers.Of(offer)?.Caption ?? $"job {offer}";

    /// <summary>
    /// The line of work of a post.
    /// </summary>
    static string LineOf(WorkPost post) => LineOf(post.Site, post.Offer);

    /// <summary>
    /// The line of work a holder's post belongs to, or null when its building is gone.
    /// </summary>
    static string? LineOf(World world, PostHolder holder) => world.Buildings.Find(holder.Site) is { } site ? LineOf(site, holder.Offer) : null;

    /// <summary>
    /// The kind of work a machine's holder covers beyond their own post: any farm for a farmer (every crop is its own machine type), else the
    /// machine type (the functionality record, which a machine's tiers share: Well, Well II and Well III are one); null for a machine without one.
    /// </summary>
    static string? CoverOf(MachineData data) =>
        data.Farm is not null ? "farm" : data.Functionality is { } functionality ? "machine:" + functionality : null;

    /// <summary>
    /// The hours a line keeps.
    /// </summary>
    Hours HoursOf(string town, string line) => _lineHours.GetValueOrDefault(town + "#" + line, Hours.DayAndNight);

    /// <summary>
    /// The shift a new worker on a post of a line with <paramref name="hours"/> takes, or null when the post is full for those hours.
    /// </summary>
    Shift? FreeShift(WorkPost post, Hours hours, ulong except = 0) => hours switch
    {
        Hours.Day => HoldersOn(post, Shift.Day, except) < post.Places ? Shift.Day : null,
        Hours.Night => HoldersOn(post, Shift.Night, except) < post.Places ? Shift.Night : null,
        Hours.TwoShifts => HoldersOn(post, Shift.Day, except) < post.Places ? Shift.Day
            : HoldersOn(post, Shift.Night, except) < post.Places ? Shift.Night : null,
        _ => HoldersOn(post, Shift.Always, except) < post.Places ? Shift.Always : null,
    };

    /// <summary>
    /// Puts a worker on the first post of a line with a place free for its hours; false when the line is full.
    /// </summary>
    bool AddToLine(World world, PlayerTown town, string key, string line, ulong id, bool tell = true)
    {
        var hours = HoursOf(key, line);
        foreach (var post in PostsOf(world, town.Number))
        {
            if (LineOf(post) != line || FreeShift(post, hours, id) is not { } shift) continue;
            DropGiven(world, id);
            _holders[id] = new PostHolder(key, post.Site.Id, shift, post.Offer);
            if (tell) TownOf(key).Tell(world.GameHours, $"{_residents[id].Name} took the post at the {NameOf(post)} ({Names.Of(shift)})");
            Log($"{town.Name}: {_residents[id].Name} on the {NameOf(post)} ({post.Site.Id}), {Names.Of(shift)}");
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
                var last = _holders.Where(h => h.Value.Town == key && LineOf(world, h.Value) == remove.Line)
                    .Select(h => h.Key).DefaultIfEmpty(0UL).Last();
                if (last != 0 && _holders.Remove(last, out var was))
                {
                    TownOf(key).Tell(world.GameHours, $"{_residents[last].Name} left the post at the {NameOfPost(world, was)}");
                    DropGiven(world, last);
                    _dirty = true;
                }
                return true;
            case SetLineHours set:
                if (set.Hours == Hours.DayAndNight) _lineHours.Remove(key + "#" + set.Line);
                else _lineHours[key + "#" + set.Line] = set.Hours;
                var workers = _holders.Where(h => h.Value.Town == key && LineOf(world, h.Value) == set.Line)
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
                var lines = PostsOf(world, town.Number).Select(LineOf).Distinct().ToList();
                foreach (var idle in IdleResidents(world, key).ToList())
                    foreach (var line in lines)
                        if (AddToLine(world, town, key, line, idle)) break;
                return true;
        }
        return true;
    }

    /// <summary>
    /// Each town's posts as last read (<see cref="PostsOf"/>), and the tick they were read on.
    /// </summary>
    readonly Dictionary<int, List<WorkPost>> _posts = [];

    /// <summary>
    /// The tick and building revision <see cref="_posts"/> was read at; read again when either moves.
    /// </summary>
    (long Tick, long Revision) _postsAt = (-1, -1);

    /// <summary>
    /// The complete posts of the player's in town <paramref name="town"/>, in the machine table's order: every machine people work and every gun
    /// turret, read from the furniture's own data, so furniture other mods add makes posts too; then each job another mod offers to people on a
    /// building of the town, one post per building and job. Another mod's job covers no other building: the game keeps one job of a mod's type per
    /// character, so a second building would move the job, not add one (docs/modding.md "Mod jobs").
    /// </summary>
    static IEnumerable<WorkPost> WorkPosts(World world, int town)
    {
        foreach (var m in world.Machines.All)
        {
            if (world.Buildings.Find(m.Site) is not { Complete: true } site || site.Owner != world.Player.Faction || TownOf(world, site) != town) continue;
            if (m.Data.Function == ObjectRanges.TurretFunction) yield return new WorkPost(site, PostKind.Turret, 1);
            else if (m.Data.Function == ShopCounterFunction && m.Storage is not null) yield return new WorkPost(site, PostKind.Counter, 1);
            else if (m.Data.Operable) yield return new WorkPost(site, PostKind.Machine, Math.Max(1, m.Data.MaxOperators), Cover: CoverOf(m.Data));
        }

        var offers = JobOffers.All();
        if (offers.Count == 0) yield break;

        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Owner != world.Player.Faction || !JobOffers.OnAny(site.Record) || TownOf(world, site) != town) continue;
            foreach (var offer in offers)
                if (offer.Buildings.Contains(site.Record)) yield return new WorkPost(site, PostKind.Offer, 1, offer.Type);
        }
    }

    /// <summary>
    /// The town's posts (<see cref="WorkPosts"/>), read once a tick (again if a building changes within it) and shared by every caller. The list
    /// is not to be changed.
    /// </summary>
    List<WorkPost> PostsOf(World world, int town)
    {
        (long, long) at = (world.Tick, world.Buildings.Revision);
        if (_postsAt != at)
        {
            _posts.Clear();
            _postsAt = at;
        }

        if (!_posts.TryGetValue(town, out var posts)) _posts[town] = posts = [.. WorkPosts(world, town)];
        return posts;
    }

    /// <summary>
    /// The post at <paramref name="site"/> in town <paramref name="town"/> giving offered job <paramref name="offer"/> (0 for the furniture's own
    /// job), if it is one.
    /// </summary>
    WorkPost? PostAt(World world, int town, string site, int offer = 0)
    {
        foreach (var post in PostsOf(world, town))
            if (post.Site.Id == site && post.Offer == offer) return post;
        return null;
    }

    /// <summary>
    /// A post's name for the news: the furniture's, with another mod's job named after it.
    /// </summary>
    static string NameOf(WorkPost post) => post.Offer != 0 ? $"{post.Site.Name} ({CaptionOf(post.Offer)})" : post.Site.Name;

    /// <summary>
    /// The name of a holder's post for the news, or "building" when it is gone.
    /// </summary>
    static string NameOfPost(World world, PostHolder holder) =>
        holder.Offer != 0 ? $"{NameOfSite(world, holder.Site)} ({CaptionOf(holder.Offer)})" : NameOfSite(world, holder.Site);

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
    /// How many holders of <paramref name="post"/> work during <paramref name="shift"/>, leaving out <paramref name="except"/>.
    /// </summary>
    int HoldersOn(WorkPost post, Shift shift, ulong except = 0)
    {
        int count = 0;
        foreach (var (id, holder) in _holders)
            if (id != except && post.HeldBy(holder) && Overlap(shift, holder.Shift)) count++;
        return count;
    }

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
                TownOf(was.Town).Tell(world.GameHours, $"{r.Name} left the post at the {NameOfPost(world, was)}");
                DropGiven(world, id);
                return true;
            case SetShift set when ResidentLasting(world, set.Character) is var id and not 0 && _holders.TryGetValue(id, out var holder)
                && world.Buildings.Find(holder.Site) is { } site && PostAt(world, TownOf(world, site), holder.Site, holder.Offer) is { } post
                && HoldersOn(post, set.Shift, id) < post.Places:
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
        if (id == 0 || _residents[id].Town != key || IsGuard(id) || PostAt(world, town.Number, assign.Site, assign.Offer) is not { } post) return;

        if (FreeShift(post, HoursOf(key, LineOf(post)), id) is not { } s) return;

        DropGiven(world, id);
        _holders[id] = new PostHolder(key, post.Site.Id, s, post.Offer);
        TownOf(key).Tell(world.GameHours, $"{_residents[id].Name} took the post at the {NameOf(post)} ({Names.Of(s)})");
        Log($"{town.Name}: {_residents[id].Name} on the {NameOf(post)} ({post.Site.Id}), {Names.Of(s)}");
    }

    /// <summary>
    /// For testing: plans the furniture in every home of the town.
    /// </summary>
    void PlanInHomes(World world, PlayerTown town, PlanFurniture plan)
    {
        if (_construction is null || _data.Db.Find(plan.Record) is null) return;
        var plans = Homes(world, town).Select(h => Plans.Make(plan.Record, h.Site.Position + plan.Offset, Quaternion.Identity, 0, h.Site.Id)).ToList();
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
        var posts = PostsOf(world, town.Number);
        int idle = 0;
        foreach (var (id, r) in _residents)
        {
            if (r.Town != state.Key || _holders.ContainsKey(id)) continue;
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone) continue;
            idle++;
            foreach (var post in posts)
            {
                if (HoldersOn(post, Shift.Day) >= post.Places && HoldersOn(post, Shift.Night) >= post.Places) continue;
                Assign(world, new AssignPost(town.Number, c, post.Site.Id, post.Offer) { Tick = world.Tick });
                break;
            }
        }
        Log($"{town.Name}: {posts.Count} post(s) ({string.Join(", ", posts.Select(p => $"{p.Site.Name} x{p.Places}"))}), {idle} resident(s) had none (testing)");
    }

    /// <summary>
    /// Keeps the game's job list in step with the posts: a holder on shift has the post's job, one off shift (or whose town is no longer a town)
    /// does not, so off shift the town's own day (home, meals, bed) runs. A holder who is no longer a resident, or whose post is gone, loses it.
    /// Runs with the settler step, once a game second.
    /// </summary>
    void StepPosts(World world)
    {
        if (_production is null) return;
        if (_adoptJobs) AdoptJobs(world);

        var live = new Dictionary<string, PlayerTown>(StringComparer.Ordinal);
        foreach (var (town, state) in LiveTowns(world)) live[state.Key] = town;

        foreach (var (id, holder) in _holders.ToList()) StepHolder(world, id, holder, live);
        DropLeftovers(world);
        PayShares(world);
    }

    /// <summary>
    /// One holder's step: loses the post when no longer a resident or the post is gone, leaves it to eat when hungry, and otherwise keeps the jobs
    /// the post wants at this hour.
    /// </summary>
    void StepHolder(World world, ulong id, PostHolder holder, Dictionary<string, PlayerTown> live)
    {
        var table = world.Characters;
        var c = world.LastingIds.Find(new LastingId(id));
        var cold = !c.IsNone && table.TryResolveNext(c, out int slot) ? table.Cold(slot) : null;
        bool resident = _residents.TryGetValue(id, out var r) && r.Town == holder.Town && cold is { IsPlayer: true, Medical.Dead: false };
        var town = live.GetValueOrDefault(holder.Town);
        if (!resident || town is null || PostAt(world, town.Number, holder.Site, holder.Offer) is not { } post)
        {
            LosePost(world, id, holder, resident ? r : null, town);
            return;
        }

        double now = world.GameHours;
        var pay = PayOf(id);
        bool hungry = TooHungry(cold!, pay, r!.Name);
        bool on = OnShift(holder.Shift, now) && !hungry && pay.Unpaid == 0;
        var own = new GivenJob(post.Job, holder.Site);
        bool had = Has(cold!, own);
        if (on && had) pay.Worked |= (byte)(OnShift(Shift.Day, now) ? 1 : 2);

        WantedJobs(world, town.Number, post, on);
        KeepJobs(world, id, c, cold!);
        bool has = Has(cold!, own);
        if (on && !had && has) Log($"{r.Name}: on shift at the {NameOf(post)} ({Names.Of(holder.Shift)}), jobs {cold!.Jobs.Count}");
        else if (!on && had) Log($"{r.Name}: off shift ({Names.Of(holder.Shift)}), jobs {cold!.Jobs.Count}");

        if (on && !has && _refused.Add(id)) Log($"{r.Name}: could not take the job at the {NameOf(post)}; jobs {cold!.Jobs.Count}");
        else if (has) _refused.Remove(id);
    }

    /// <summary>
    /// A holder who is no longer a resident of the post's town, or whose post is gone: the post and the jobs it gave go, with a line in the news
    /// when a resident lost it.
    /// </summary>
    void LosePost(World world, ulong id, PostHolder holder, Resident? resident, PlayerTown? town)
    {
        _holders.Remove(id);
        DropGiven(world, id);
        _dirty = true;
        if (resident is null || town is null) return;

        TownOf(holder.Town).Tell(world.GameHours, $"{resident.Name} has no post now: the {NameOfPost(world, holder)} is gone");
        Log($"{town.Name}: {resident.Name} lost the post at {holder.Site} (gone)");
    }

    /// <summary>
    /// Whether a worker is too hungry to work: below <see cref="Hungry"/> they leave the job to eat, so the town's own day gives them a meal, and
    /// go back once at <see cref="Fed"/>.
    /// </summary>
    static bool TooHungry(CharacterCold cold, PayState pay, string name)
    {
        bool hungry = cold.Medical is { } medical && medical.Hunger < (pay.Eating ? Fed : Hungry);
        if (hungry == pay.Eating) return hungry;

        pay.Eating = hungry;
        string why = hungry ? $"hungry ({cold.Medical!.Hunger:0.00}), off to eat" : $"fed ({cold.Medical!.Hunger:0.00}), back to work";
        Log($"{name}: {why}");
        return hungry;
    }

    #region Jobs Fiefdom gives

    /// <summary>
    /// The jobs Fiefdom put on each townsperson's job list, by lasting id, in the order given (saved). Only these are ever taken off again.
    /// </summary>
    readonly SortedDictionary<ulong, List<GivenJob>> _given = [];

    /// <summary>
    /// The jobs one holder should have now, each with the order that gives it (<see cref="WantedJobs"/>), reused for each holder in turn.
    /// </summary>
    readonly List<(GivenJob Job, TaskType Order)> _wanted = [];

    /// <summary>
    /// Holders whose post's job did not go on their list (the game refused it, or they hold another of that mod job's type), so the log says so
    /// once rather than every step (not saved).
    /// </summary>
    readonly HashSet<ulong> _refused = [];

    /// <summary>
    /// Set when a save from before jobs were tracked is read: the next step takes the townspeople's post jobs as Fiefdom's
    /// (<see cref="AdoptJobs"/>).
    /// </summary>
    bool _adoptJobs;

    /// <summary>
    /// Fills <see cref="_wanted"/> with the jobs a holder of <paramref name="post"/> should have: none off shift; on shift the post's own job, then
    /// the same kind of work on every other post of the town (<see cref="WorkPost.Cover"/>), as a player's worker with several jobs has them. The
    /// job list runs the first job that has work, so a worker whose own post has nothing to do moves on to the next instead of standing idle.
    /// </summary>
    void WantedJobs(World world, int town, WorkPost post, bool on)
    {
        _wanted.Clear();
        if (!on) return;

        _wanted.Add((new GivenJob(post.Job, post.Site.Id), post.Order));
        if (post.Cover is null) return;

        foreach (var other in PostsOf(world, town))
            if (other.Cover == post.Cover && other.Site.Id != post.Site.Id) _wanted.Add((new GivenJob(other.Job, other.Site.Id), other.Order));
    }

    /// <summary>
    /// Brings a townsperson's job list in step with <see cref="_wanted"/>: takes off each job Fiefdom gave that is no longer wanted, and gives each
    /// wanted job the list lacks. A wanted job already on the list that Fiefdom did not give (the player's, another mod's) is left as it is and
    /// is never counted as Fiefdom's. Nor is another mod's job given to someone holding one of its type that Fiefdom did not give: the game
    /// keeps one per type and would move theirs.
    /// </summary>
    void KeepJobs(World world, ulong id, CharacterId c, CharacterCold cold)
    {
        if (_given.TryGetValue(id, out var given))
            for (int i = given.Count - 1; i >= 0; i--)
            {
                if (IsWanted(given[i])) continue;
                TakeGiven(world, c, cold, given[i]);
                given.RemoveAt(i);
            }

        foreach (var (job, order) in _wanted)
        {
            if (Has(cold, job) || HoldsOthers(cold, job, given)) continue;
            Give(world, c, cold, job, order);
            if (!Has(cold, job)) continue;
            given ??= _given[id] = [];
            if (!given.Contains(job)) given.Add(job);
        }

        if (given is { Count: 0 }) _given.Remove(id);
    }

    /// <summary>
    /// Whether <paramref name="job"/> is among <see cref="_wanted"/>.
    /// </summary>
    bool IsWanted(GivenJob job)
    {
        foreach (var (wanted, _) in _wanted)
            if (wanted == job) return true;
        return false;
    }

    /// <summary>
    /// Whether the list holds a job of another mod's offered type (<paramref name="job"/>'s) on another building, one Fiefdom did not give.
    /// </summary>
    static bool HoldsOthers(CharacterCold cold, GivenJob job, List<GivenJob>? given)
    {
        if (JobOffers.Of((int)job.Type) is null) return false;

        foreach (var j in cold.Jobs)
        {
            if (j.Type != job.Type || j.Site == job.Site) continue;
            if (given is null || !given.Contains(new GivenJob(j.Type, j.Site ?? ""))) return true;
        }
        return false;
    }

    /// <summary>
    /// Takes every job Fiefdom gave a townsperson off their list: on losing or leaving a post, becoming a guard or a food runner.
    /// </summary>
    void DropGiven(World world, ulong id)
    {
        if (!_given.Remove(id, out var given)) return;

        var c = world.LastingIds.Find(new LastingId(id));
        if (c.IsNone || !world.Characters.TryResolveNext(c, out int slot) || world.Characters.Cold(slot) is not { } cold) return;
        foreach (var job in given) TakeGiven(world, c, cold, job);
    }

    /// <summary>
    /// Townspeople holding no post who still have jobs Fiefdom gave (left a post, became a guard or a runner, were evicted): those jobs go.
    /// </summary>
    void DropLeftovers(World world)
    {
        if (_given.Count == 0) return;

        foreach (ulong id in _given.Keys.Where(k => !_holders.ContainsKey(k)).ToList()) DropGiven(world, id);
    }

    /// <summary>
    /// Whether the job list holds <paramref name="job"/>.
    /// </summary>
    static bool Has(CharacterCold cold, GivenJob job)
    {
        foreach (var j in cold.Jobs)
            if (j.Type == job.Type && j.Site == job.Site) return true;
        return false;
    }

    /// <summary>
    /// Gives a job through the game's own command (the merchant's, which is Fiefdom's own, straight onto the list).
    /// </summary>
    void Give(World world, CharacterId c, CharacterCold cold, GivenJob job, TaskType order)
    {
        if (order == (TaskType)MerchantRow) GiveMerchantJob(cold, job.Site);
        else _production!.Inputs(world, [new AddJob([c], order, job.Site) { Tick = world.Tick }]);
    }

    /// <summary>
    /// Takes one job Fiefdom gave off the list through the production system's own command, stopping a merchant mid-trip first. A job the list no
    /// longer holds (taken off by something else) is left alone.
    /// </summary>
    void TakeGiven(World world, CharacterId c, CharacterCold cold, GivenJob job)
    {
        if (job.Type == (TaskType)MerchantRow && cold.Running is { Row: (TaskType)MerchantRow } && world.Characters.TryResolveNext(c, out int slot))
            TaskStart.End(world, slot, TaskEnd.Stopped);

        for (int i = cold.Jobs.Count - 1; i >= 0; i--)
        {
            if (cold.Jobs[i].Type != job.Type || cold.Jobs[i].Site != job.Site) continue;
            _production?.Inputs(world, [new RemoveJob(c, i) { Tick = world.Tick }]);
            return;
        }
    }

    /// <summary>
    /// For a save from before Fiefdom tracked its jobs: every job a resident has on one of the town posts' buildings, of the kind Fiefdom gives
    /// there (operating, a turret, a counter), is taken as Fiefdom's, which is what the old step assumed; the next steps then keep them as any
    /// other. Runs once, on the first step after such a load.
    /// </summary>
    void AdoptJobs(World world)
    {
        _adoptJobs = false;
        var posts = new HashSet<GivenJob>();
        foreach (var (town, _) in LiveTowns(world))
            foreach (var post in PostsOf(world, town.Number))
                if (post.Kind != PostKind.Offer) posts.Add(new GivenJob(post.Job, post.Site.Id));

        var table = world.Characters;
        foreach (var id in _residents.Keys)
        {
            var c = world.LastingIds.Find(new LastingId(id));
            if (c.IsNone || !table.TryResolveNext(c, out int slot) || table.Cold(slot) is not { Jobs.Count: > 0 } cold) continue;
            foreach (var j in cold.Jobs)
                if (j.Site is { } site && posts.Contains(new GivenJob(j.Type, site))) Adopt(id, new GivenJob(j.Type, site));
        }

        Log($"jobs taken as Fiefdom's from an older save: {_given.Values.Sum(g => g.Count)}");
    }

    /// <summary>
    /// Counts a job already on a townsperson's list as one Fiefdom gave.
    /// </summary>
    void Adopt(ulong id, GivenJob job)
    {
        var given = _given.TryGetValue(id, out var list) ? list : _given[id] = [];
        if (!given.Contains(job)) given.Add(job);
    }

    #endregion

    /// <summary>
    /// A building's name, or "building" when it is gone.
    /// </summary>
    static string NameOfSite(World world, string site) => world.Buildings.Find(site)?.Name ?? "building";
}
