namespace SkySaga.Game.GeoData;

/// <summary>
/// One entry of <c>GeoData.json &gt; Voxels</c> — the client's block table.
/// </summary>
/// <remarks>
/// This is the authoritative block list: 50 entries, each with the id the wire format uses
/// (<see cref="VoxelIndex"/>) and the item it is made of / drops (<see cref="Resource"/>).
/// It replaces a hand-written table that had been assembled from item CRCs, and it is what
/// tells us blocks like <c>Iron_Deposit</c> and <c>Water</c> exist at all.
/// </remarks>
public sealed class VoxelData
{
    /// <summary>Block name, e.g. <c>Iron_Deposit</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The byte the chunk data and <c>PartialChunkEditsSync</c> use.</summary>
    public required byte VoxelIndex { get; init; }

    /// <summary>Item this block is made of, and what mining it yields. May be empty.</summary>
    public string Resource { get; init; } = string.Empty;

    /// <summary>Whether a player can place this block; ore deposits cannot.</summary>
    public bool IsPlaceable { get; init; }

    public bool IsDiggable { get; init; }

    /// <summary>
    /// Whether the client draws this block at all. Eight blocks are invisible — the Entity_*
    /// markers, Tree, Cactus, LitVoxel and, surprisingly, Water — so placing one leaves a solid
    /// but unseen voxel.
    /// </summary>
    public bool IsRendered { get; init; } = true;

    /// <summary>0-16. Higher is slower to mine; ore deposits are 10, metals 16.</summary>
    public int MiningToughness { get; init; }

    public bool IsTerrain { get; init; }
}
