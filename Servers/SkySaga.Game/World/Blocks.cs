using System.Linq;

using SkySaga.Game.GeoData;

namespace SkySaga.Game.World;

/// <summary>
/// Block ids and the item each one drops, taken from the client's own table
/// (<c>GeoData.json &gt; Voxels</c>, 50 entries).
/// </summary>
/// <remarks>
/// This used to be a hand-written map assembled from item CRCs, which was both incomplete and
/// wrong in places — it had no ore deposits and no water, and it mislabelled several ids. The
/// real table carries a <c>VoxelIndex</c> (the byte on the wire) and a <c>Resource</c> (the
/// item mined from it) per block, so both directions come straight from the data now.
///
/// Note several blocks share a drop: Dirt, Exposed_Dirt, Dirt_Frozen and Dirt_Path all yield
/// Dirt, and every stone and ore deposit yields Stone. Going item to block therefore picks the
/// <c>IsPlaceable</c> entry, since that is the one a player is allowed to put down.
/// </remarks>
public static class Blocks
{
    public const byte Air = byte.MaxValue;

    /// <summary>Ids the terrain generator builds with, resolved by name from the table.</summary>
    public static byte Dirt => Id("Dirt", 0);
    public static byte Stone => Id("Blue_Stone", 2);
    public static byte Sand => Id("Sand", 24);
    public static byte Water => Id("Water", 50);

    /// <summary>Ore veins. Not placeable — they only exist in generated terrain.</summary>
    public static byte IronDeposit => Id("Iron_Deposit", 26);
    public static byte CopperDeposit => Id("Copper_Deposit", 25);
    public static byte GoldDeposit => Id("Gold_Deposit", 42);
    public static byte LeadDeposit => Id("Lead_Deposit", 33);

    /// <summary>The item a broken block drops, or null when it yields nothing.</summary>
    public static string? ItemFor(byte material)
        => GeoDataManager.TryGetVoxel(material, out var voxel) && voxel.Resource.Length > 0
            ? voxel.Resource
            : null;

    /// <summary>
    /// The block an item places, or null when the item is not a placeable block. Prefers the
    /// entry the player is actually allowed to place.
    /// </summary>
    public static byte? MaterialFor(string itemName)
    {
        var matches = GeoDataManager.Voxels
            .Where(voxel => string.Equals(voxel.Resource, itemName, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return null;

        var placeable = matches.FirstOrDefault(voxel => voxel.IsPlaceable);

        return placeable?.VoxelIndex;
    }

    /// <summary>How tough a block is to mine, 0-16; ore is 10 and worked metal 16.</summary>
    public static int ToughnessOf(byte material)
        => GeoDataManager.TryGetVoxel(material, out var voxel) ? voxel.MiningToughness : 0;

    private static byte Id(string name, byte fallback)
        => GeoDataManager.TryGetVoxel(name, out var voxel) ? voxel.VoxelIndex : fallback;
}
