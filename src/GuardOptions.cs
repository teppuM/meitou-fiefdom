using System.Reflection;
using Meitou.Simulation;
using Meitou.Simulation.Factions;

namespace Fiefdom;

/// <summary>
/// Guard squads' own squad AI options: guards do not sit on residents' chairs when idle, go to bed in residents' beds when injured, or put
/// what they carry into the town's storage, whatever the player's AI tab says; they keep feeding animals, sharing food, and healing, helping
/// and rescuing allies. Uses the game's per-squad options (<c>SetSquadOptions</c>, MeitouClient 0.22.2-nightly.20261124 on), found by
/// reflection so the mod still loads on an older game, where guards follow the player's options as before. A squad given options keeps them
/// in the save, so this runs once per guard squad: when it is made, and for squads from older saves the first time they are seen.
/// </summary>
static class GuardOptions
{
    /// <summary>
    /// The game's <c>SquadAi</c> flags the guards keep: Heal (8), Help (16), Rescue (32), Share (64) and Feed (128). Left out: Ditch (1),
    /// Sleep (2), Sit (4), Shoot First (256), Heal Neutrals (512) and Heal All (1024).
    /// </summary>
    const ushort Kept = 8 | 16 | 32 | 64 | 128;

    /// <summary>
    /// The game's <c>SetSquadOptions</c> command type, null on a game without it.
    /// </summary>
    static readonly Type? s_command = typeof(SquadCommand).Assembly.GetType("Meitou.Simulation.Factions.SetSquadOptions");

    /// <summary>
    /// The game's <c>SquadAi</c> flags type.
    /// </summary>
    static readonly Type? s_flags = typeof(SquadCommand).Assembly.GetType("Meitou.Simulation.Player.SquadAi");

    /// <summary>
    /// <c>Squad.OwnOptions</c>, as a property or a field.
    /// </summary>
    static readonly MemberInfo? s_own = (MemberInfo?)typeof(Squad).GetProperty("OwnOptions") ?? typeof(Squad).GetField("OwnOptions");

    /// <summary>
    /// Whether this game has per-squad options.
    /// </summary>
    public static bool Available => s_command is not null && s_flags is not null && s_own is not null;

    /// <summary>
    /// Whether a squad has no options of its own yet.
    /// </summary>
    public static bool Unset(Squad squad) => s_own switch
    {
        PropertyInfo p => p.GetValue(squad) is null,
        FieldInfo f => f.GetValue(squad) is null,
        _ => false,
    };

    /// <summary>
    /// The command giving <paramref name="squad"/> the guards' options, owned by <paramref name="owner"/>; null on a game without them.
    /// </summary>
    public static SimCommand? Command(int squad, string owner, long tick)
    {
        if (!Available) return null;

        var command = (SimCommand)Activator.CreateInstance(s_command!, squad, Enum.ToObject(s_flags!, Kept), owner)!;
        s_command!.GetProperty(nameof(SimCommand.Tick))!.SetValue(command, tick);
        return command;
    }
}
