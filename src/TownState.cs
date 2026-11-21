namespace Fiefdom;

/// <summary>
/// How fast settlers come, against the pull score's pace.
/// </summary>
public enum ArrivalPace : byte
{
    /// <summary>
    /// Half as often.
    /// </summary>
    Slow = 0,
    /// <summary>
    /// The pull score's pace.
    /// </summary>
    Normal = 1,
    /// <summary>
    /// Twice as often.
    /// </summary>
    Fast = 2,
}

/// <summary>
/// One Fiefdom town's own state, kept in the mod's save data under the town's key (the value of its tag on the outpost, which stays the same
/// across loads where the outpost's number does not): the player's settler settings, the steward, when the next group is due, and the town's
/// recent news. Changed only in the system's serial steps.
/// </summary>
public sealed class TownState
{
    /// <summary>
    /// The default population cap: a safety valve for performance, not a design limit.
    /// </summary>
    public const int DefaultCap = 60;

    /// <summary>
    /// The most lines the town's news keeps.
    /// </summary>
    public const int NewsLines = 12;

    /// <summary>
    /// The town's key (its tag's value).
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Whether settlers may come (Open) or not (Closed). Closed when the town is made.
    /// </summary>
    public bool Open { get; set; }

    /// <summary>
    /// The most residents the town takes.
    /// </summary>
    public int Cap { get; set; } = DefaultCap;

    /// <summary>
    /// How fast settlers come.
    /// </summary>
    public ArrivalPace Pace { get; set; } = ArrivalPace.Normal;

    /// <summary>
    /// Whether settlers the town's weather would hurt may come.
    /// </summary>
    public bool AllowHarmed { get; set; }

