using System.Collections.Generic;
using System.Linq;

namespace SkySaga.Game.World;

/// <summary>
/// The voxel materials the client renders, and the item each one corresponds to.
/// </summary>
/// <remarks>
/// Block ids come from idkb8907/SkySaga_Server, which keyed them by the item's CRC; resolving
/// those hashes against GeoData's Resources gives the names below. This corrected three
/// mislabelled constants in the terrain generator: 24 is Sand (not Dirt), 13 is Wooden_Plank
/// (not Stone) and 14 is Leaf (not Rock). Placing Dirt was putting down block 24 — sandstone —
/// which is why a placed "dirt" block looked like the terrain walls.
/// </remarks>
public static class Blocks
{
    public const byte Air = byte.MaxValue;

    public const byte Dirt = 1;
    public const byte Stone = 2;
    public const byte Sand = 24;

    /// <summary>Block id to the item it is made of / drops.</summary>
    private static readonly Dictionary<byte, string> Items = new()
    {
        [1] = "Dirt",
        [2] = "Stone",
        [5] = "Wood",
        [8] = "Ice",
        [10] = "Snow",
        [11] = "Thatch",
        [12] = "Gravel",
        [13] = "Wooden_Plank",
        [14] = "Leaf",
        [24] = "Sand",
        [29] = "Clay",
        [35] = "Metal",
        [36] = "Cactus_Pulp"
    };

    private static readonly Dictionary<string, byte> Materials =
        Items.ToDictionary(pair => pair.Value, pair => pair.Key, System.StringComparer.OrdinalIgnoreCase);

    /// <summary>The item a broken block drops, or null when it maps to nothing.</summary>
    public static string? ItemFor(byte material)
        => Items.TryGetValue(material, out var item) ? item : null;

    /// <summary>The block an item places, or null when the item is not a terrain block.</summary>
    public static byte? MaterialFor(string itemName)
        => Materials.TryGetValue(itemName, out var material) ? material : null;
}
