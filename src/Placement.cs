using System.Linq.Expressions;
using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Modding;

namespace Fiefdom;

/// <summary>
/// Building in a Fiefdom town: any furniture the game keeps indoors (shop counters, beds, ovens, benches, other mods' furniture too) may also
/// stand outdoors inside a Fiefdom town. Uses the game's placement rules (<c>ModContext.AddPlacementRule</c>), found by reflection so the mod
/// still loads on a game without them; there furniture stays indoors as in the original.
/// </summary>
static class Placement
{
    /// <summary>
    /// Registers the rule, or logs that this game has no placement rules.
    /// </summary>
    public static void Register(ModContext context)
    {
        var add = typeof(ModContext).GetMethod("AddPlacementRule");
        var ruleType = add?.GetParameters() is [{ } only] ? only.ParameterType : null;
        var waive = ruleType?.GetProperty("Waive");
        if (add is null || ruleType is null || waive is null)
        {
            context.Log("this game has no placement rules: furniture stays indoors in Fiefdom towns");
            return;
        }

        var rule = Activator.CreateInstance(ruleType)!;
        waive.SetValue(rule, Waiver(waive.PropertyType));
        add.Invoke(context, [rule]);
        context.Log("furniture may stand outdoors in Fiefdom towns");
    }

    /// <summary>
    /// The rule's waiver as the game's delegate type (ask, failure) -> bool, calling <see cref="MayStandOutdoors"/> with the ask's position and
    /// parent.
    /// </summary>
    static Delegate Waiver(Type delegateType)
    {
        var types = delegateType.GetGenericArguments();
        var ask = Expression.Parameter(types[0], "ask");
        var failure = Expression.Parameter(types[1], "failure");
        var call = Expression.Call(typeof(Placement).GetMethod(nameof(MayStandOutdoors))!, Expression.Convert(failure, typeof(int)),
            Expression.Property(ask, "Position"), Expression.Property(ask, "Parent"));
        return Expression.Lambda(delegateType, call, ask, failure).Compile();
    }

    /// <summary>
    /// Whether the game's "must be placed inside a building" is lifted: for furniture standing outdoors inside a Fiefdom town. Asked on the
    /// interface's thread for each ghost, so it reads only the published view.
    /// </summary>
    public static bool MayStandOutdoors(int failure, Vector3 position, string? parent)
    {
        if (failure != (int)PlacementFailure.MustBeInside || parent is not null) return false;

        var at = new Vector2(position.X, position.Z);
        foreach (var area in FiefdomSystem.View.Areas)
            if (Vector2.DistanceSquared(at, area.Centre) <= area.Radius * area.Radius) return true;

        return false;
    }
}