    /// <summary>
    /// The races the player keeps out (RACE string ids). Every race not here may settle, so races other mods add are welcome until the player
    /// says otherwise.
    /// </summary>
    public SortedSet<string> BlockedRaces { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The steward's lasting id (0: none).
    /// </summary>
    public ulong Steward { get; set; }

    /// <summary>
    /// The game hour the next group is due (NaN: not worked out yet).
    /// </summary>
    public double NextArrival { get; set; } = double.NaN;

    /// <summary>
    /// The game hour the next visitors are due (NaN: not worked out yet).
    /// </summary>
    public double NextVisit { get; set; } = double.NaN;

    /// <summary>
    /// The town's recent news, oldest first: who came and why, who left, who died.
    /// </summary>
    public List<string> News { get; } = [];

    /// <summary>
    /// The game hours at which residents of this town were killed, for the pull score (only the last few days are kept).
    /// </summary>
    public List<double> Killings { get; } = [];

    /// <summary>
    /// The game hours at which residents left the town hungry, for the pull score (only the last few days are kept).
    /// </summary>
    public List<double> Leavings { get; } = [];

    /// <summary>
    /// The races (people or animals) the town's guards attack on sight inside the town (RACE string ids).
    /// </summary>
    public SortedSet<string> AttackOnSight { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The races of everyone not of the player's faction seen in or near the town lately, with the game hour last seen (for the Policies tab).
    /// </summary>
    public SortedDictionary<string, double> Seen { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a line to the news, dropping the oldest past <see cref="NewsLines"/>.
    /// </summary>
    public void Tell(double hours, string line)
    {
        News.Add($"Day {DayOf(hours)}: {line}");
        if (News.Count > NewsLines) News.RemoveRange(0, News.Count - NewsLines);
    }

    /// <summary>
    /// The day number the game's clock shows for a game hour.
    /// </summary>
    public static int DayOf(double hours) => (int)Math.Floor(hours / 24);

    /// <summary>
    /// Writes the state.
    /// </summary>
    public void Write(BinaryWriter w)
    {
        w.Write(Key);
        w.Write(Open);
        w.Write(Cap);
        w.Write((byte)Pace);
        w.Write(AllowHarmed);
        w.Write(BlockedRaces.Count);
        foreach (string race in BlockedRaces) w.Write(race);
        w.Write(Steward);
        w.Write(NextArrival);
        w.Write(News.Count);
        foreach (string line in News) w.Write(line);
        w.Write(Killings.Count);
        foreach (double at in Killings) w.Write(at);
        w.Write(Leavings.Count);
        foreach (double at in Leavings) w.Write(at);
        w.Write(AttackOnSight.Count);
        foreach (string race in AttackOnSight) w.Write(race);
        w.Write(Seen.Count);
        foreach (var (race, at) in Seen)
        {
            w.Write(race);
            w.Write(at);
        }
        w.Write(NextVisit);
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote, in the save data's format <paramref name="version"/> (format 2 had no hungry leavings, formats before
    /// 7 no policies, before 8 no visits).
    /// </summary>
    public static TownState Read(BinaryReader r, int version)
    {
        var town = new TownState { Key = r.ReadString() };
        town.Open = r.ReadBoolean();
        town.Cap = r.ReadInt32();
        town.Pace = (ArrivalPace)r.ReadByte();
        town.AllowHarmed = r.ReadBoolean();
        for (int n = r.ReadInt32(); n > 0; n--) town.BlockedRaces.Add(r.ReadString());
        town.Steward = r.ReadUInt64();
        town.NextArrival = r.ReadDouble();
        for (int n = r.ReadInt32(); n > 0; n--) town.News.Add(r.ReadString());
        for (int n = r.ReadInt32(); n > 0; n--) town.Killings.Add(r.ReadDouble());
        if (version >= 3)
            for (int n = r.ReadInt32(); n > 0; n--) town.Leavings.Add(r.ReadDouble());
        if (version >= 7)
        {
            for (int n = r.ReadInt32(); n > 0; n--) town.AttackOnSight.Add(r.ReadString());
            for (int n = r.ReadInt32(); n > 0; n--) town.Seen[r.ReadString()] = r.ReadDouble();
        }
        if (version >= 8) town.NextVisit = r.ReadDouble();
        return town;
    }

    /// <summary>
    /// Adds the state to the world's state hash.
    /// </summary>
    public void Hash(ref Meitou.Simulation.StateHasher h)
    {
        h.Add(Meitou.Simulation.Rng.StableHash(Key));
        h.Add(Open ? 1 : 0);
        h.Add(Cap);
        h.Add((int)Pace);
        h.Add(AllowHarmed ? 1 : 0);
        foreach (string race in BlockedRaces) h.Add(Meitou.Simulation.Rng.StableHash(race));
        h.Add((long)Steward);
        if (!double.IsNaN(NextArrival)) h.Add((long)(NextArrival * 1000));
        if (!double.IsNaN(NextVisit)) h.Add((long)(NextVisit * 1000) + 7);
        h.Add(News.Count);
        h.Add(Killings.Count);
        h.Add(Leavings.Count);
        foreach (string race in AttackOnSight) h.Add(Meitou.Simulation.Rng.StableHash(race) + 1);
        h.Add(Seen.Count);
    }
}

/// <summary>
/// Where a group of would-be settlers is.
/// </summary>
public enum GroupStage : byte
{
    /// <summary>
    /// On its way to the town.
    /// </summary>
    Travelling = 0,
    /// <summary>
    /// At the town, waiting for the player's answer.
    /// </summary>
    Waiting = 1,
}

/// <summary>
/// A group the mod brought toward one of its towns: found again by its platoon's tag after a load.
/// </summary>
public sealed class SettlerGroup
{
    /// <summary>
    /// The group's number (never reused in a save), which its platoon's tag carries.
    /// </summary>
    public required int Serial { get; init; }

    /// <summary>
    /// The town's key.
    /// </summary>
    public required string Town { get; init; }

    /// <summary>
    /// The SQUAD_TEMPLATE it was made from.
    /// </summary>
    public required string Template { get; init; }

    /// <summary>
    /// The game hour it was made.
    /// </summary>
    public required double Since { get; init; }

    /// <summary>
    /// Where it is.
    /// </summary>
    public GroupStage Stage { get; set; }

    /// <summary>
    /// The game hour it reached the town (NaN while travelling).
    /// </summary>
    public double ArrivedAt { get; set; } = double.NaN;

    /// <summary>
    /// Whether the player accepted it while it was out of the world; it moves in once it is back.
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>
    /// How many walks toward the town its leader has been given.
    /// </summary>
    public int Walks { get; set; }

    /// <summary>
    /// Whether the group comes to visit (to shop, sit about and go on) rather than to settle.
    /// </summary>
    public bool Visiting { get; init; }

    /// <summary>
    /// A visiting group's own AI package, given back when the visit ends ("" until it arrives).
    /// </summary>
    public string Package { get; set; } = "";

    /// <summary>
    /// Whether a visiting group has been to the town's shop and done its buying.
    /// </summary>
    public bool Shopped { get; set; }

    /// <summary>
    /// The tag its platoon carries.
    /// </summary>
    public string Tag => $"{FiefdomSystem.ModId}:group:{Serial}";

    /// <summary>
    /// Writes the group.
    /// </summary>
    public void Write(BinaryWriter w)
    {
        w.Write(Serial);
        w.Write(Town);
        w.Write(Template);
        w.Write(Since);
        w.Write((byte)Stage);
        w.Write(ArrivedAt);
        w.Write(Accepted);
        w.Write(Walks);
        w.Write(Visiting);
        w.Write(Package);
        w.Write(Shopped);
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote, in the save data's format <paramref name="version"/> (before 8, only settlers).
    /// </summary>
    public static SettlerGroup Read(BinaryReader r, int version)
    {
        int serial = r.ReadInt32();
        string town = r.ReadString(), template = r.ReadString();
        double since = r.ReadDouble();
        var stage = (GroupStage)r.ReadByte();
        double arrived = r.ReadDouble();
        bool accepted = r.ReadBoolean();
        int walks = r.ReadInt32();
        bool visiting = version >= 8 && r.ReadBoolean();
        string package = version >= 8 ? r.ReadString() : "";
        bool shopped = version >= 9 && r.ReadBoolean();
        return new SettlerGroup
        {
            Serial = serial, Town = town, Template = template, Since = since, Stage = stage, ArrivedAt = arrived, Accepted = accepted, Walks = walks,
            Visiting = visiting, Package = package, Shopped = shopped,
        };
    }
}

/// <summary>
/// A resident: which town took them in, which home they live in, when they came, and their name for the news.
/// </summary>
public sealed record Resident(string Town, string Home, double Since, string Name);
