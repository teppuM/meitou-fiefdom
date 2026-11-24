using System.Numerics;
using System.Reflection;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Fiefdom;

/// <summary>
/// Settler and visitor groups spawned so the call holds across game releases: <see cref="PopulationSystem.SpawnPlatoon"/> gains optional
/// parameters between releases (MeitouClient 0.22.2-nightly.20261124.2 added a member cap), and a mod compiled against an older release calls a
/// method the newer game no longer has, which stops the mod (<c>MissingMethodException</c>). So the spawn goes through whichever overload the
/// running game has, its parameters matched by name and the ones this mod does not know left at their defaults. Runs once per group sent.
/// </summary>
static class Spawns
{
    /// <summary>
    /// The running game's widest <see cref="PopulationSystem.SpawnPlatoon"/> overload and its parameters.
    /// </summary>
    static readonly (MethodInfo Method, ParameterInfo[] Parameters) Widest = Find();

    /// <summary>
    /// Spawns a platoon of <paramref name="templateId"/> for <paramref name="faction"/> between <paramref name="minRadius"/> and
    /// <paramref name="maxRadius"/> of <paramref name="around"/>, carrying <paramref name="money"/>; its id, or -1 when no place was found.
    /// </summary>
    public static int Platoon(PopulationSystem population, World world, int faction, string templateId, int homeSite, Vector2 around,
        float minRadius, float maxRadius, long money)
    {
        var args = new object?[Widest.Parameters.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var p = Widest.Parameters[i];
            args[i] = p.Name switch
            {
                "world" => world,
                "faction" => faction,
                "templateId" => templateId,
                "homeSite" => homeSite,
                "around" => around,
                "minRadius" => minRadius,
                "maxRadius" => maxRadius,
                "money" => money,
                _ => Default(p),
            };
        }

        return (int)Widest.Method.Invoke(population, args)!;
    }

    /// <summary>
    /// The public overload with the most parameters.
    /// </summary>
    static (MethodInfo, ParameterInfo[]) Find()
    {
        var method = typeof(PopulationSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(PopulationSystem.SpawnPlatoon))
            .OrderByDescending(m => m.GetParameters().Length)
            .First();
        return (method, method.GetParameters());
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
