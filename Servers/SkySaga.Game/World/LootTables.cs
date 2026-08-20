using System;
using System.Collections.Generic;
using System.Linq;

using SkySaga.Game.GeoData;

namespace SkySaga.Game.World;

/// <summary>
/// Rolls a loot table into the items it produces, and says which table a broken block uses.
/// </summary>
/// <remarks>
/// The client ships six <c>*Ore_Seam_LootTable</c>s, and they are the only route to ore items —
/// every ore <em>voxel</em> in <c>GeoData &gt; Voxels</c> lists <c>Resource: Stone</c>, so mining
/// a deposit block yields plain stone. The seam tables belong to ore <em>entities</em>
/// (<c>o_ore_Carbon_Deposit</c> and friends, ACTR models in the .pc archives) whose definitions
/// we have not parsed yet, so nothing in the data links a voxel to a seam table.
///
/// <see cref="_seamTables"/> is therefore <b>our own mapping, not the client's</b>: it points the
/// four deposit blocks at the seam table that matches their name so ore is minable and the loot
/// path is testable today. Lead_Deposit is paired with the carbon table because the two are the
/// orphans — lead has no seam table and carbon has no voxel. Replace the whole map once the real
/// seam entities are readable.
/// </remarks>
public static class LootTables
{
    private static readonly Dictionary<string, string> _seamTables = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Iron_Deposit", "IronOre_Seam_LootTable" },
        { "Copper_Deposit", "CopperOre_Seam_LootTable" },
        { "Gold_Deposit", "GoldOre_Seam_LootTable" },
        { "Lead_Deposit", "CarbonOre_Seam_LootTable" }
    };

    /// <summary>
    /// The loot table a block rolls when broken, or null when it just drops its
    /// <c>Resource</c> straight into the rucksack the way dirt and stone do.
    /// </summary>
    public static string? TableFor(byte material)
        => GeoDataManager.TryGetVoxel(material, out var voxel) &&
           _seamTables.TryGetValue(voxel.Name, out var table)
            ? table
            : null;

    /// <summary>
    /// Roll a table into concrete items. Each entry rolls its <c>SpawnPercentage</c>, then picks
    /// one resource from its list weighted by <c>Frequency</c>.
    /// </summary>
    public static List<(string Name, int Count)> Roll(string tableName, Random random)
    {
        var rolled = new List<(string, int)>();

        if (!GeoDataManager.TryGetLootTable(tableName, out var table))
            return rolled;

        foreach (var entry in table.Entries)
        {
            if (entry.SpawnPercentage < 100 && random.Next(100) >= entry.SpawnPercentage)
                continue;

            if (!GeoDataManager.TryGetLootList(entry.Name, out var list) || list.Resources.Count == 0)
                continue;

            var total = list.Resources.Sum(resource => resource.Frequency);

            var roll = random.Next(total);

            foreach (var resource in list.Resources)
            {
                roll -= resource.Frequency;

                if (roll >= 0)
                    continue;

                rolled.Add((resource.Name, resource.Quantity * entry.Quantity));

                break;
            }
        }

        return rolled;
    }
}
