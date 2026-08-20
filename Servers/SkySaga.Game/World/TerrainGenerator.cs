using System;

namespace SkySaga.Game.World;

/// <summary>
/// Generates voxel chunks for <see cref="Packets.ChunkSync"/>.
/// </summary>
/// <remarks>
/// Chunk layout, reversed from the client (see documentations/reverse-engineering.md):
/// a chunk is 32x32x32 voxels, one byte per voxel, in a flat array indexed
/// <c>y * 1024 + h1 * 32 + h2</c> — Y-major, so each 1024-byte block is one horizontal
/// layer. The blob sent to the client is that array prefixed with a compression mode byte:
/// <code>
/// 0  raw          the rest is the voxel array verbatim
/// 1  RLE          (value, count) byte pairs
/// 2  split RLE    counts first, then values, paired by index
/// 3  fill         one byte, applied to the whole chunk
/// 4  offset RLE   cumulative 16-bit end offsets, then values
/// </code>
/// Empty chunks use mode 3 so they cost two bytes instead of 32 KB.
///
/// Which horizontal axis is X and which is Z is still unconfirmed; swapping them only
/// mirrors the world, so it is cosmetic until entity placement needs to agree with voxels.
/// </remarks>
public static class TerrainGenerator
{
    public const int ChunkSize = 32;
    public const int VoxelsPerChunk = ChunkSize * ChunkSize * ChunkSize;

    // Block ids come from Blocks, which resolved them from the client's item CRCs. The
    // previous values here were mislabelled: 24 is Sand, 13 is Wooden_Plank and 14 is Leaf,
    // so the "stone" underground was really planks and leaves.
    private static byte Air => Blocks.Air;
    private static byte Sand => Blocks.Sand;
    private static byte Dirt => Blocks.Dirt;
    private static byte Stone => Blocks.Stone;

