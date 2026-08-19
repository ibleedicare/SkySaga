using System;
using System.Collections.Generic;

using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;

namespace SkySaga.Game.Packets;

/// <summary>
/// Tells the client that voxels in one chunk changed — how a dug block actually disappears
/// and a placed block actually appears, instead of only existing in the client's prediction.
/// </summary>
/// <remarks>
/// Format from idkb8907/SkySaga_Server, which had this working:
/// <code>
///   chunkCoord X, Y, Z          6 bits each (32 - NumBitsRequired(32))
///   edit list                   count-optimised, default count 7
///     voxelIndex                8 bits — the new material
///     voxel coord list          count-optimised, default 7; X, Y, Z at 6 bits each
/// </code>
/// The count-optimised lists write <c>count - 1</c>, and air goes on the wire as 255.
/// </remarks>
public class PartialChunkEditsSync : ISerializablePacket
{
    /// <summary>One material applied to a set of voxels in the chunk.</summary>
    public sealed class Edit
    {
        public byte VoxelIndex;

        public List<(int X, int Y, int Z)> Voxels = [];
    }

    private const int DefaultCount = 7;

    public int ChunkX;
    public int ChunkY;
    public int ChunkZ;

    public List<Edit> Edits = [];

    public BitStream Serialize()
    {
        var bitStream = new BitStream();

        bitStream.WritePacketId(PacketId.PartialChunkEditsSync);

        WriteCoordinate(bitStream, ChunkX);
        WriteCoordinate(bitStream, ChunkY);
        WriteCoordinate(bitStream, ChunkZ);

        WriteCount(bitStream, Edits.Count);

        foreach (var edit in Edits)
        {
            bitStream.WriteBits([edit.VoxelIndex], 8, true);

            WriteCount(bitStream, edit.Voxels.Count);

            foreach (var (x, y, z) in edit.Voxels)
            {
                WriteCoordinate(bitStream, x);
                WriteCoordinate(bitStream, y);
                WriteCoordinate(bitStream, z);
            }
        }

        return bitStream;
    }

    /// <summary>Chunk and voxel coordinates are 6 bit fields.</summary>
    private static void WriteCoordinate(BitStream bitStream, int value)
        => bitStream.WriteBits(BitConverter.GetBytes(value), 32 - Util.NumBitsRequiredUInt32(32), true);

    /// <summary>
    /// The same "count optimised" list length the inventory uses, except the value written is
    /// <c>count - 1</c>: a short list writes the length inline, a long one writes the default
    /// then a flag bit and the real count.
    /// </summary>
    private static void WriteCount(BitStream bitStream, int count)
    {
        if (count < DefaultCount)
        {
            bitStream.WriteBits(BitConverter.GetBytes(count - 1), 32 - Util.NumBitsRequiredUInt32(DefaultCount), true);

            return;
        }

        bitStream.WriteBits(BitConverter.GetBytes(DefaultCount - 1), 32 - Util.NumBitsRequiredUInt32(DefaultCount), true);

        bitStream.Write1();
        bitStream.Write(count);
    }
}
