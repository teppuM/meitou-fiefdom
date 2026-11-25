using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Economy;

namespace Fiefdom;

/// <summary>
/// One item a Fiefdom merchant can bring in from outside, read from the merged game database's real trader vendor lists so modded items appear
/// automatically and dungeon/bandit loot tables are left out.
/// </summary>
public readonly record struct CatalogItem(string Record, FcsRecordType Type, int Value, int Weight, string Manufacturer = "");

/// <summary>
/// The outside goods available to Fiefdom merchants, grouped by <see cref="Goods"/> flag. Built once when the mod starts, sorted deterministically
/// by record id, and read-only during play.
/// </summary>
public static class ShopCatalog
{
    /// <summary>
    /// Item function for money items (never stocked as trade goods).
    /// </summary>
    const int MoneyFunction = 13;

    /// <summary>
    /// Items by single <see cref="Goods"/> bit, sorted by record id.
    /// </summary>
    static readonly Dictionary<Goods, List<CatalogItem>> s_byKind = [];

    /// <summary>
    /// Builds the catalog from every trader SQUAD_TEMPLATE's VENDOR_LIST records in the merged game database.
    /// </summary>
    public static string Load(GameDatabase db, Action<string> log)
    {
        s_byKind.Clear();
        var vendorIds = new HashSet<string>(StringComparer.Ordinal);
        int traders = 0;
        foreach (var r in db.OfType(FcsRecordType.SquadTemplate))
        {
            if (!r.GetBool("is trader")) continue;
            var vendors = r.GetReferences("vendors");
            if (vendors.Count == 0) continue;
            traders++;
            foreach (var link in vendors) vendorIds.Add(link.TargetStringId);
        }

        // Accumulate best weight per (kind, record) so duplicates across vendor lists do not skew or repeat entries.
        var weights = new Dictionary<(Goods Kind, string Record), (FcsRecordType Type, int Value, int Weight, string Manufacturer)>();
        foreach (string vid in vendorIds.OrderBy(static id => id, StringComparer.Ordinal))
        {
            if (db.Find(vid) is not { Type: FcsRecordType.VendorList } vr) continue;
            var list = VendorList.From(db, vr);
            Dictionary<string, string>? weaponMaker = null;
            if (list.Manufacturers.Count > 0)
            {
                weaponMaker = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var m in list.Manufacturers)
                {
                    if (db.Find(m.Id) is not { } mr) continue;
                    foreach (var wt in mr.GetReferences("weapon types"))
                        weaponMaker.TryAdd(wt.TargetStringId, m.Id);
                }
            }
            foreach (var entry in list.Entries)
            {
                if (entry.Type is not (FcsRecordType.Item or FcsRecordType.Weapon or FcsRecordType.Armour
                    or FcsRecordType.Crossbow or FcsRecordType.LimbReplacement or FcsRecordType.Container)) continue;
                if (db.Find(entry.Id) is not { } item) continue;
                if (item.GetBool("unique")) continue;
                if (entry.Type == FcsRecordType.Item && item.GetInt("item function") == MoneyFunction) continue;

                int value = item.GetInt("value");
                if (value <= 0) continue;

                string maker = entry.Type == FcsRecordType.Weapon && weaponMaker is not null
                    ? weaponMaker.GetValueOrDefault(entry.Id, "")
                    : "";
                var kind = KindOfRecord(item);
                var key = (kind, item.StringId);
                int w = Math.Max(1, entry.Weight);
                if (weights.TryGetValue(key, out var existing))
                    weights[key] = (existing.Type, existing.Value, Math.Min(1000, existing.Weight + w), existing.Manufacturer.Length > 0 ? existing.Manufacturer : maker);
                else
                    weights[key] = (entry.Type, value, w, maker);
            }
        }

        int total = 0;
        foreach (var ((kind, record), info) in weights.OrderBy(static p => p.Key.Kind).ThenBy(static p => p.Key.Record, StringComparer.Ordinal))
        {
            if (!s_byKind.TryGetValue(kind, out var list)) s_byKind[kind] = list = [];
            list.Add(new CatalogItem(record, info.Type, info.Value, info.Weight, info.Manufacturer));
            total++;
        }

        string summary = $"shop catalog: {total} item(s) across {s_byKind.Count} kind(s) from {vendorIds.Count} trader vendor list(s) ({traders} trader templates)";
        log(summary);
        return summary;
    }

    /// <summary>
    /// Classifies an FCS item record into one <see cref="Goods"/> flag.
    /// </summary>
    public static Goods KindOfRecord(GameRecord record) => record.Type switch
    {
        FcsRecordType.Weapon or FcsRecordType.Crossbow => Goods.Weapons,
        FcsRecordType.Armour => Goods.Armour,
        FcsRecordType.Container => Goods.Bags,
        FcsRecordType.LimbReplacement => Goods.Robotics,
        FcsRecordType.Item => record.GetInt("item function") switch
        {
            3 or 15 => Goods.Food,
            1 or 12 => Goods.Medicine,
            2 => Goods.Robotics,
            8 => Goods.Drugs,
            5 => Goods.Weapons,
            6 => Goods.Armour,
            4 => Goods.Bags,
            _ => Goods.Other,
        },
        _ => Goods.Other,
    };

    /// <summary>
    /// Picks one deterministic item matching <paramref name="sells"/> whose base value is at most <paramref name="maxValue"/> (falling back to
    /// the cheapest matching items if none fit under the cap); null when no items exist for <paramref name="sells"/>.
    /// </summary>
    public static CatalogItem? Pick(Goods sells, long maxValue, ulong roll)
    {
        if (sells == Goods.None) return null;

        long totalWeight = 0;
        int cheapest = int.MaxValue;
        foreach (var (kind, list) in s_byKind)
        {
            if ((sells & kind) == 0) continue;
            foreach (var item in list)
            {
                if (item.Value < cheapest) cheapest = item.Value;
                if (item.Value <= maxValue) totalWeight += item.Weight;
            }
        }

        long cap = totalWeight > 0 ? maxValue : cheapest;
        if (totalWeight <= 0)
        {
            foreach (var (kind, list) in s_byKind)
            {
                if ((sells & kind) == 0) continue;
                foreach (var item in list)
                    if (item.Value <= cap) totalWeight += item.Weight;
            }
        }
        if (totalWeight <= 0) return null;

        long target = (long)(roll % (ulong)totalWeight);
        long seen = 0;
        foreach (var (kind, list) in s_byKind)
        {
            if ((sells & kind) == 0) continue;
            foreach (var item in list)
            {
                if (item.Value > cap) continue;
                seen += item.Weight;
                if (target < seen) return item;
            }
        }
        return null;
    }
}