    public static int Seed { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SKYSAGA_WORLD_SEED"), out var seed) ? seed : 1337;

    /// <summary>Chunks generated per axis in the horizontal plane.</summary>
    public static int SizeChunks { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SKYSAGA_WORLD_CHUNKS"), out var size) ? size : 4;

    /// <summary>
    /// Builds the payload for one chunk, or null when the chunk is entirely air and the
    /// caller should skip it.
    /// </summary>
    public static byte[]? GenerateChunk(int chunkX, int chunkY, int chunkZ)
    {
        var voxels = new byte[VoxelsPerChunk + 1];

        voxels[0] = 0; // raw

        // Start as air rather than leaving zeros to be patched up afterwards: 0 is a real
        // block id (Dirt), so a "0 means empty" sentinel silently deleted every dirt voxel
        // and left the world hollow and unlit.
        Array.Fill(voxels, Air, 1, VoxelsPerChunk);

        var solid = false;

        for (var h1 = 0; h1 < ChunkSize; h1++)
        {
            for (var h2 = 0; h2 < ChunkSize; h2++)
            {
                var worldH1 = chunkZ * ChunkSize + h1;
                var worldH2 = chunkX * ChunkSize + h2;

                for (var y = 0; y < ChunkSize; y++)
                {
                    var worldY = chunkY * ChunkSize + y;

                    // worldH1 is Z and worldH2 is X - see MaterialAt.
                    var material = MaterialAt(worldH2, worldY, worldH1);

                    if (material == Air)
                        continue;

                    voxels[1 + y * ChunkSize * ChunkSize + h1 * ChunkSize + h2] = material;

                    solid = true;
                }
            }
        }

        if (!solid)
            return null;

        return voxels;
    }

    /// <summary>A spot to drop the player: above the surface at the middle of the world.</summary>
    public static (int X, int Y, int Z) Spawn()
    {
        var centre = SizeChunks * ChunkSize / 2;

        if (PaletteMode)
            return (centre, PaletteFloor + 3, centre);

        var (surface, _) = Column(centre, centre);

        return (centre, surface + 3, centre);
    }

    /// <summary>A chunk of nothing but air, as a two byte "fill" blob.</summary>
    public static byte[] EmptyChunk() => [3, Air];

    /// <summary>Surface height and island underside for one column.</summary>
    /// <summary>
    /// The material at a world voxel, for answering "what did the player just dig?" without
    /// regenerating a whole chunk. Returns <see cref="byte.MaxValue"/> (air) outside the ground.
    /// </summary>
    public static byte MaterialAt(int worldX, int worldY, int worldZ)
    {
        if (PaletteMode)
            return PaletteMaterial(worldX, worldY, worldZ);

        // GenerateChunk builds columns as Column(worldH1 = Z, worldH2 = X), so the arguments
        // go in that order here too — passing X,Z reads a different column entirely.
        var (surface, floor) = Column(worldZ, worldX);

        return Material(worldX, worldY, worldZ, surface, floor);
    }

    /// <summary>
    /// SKYSAGA_BLOCK_PALETTE=1 replaces the world with every block id laid out on a floor, so
    /// unknown blocks can be identified by looking at them: dig one and the server logs which
    /// id it was. Ids run 0-255 over a repeating 16x16 tile, id = (x % 16) + (z % 16) * 16.
    /// </summary>
    public static bool PaletteMode =>
        Environment.GetEnvironmentVariable("SKYSAGA_BLOCK_PALETTE") == "1";

    private const int PaletteFloor = 16;

    private static byte PaletteMaterial(int worldX, int worldY, int worldZ)
    {
        // A slab to stand on, with the samples one layer above it.
        if (worldY < PaletteFloor)
            return Stone;

        if (worldY > PaletteFloor)
            return Air;

        var x = ((worldX % 16) + 16) % 16;
        var z = ((worldZ % 16) + 16) % 16;

        return (byte)(x + z * 16);
    }


    /// <summary>The material an item places, or null for items that are not terrain blocks.</summary>
    public static byte? MaterialFor(string itemName) => Blocks.MaterialFor(itemName);

    /// <summary>The item a broken voxel drops, or null for materials with no item.</summary>
    public static string? LootFor(byte material) => Blocks.ItemFor(material);

    private static (int Surface, int Floor) Column(int h1, int h2)
    {
        // Rolling surface.
        var surface = 14 + (int)(Fractal(h1 * 0.045f, h2 * 0.045f, Seed) * 7f);

        // Islands are thicker under high ground, which gives them a rounded underside
        // rather than a flat cut — SkySaga's world is floating islands, not a solid map.
        var thickness = 5 + (int)(Fractal(h1 * 0.03f + 100f, h2 * 0.03f + 100f, Seed + 7) * 9f)
            + (surface - 14) / 2;

        return (surface, Math.Max(0, surface - thickness));
    }

    private static byte Material(int x, int y, int z, int surface, int floor)
    {
        if (y > surface || y < floor)
            return Air;

        // A desert island: sand on top, dirt under it, stone at depth.
        if (y > surface - 4)
            return Sand;

        if (y >= floor + 3)
            return Dirt;

        return Ore(x, y, z) ?? Stone;
    }


    /// <summary>
    /// Scatters ore deposits through the stone layer so they can actually be mined. Deposits
    /// are <c>IsPlaceable: false</c> in the client's table — they exist only in terrain, which
    /// is why none could be found before the generator produced any.
    /// </summary>
    private static byte? Ore(int x, int y, int z)
    {
        // Deterministic per voxel, and sparse: roughly one voxel in twenty is ore.
        var roll = Hash(x * 31 + y * 17, z * 13 + y * 7, Seed + 99);

        if (roll > 0.05f)
            return null;

        // Deeper is rarer and more valuable.
        return roll switch
        {
            < 0.006f => Blocks.GoldDeposit,
            < 0.016f => Blocks.LeadDeposit,
            < 0.030f => Blocks.CopperDeposit,
            _ => Blocks.IronDeposit
        };
    }

    /// <summary>Value noise with a few octaves; deterministic, no allocations.</summary>
    private static float Fractal(float x, float y, int seed)
    {
        var total = 0f;
        var amplitude = 1f;
        var frequency = 1f;
        var normal = 0f;

        for (var octave = 0; octave < 4; octave++)
        {
            total += Value(x * frequency, y * frequency, seed + octave) * amplitude;
            normal += amplitude;

            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return total / normal;
    }

    private static float Value(float x, float y, int seed)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);

        var fx = Smooth(x - x0);
        var fy = Smooth(y - y0);

        var top = Lerp(Hash(x0, y0, seed), Hash(x0 + 1, y0, seed), fx);
        var bottom = Lerp(Hash(x0, y0 + 1, seed), Hash(x0 + 1, y0 + 1, seed), fx);

        return Lerp(top, bottom, fy);
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Hash(int x, int y, int seed)
    {
        var h = x * 374761393 + y * 668265263 + seed * 1274126177;

        h = (h ^ (h >> 13)) * 1274126177;

        return ((h ^ (h >> 16)) & 0x7fffffff) / (float)0x7fffffff;
    }
}
