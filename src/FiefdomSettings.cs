using Meitou.Modding;

namespace Fiefdom;

/// <summary>
/// Fiefdom's settings on the Settings screen's Mods page. The numbers are starting values to tune in play; the simulation reads them in its own
/// serial steps.
/// </summary>
public sealed class FiefdomSettings
{
    /// <summary>
    /// The settings the mod runs on (set when the mod starts; the defaults until then).
    /// </summary>
    public static FiefdomSettings Current { get; set; } = new();

    /// <summary>
    /// What each resident earns a game day from odd jobs outside the town, into their home's purse.
    /// </summary>
    [Setting("Residents' outside income (cats a day)", Hint = "What each resident earns a day from odd jobs outside the town. Keep it below a day's food, or nobody takes a job.", Min = 0, Max = 200, Step = 5)]
    public int OutsideIncome { get; set; } = 35;

    /// <summary>
    /// What a worker earns for each shift worked (day 7:00 to 19:00, night the rest), from the player's money at each day change.
    /// </summary>
    [Setting("Wage per shift worked (cats)", Hint = "Paid from your money at each day change, for each day or night shift a worker spent on their post. Unpaid workers stop; three days unpaid and they leave.", Min = 0, Max = 200, Step = 5)]
    public int WagePerShift { get; set; } = 30;

    /// <summary>
    /// What a guard earns a game day, from the player's money at each day change.
    /// </summary>
    [Setting("Guard's wage (cats a day)", Hint = "Paid from your money at each day change to every guard. Three days unpaid and a guard leaves.", Min = 0, Max = 400, Step = 10)]
    public int GuardWagePerDay { get; set; } = 100;

    /// <summary>
    /// How often visitors come to a town open to the public.
    /// </summary>
    [Setting("Visitors: hours between groups", Hint = "While a town is open to the public, a group from the region comes to shop and sit about for a few hours about this often. 0: no visitors.", Min = 0, Max = 96, Step = 6)]
    public int VisitorHours { get; set; } = 24;

    /// <summary>
    /// How long a resident goes starving before leaving the town.
    /// </summary>
    [Setting("Days starving before a resident leaves", Hint = "A resident who stays starving this long leaves the town rather than starve in the street.", Min = 0.25, Max = 5, Step = 0.25)]
    public double StarvingDaysBeforeLeaving { get; set; } = 1;

    /// <summary>
    /// How many food items per resident merchants leave in town stock for meals.
    /// </summary>
    [Setting("Food kept back for meals (per resident)", Hint = "Merchants never take the town's last food for their counters: this many food items per resident stay in town stock.", Min = 0, Max = 20, Step = 1)]
    public int FoodKeptPerResident { get; set; } = 2;
}
