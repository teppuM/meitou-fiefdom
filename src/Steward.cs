using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;

namespace Fiefdom;

/// <summary>
/// A group at the gate as the steward or the player judges it.
/// </summary>
/// <param name="Size">How many would move in.</param>
/// <param name="FreeBeds">The town's free beds in its homes.</param>
/// <param name="Room">How many more residents the cap allows.</param>
/// <param name="BlockedRaces">The races among them the player keeps out (names).</param>
/// <param name="Harmed">The town's harmful weather that would hurt some of them (names).</param>
/// <param name="MoneyPerHead">The group's money over its size.</param>
/// <param name="Armed">Whether any of them carries a weapon in hand or on the back.</param>
/// <param name="BringsSkill">Whether one of them is better at a trade than every resident (always true while the town has no residents).</param>
/// <param name="Purse">The group's money.</param>
public sealed record Applicants(int Size, int FreeBeds, int Room, IReadOnlyList<string> BlockedRaces, IReadOnlyList<string> Harmed,
    long MoneyPerHead, bool Armed, bool BringsSkill, long Purse, int BedsNeeded, bool HasHome)
{
    /// <summary>
    /// Why the hard rules (homes, beds, cap) refuse them, or null when they allow it. Nobody, steward or player, takes in more than the beds and cap;
    /// robotic races need a home and room under the cap, not a bed.
    /// </summary>
    public string? HardRefusal => !HasHome ? "no home for residents"
        : BedsNeeded > FreeBeds ? (FreeBeds == 0 ? "no free bed" : $"only {FreeBeds} free bed{(FreeBeds == 1 ? "" : "s")} for {BedsNeeded}")
        : Size > Room ? "the town is at its cap" : null;

    /// <summary>
    /// The player's rules they break (a race kept out, weather that would hurt them), or null when they break none.
    /// </summary>
    public string? RuleBroken => BlockedRaces.Count > 0 ? $"{string.Join(", ", BlockedRaces)} kept out"
        : Harmed.Count > 0 ? $"the {string.Join(" and ", Harmed)} here would hurt them" : null;
}

/// <summary>
/// A steward's answer: whether the group moves in, the reason the news gives, what the steward says aloud, and any bribe taken from the group.
/// </summary>
public sealed record Verdict(bool Admit, string Reason, string Says, long Bribe = 0);

/// <summary>
/// How a steward judges a group, coloured by the steward's personality (the character's dialogue personality tag). Beds and the cap bind every
/// steward. Within them, an honourable, normal or brave steward keeps the player's rules exactly; the others bend them in their own way:
/// <list type="bullet">
/// <item>kind: takes in the poor and the hungry even of a race the player keeps out;</item>
/// <item>cruel: turns away anyone who cannot pay their way;</item>
/// <item>smart: takes only those who bring a trade the town lacks;</item>
/// <item>fearful: turns away armed strangers;</item>
/// <item>traitorous: lets in rule-breakers who pay a bribe, and keeps it;</item>
/// <item>dumb or maniacal: now and then gets it wrong either way.</item>
/// </list>
/// </summary>
public static class Steward
{
    /// <summary>
    /// The personality tags (docs: PersonalityTags).
    /// </summary>
    public const int Honourable = 1, Traitorous = 2, Smart = 5, Dumb = 6, Brave = 9, Fearful = 10, Kind = 11, Cruel = 12, Normal = 13, Maniacal = 14;

    /// <summary>
    /// The money per head below which a group counts as poor: a cruel steward turns it away and a kind one takes it in.
    /// </summary>
    public const long PoorPerHead = 100;

    /// <summary>
    /// The bribe per head a traitorous steward asks to let in a group that breaks the player's rules.
    /// </summary>
    public const long BribePerHead = 100;

    /// <summary>
    /// One chance in this that a dumb or maniacal steward gets a decision wrong.
    /// </summary>
    public const int MistakeOdds = 4;

    /// <summary>
    /// A personality's name for the window.
    /// </summary>
    public static string NameOf(int personality) => personality switch
    {
        Honourable => "honourable",
        Traitorous => "traitorous",
        Smart => "smart",
        Dumb => "dumb",
        Brave => "brave",
        Fearful => "fearful",
        Kind => "kind",
        Cruel => "cruel",
        Normal => "normal",
        Maniacal => "maniacal",
        _ => "plain",
    };

    /// <summary>
    /// What a personality does with settlers, for the window.
    /// </summary>
    public static string Describe(int personality) => personality switch
    {
        Kind => "Keeps your rules, but takes in the poor and hungry even of a race you keep out.",
        Cruel => "Keeps your rules, and turns away anyone who can't pay their way.",
        Smart => "Keeps your rules, and takes only people who bring a trade the town lacks.",
        Fearful => "Keeps your rules, and turns away armed strangers.",
        Traitorous => "Keeps your rules, unless a group pays them to look away.",
        Dumb or Maniacal => "Tries to keep your rules, but gets it wrong now and then.",
        _ => "Keeps your rules exactly.",
    };

    /// <summary>
    /// The steward's verdict on a group. <paramref name="roll"/> is the group's own random draw, for a dumb or maniacal steward's mistakes.
    /// </summary>
    public static Verdict Judge(int personality, Applicants a, ulong roll)
    {
        if (a.HardRefusal is { } hard) return new Verdict(false, hard, "We've no room for you.");

        string? broken = a.RuleBroken;
        bool poor = a.MoneyPerHead < PoorPerHead;
        switch (personality)
        {
            case Kind when broken is not null && poor && a.Harmed.Count == 0:
                return new Verdict(true, $"taken in out of kindness ({broken})", "You look like you need a roof. Come in.");
            case Cruel when broken is null && poor:
                return new Verdict(false, "too poor to pay their way", "No coin, no home. Move along.");
            case Smart when broken is null && !a.BringsSkill:
                return new Verdict(false, "they bring no trade the town lacks", "We've got enough of your sort.");
            case Fearful when broken is null && a.Armed:
                return new Verdict(false, "they came armed", "I don't like the look of those weapons.");
            case Traitorous when broken is not null && a.Purse >= BribePerHead * a.Size:
                return new Verdict(true, $"let in for a bribe ({broken})", "Nobody needs to know about this.", BribePerHead * a.Size);
            case Dumb or Maniacal when Rng.Int(roll, MistakeOdds) == 0:
                return broken is null
                    ? new Verdict(false, "turned away by mistake", "Er... no. No room. I think.")
                    : new Verdict(true, $"let in by mistake ({broken})", "Sure, come in, why not.");
        }

        return broken is null
            ? new Verdict(true, "they meet your rules", "Welcome. Find yourselves a bed.")
            : new Verdict(false, broken, "Sorry. Town rules.");
    }

    /// <summary>
    /// The trade skills a town wants: work, making, healing, farming and cooking.
    /// </summary>
    public static readonly StatsEnumerated[] Trades =
    [
        StatsEnumerated.Labouring, StatsEnumerated.Science, StatsEnumerated.Engineering, StatsEnumerated.Robotics, StatsEnumerated.SmithingWeapon,
        StatsEnumerated.SmithingArmour, StatsEnumerated.SmithingBow, StatsEnumerated.Medic, StatsEnumerated.Turrets, StatsEnumerated.Farming,
        StatsEnumerated.Cooking,
    ];
}
