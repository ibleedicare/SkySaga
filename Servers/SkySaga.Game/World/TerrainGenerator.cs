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

    // Materials confirmed to render as solid blocks.
    private const byte Air = byte.MaxValue;
    private const byte Dirt = 24;
    private const byte Stone = 13;
    private const byte Rock = 14;

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

        var solid = false;

        for (var h1 = 0; h1 < ChunkSize; h1++)
        {
            for (var h2 = 0; h2 < ChunkSize; h2++)
            {
                var worldH1 = chunkZ * ChunkSize + h1;
                var worldH2 = chunkX * ChunkSize + h2;

                var (surface, floor) = Column(worldH1, worldH2);

                for (var y = 0; y < ChunkSize; y++)
                {
                    var worldY = chunkY * ChunkSize + y;

                    var material = Material(worldY, surface, floor);

                    if (material == Air)
                        continue;

                    voxels[1 + y * ChunkSize * ChunkSize + h1 * ChunkSize + h2] = material;

                    solid = true;
                }
            }
        }

        if (!solid)
            return null;

        // The array starts zeroed and 0 is a real block id, so fill the gaps with air.
        for (var i = 1; i < voxels.Length; i++)
        {
            if (voxels[i] == 0)
                voxels[i] = Air;
        }

        return voxels;
    }

    /// <summary>A spot to drop the player: above the surface at the middle of the world.</summary>
    public static (int X, int Y, int Z) Spawn()
    {
        var centre = SizeChunks * ChunkSize / 2;

        var (surface, _) = Column(centre, centre);

        return (centre, surface + 3, centre);
    }

    /// <summary>A chunk of nothing but air, as a two byte "fill" blob.</summary>
    public static byte[] EmptyChunk() => [3, Air];

    /// <summary>Surface height and island underside for one column.</summary>
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

    private static byte Material(int y, int surface, int floor)
    {
        if (y > surface || y < floor)
            return Air;

        if (y == surface)
            return Dirt;

        if (y > surface - 4)
            return Dirt;

        return y < floor + 3 ? Rock : Stone;
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
