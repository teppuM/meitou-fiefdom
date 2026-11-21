namespace Fiefdom;

/// <summary>
/// The rules a building in a Fiefdom town can carry. What a building is used for comes from its furniture (beds, machines, counters, turrets);
/// the rules say only what furniture cannot. The numbers are saved; never renumber one, only add new bits.
/// </summary>
[Flags]
public enum BuildingRule : byte
{
    /// <summary>
    /// No rules.
    /// </summary>
    None = 0,

    /// <summary>
    /// Residents live here, one per bed.
    /// </summary>
    Residents = 1,

    /// <summary>
    /// Every storage box inside counts as town stock, whatever its own mark.
    /// </summary>
    TownStock = 2,

    /// <summary>
    /// The town's guards live here, one per bed (never together with <see cref="Residents"/>).
    /// </summary>
    Guards = 4,

    /// <summary>
    /// The guards living here watch the gates; without it they patrol the town.
    /// </summary>
    GateDuty = 8,
}

/// <summary>
/// What a storage box in a Fiefdom town holds goods for. The numbers are saved; never renumber one.
/// </summary>
public enum StorageMark : byte
{
    /// <summary>
    /// The player's alone: townspeople never take from it (the default for every box).
    /// </summary>
    KeepForMe = 0,

    /// <summary>
    /// Its goods feed the merchants' shops and the townspeople's meals.
    /// </summary>
    TownStock = 1,
}

/// <summary>
/// The one purpose saves before format 4 gave a building. Read only to turn old saves into rules: Home became <see cref="BuildingRule.Residents"/>,
/// Storehouse <see cref="BuildingRule.TownStock"/>, and the rest, which did nothing yet, are dropped.
/// </summary>
enum OldPurpose : byte
{
    Home = 1,
    Storehouse = 11,
}

/// <summary>
/// The names shown for storage marks and shifts.
/// </summary>
public static class Names
{
    /// <summary>
    /// A storage mark's name.
    /// </summary>
    public static string Of(StorageMark mark) => mark == StorageMark.TownStock ? "Town stock" : "Keep for me";

    /// <summary>
    /// A shift's name.
    /// </summary>
    public static string Of(Shift shift) => shift switch { Shift.Day => "day shift", Shift.Night => "night shift", _ => "day and night" };
}
