using System.Collections;
using Meitou.Simulation.Player;

namespace Fiefdom;

/// <summary>
/// A job another code mod offers on its buildings for people to take (the game's job offers, <c>ModContext.AddJobOffer</c>): its job type, the
/// building records it is offered on and the caption the building's menu shows.
/// </summary>
public sealed record OfferedJob(int Type, IReadOnlySet<string> Buildings, string Caption);

/// <summary>
/// The jobs code mods offer to people, read once from the game's list. Found by reflection, so Fiefdom still loads on a game without job offers
/// (MeitouClient before the 0.22.2 nightly of 2026-11-23); there the list is empty. Offers taken only by animals are left out: a townsperson is
/// never an animal.
/// </summary>
static class JobOffers
{
    /// <summary>
    /// The game's flag for offers that people take (<c>ModJobTakers.People</c>).
    /// </summary>
    const int People = 1;

    /// <summary>
    /// The player system, whose extensions hold the game's offers (<see cref="Bind"/>).
    /// </summary>
    static PlayerSystem? s_player;

    /// <summary>
    /// The offers, once read; null until then.
    /// </summary>
    static IReadOnlyList<OfferedJob>? s_offers;

    /// <summary>
    /// The offers by job type.
    /// </summary>
    static Dictionary<int, OfferedJob> s_byType = [];

    /// <summary>
    /// The building records any offer names.
    /// </summary>
    static HashSet<string> s_buildings = new(StringComparer.Ordinal);

    /// <summary>
    /// The offers for people, in the order the mods registered them. Read on the first call: every mod registers its offers before the world
    /// runs, so the list does not change afterwards.
    /// </summary>
    public static IReadOnlyList<OfferedJob> All()
    {
        if (s_offers is not null) return s_offers;

        var offers = Read();
        s_byType = offers.ToDictionary(o => o.Type);
        s_buildings = new HashSet<string>(offers.SelectMany(o => o.Buildings), StringComparer.Ordinal);
        s_offers = offers;
        foreach (var offer in offers) FiefdomSystem.Context?.Log($"other mods' work: \"{offer.Caption}\" (job {offer.Type}) on {offer.Buildings.Count} building kind(s)");
        return offers;
    }

    /// <summary>
    /// Points the reader at a new world's player system; the offers are read again on the next call.
    /// </summary>
    public static void Bind(PlayerSystem? player)
    {
        s_player = player;
        s_offers = null;
    }

    /// <summary>
    /// The offer of job type <paramref name="type"/>, if a mod offers it to people.
    /// </summary>
    public static OfferedJob? Of(int type)
    {
        All();
        return s_byType.GetValueOrDefault(type);
    }

    /// <summary>
    /// Whether some offer names building record <paramref name="record"/>.
    /// </summary>
    public static bool OnAny(string record)
    {
        All();
        return s_buildings.Contains(record);
    }

    /// <summary>
    /// Reads the game's offers through the player system's extensions; empty on a game without them.
    /// </summary>
    static List<OfferedJob> Read()
    {
        var offers = new List<OfferedJob>();
        object? extensions = s_player?.GetType().GetProperty("Extensions")?.GetValue(s_player);
        if (extensions?.GetType().GetProperty("JobOffers")?.GetValue(extensions) is not IEnumerable list)
        {
            FiefdomSystem.Context?.Log("this game has no job offers: other mods' work does not show in the Work tab");
            return offers;
        }

        foreach (object? offer in list)
            if (offer is not null && Convert(offer) is { } read) offers.Add(read);

        return offers;
    }

    /// <summary>
    /// One of the game's offers as Fiefdom's, or null when it is not for people.
    /// </summary>
    static OfferedJob? Convert(object offer)
    {
        var type = offer.GetType();
        int takers = System.Convert.ToInt32(type.GetProperty("Takers")?.GetValue(offer) ?? 0);
        if ((takers & People) == 0) return null;

        int job = System.Convert.ToInt32(type.GetProperty("Type")?.GetValue(offer) ?? 0);
        var buildings = type.GetProperty("Buildings")?.GetValue(offer) as IEnumerable<string> ?? [];
        string caption = type.GetProperty("Caption")?.GetValue(offer) as string ?? "";
        return new OfferedJob(job, new HashSet<string>(buildings, StringComparer.Ordinal), caption);
    }
}
