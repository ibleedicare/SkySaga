using System.Collections.Generic;

namespace SkySaga.Game.GeoData;

/// <summary>
/// One entry of <c>GeoData.json &gt; LootTables</c> — what a harvested thing yields.
/// </summary>
/// <remarks>
/// Loot is two levels. A <c>LootTable</c> names one or more <c>LootList</c>s, each with a
/// <see cref="LootTableEntry.SpawnPercentage"/> roll; a <c>LootList</c> then names the actual
/// resources. So <c>CarbonOre_Seam_LootTable</c> → <c>CarbonLoot</c> → <c>Carbon x1</c>.
/// </remarks>
public sealed class LootTableData
{
    public required string Name { get; init; }

    public required IReadOnlyList<LootTableEntry> Entries { get; init; }
}

public sealed class LootTableEntry
{
    /// <summary>Name of the <c>LootList</c> this entry rolls.</summary>
    public required string Name { get; init; }

    public int Quantity { get; init; } = 1;

    /// <summary>0-100 chance this entry produces anything at all.</summary>
    public int SpawnPercentage { get; init; } = 100;
}

/// <summary>One entry of <c>GeoData.json &gt; LootLists</c>.</summary>
public sealed class LootListData
{
    public required string Name { get; init; }

    public required IReadOnlyList<LootResource> Resources { get; init; }
}

public sealed class LootResource
{
    /// <summary>Item name, matching a <c>Resources</c> entry.</summary>
    public required string Name { get; init; }

    public int Quantity { get; init; } = 1;

    /// <summary>Relative weight against the other resources in the same list.</summary>
    public int Frequency { get; init; } = 1;
}
