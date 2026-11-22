using System.Numerics;
using System.Reflection;
using Meitou.Simulation.Construction;

namespace Fiefdom;

/// <summary>
/// Building plans made so they hold across game releases: <see cref="PlannedBuilding"/> gains optional constructor parameters between releases
/// (MeitouClient 0.22.2-nightly.20261122.2 added one), and a mod compiled against an older release calls a constructor the newer game no longer
/// has, which stops the mod (<c>MissingMethodException</c>). So the plan is made through whichever constructor the running game has, its
/// parameters matched by name and the ones this mod does not know left at their defaults.
/// </summary>
static class Plans
{
    /// <summary>
    /// The running game's widest <see cref="PlannedBuilding"/> constructor and its parameters.
    /// </summary>
    static readonly (ConstructorInfo Ctor, ParameterInfo[] Parameters) Widest = Find();

    /// <summary>
    /// A plan for <paramref name="record"/> at <paramref name="position"/>, turned by <paramref name="rotation"/>, on <paramref name="floor"/>,
    /// inside the building <paramref name="parent"/> (null outdoors).
    /// </summary>
    public static PlannedBuilding Make(string record, Vector3 position, Quaternion rotation, int floor = 0, string? parent = null)
    {
        var args = new object?[Widest.Parameters.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var p = Widest.Parameters[i];
            args[i] = p.Name switch
            {
                "Record" => record,
                "Position" => position,
                "Rotation" => rotation,
                "Floor" => floor,
                "Parent" => parent,
                _ => Default(p),
            };
        }

        return (PlannedBuilding)Widest.Ctor.Invoke(args);
    }

    /// <summary>
    /// The constructor with the most parameters.
    /// </summary>
    static (ConstructorInfo, ParameterInfo[]) Find()
    {
        var ctor = typeof(PlannedBuilding).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return (ctor, ctor.GetParameters());
    }

    /// <summary>
    /// A parameter's default: its declared default, else its type's.
    /// </summary>
    static object? Default(ParameterInfo p)
    {
        if (p.HasDefaultValue) return p.DefaultValue;
        return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
    }
}
