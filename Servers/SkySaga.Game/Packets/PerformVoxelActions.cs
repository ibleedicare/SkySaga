using System;

using RakNet;

using SkySaga.Game.Enums;
using SkySaga.Game.GeoData;
using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// The client placing or breaking a voxel — what every build action ends up as.
/// </summary>
/// <remarks>
/// Wire format reversed by MichalSy/SkySaga_Server, bit-packed and read in this order:
/// <code>
///   location      4 bits   ActionLocation: which equipment slot acted
///   chunkCoord    6 bits   x, y, z
///   voxelCoord    6 bits   x, y, z
///   side          3 bits   BlockSide (which face was hit)
///   power         6 bits   / 32
///   position     17 bits   x, y, z, / 64
///   direction     8 bits   x, y, z, / 64 - 1
/// </code>
/// Place and break are the SAME packet; idkb8907/SkySaga_Server showed how they are told
/// apart: if the slot in <c>location</c> is a hand holding a block, it is a placement into the
/// neighbouring voxel (<c>voxelCoord + direction</c>); otherwise it is a break of
/// <c>voxelCoord</c> itself. <c>location</c> was previously modelled as a face/edge/corner
/// enum, which was wrong.
///
/// The three crack stages the player sees are client-side: the client streams a packet per
/// dig tick and the server decides when the block gives way.
/// </remarks>
public static class PerformVoxelActions
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.TryReadBitsValue(8 - Util.NumBitsRequiredByte(8), out var locationValue))
            return false;

        if (!TryReadCoordinate(bitStream, out var chunkX) ||
            !TryReadCoordinate(bitStream, out var chunkY) ||
            !TryReadCoordinate(bitStream, out var chunkZ))
            return false;

        if (!TryReadCoordinate(bitStream, out var voxelX) ||
            !TryReadCoordinate(bitStream, out var voxelY) ||
            !TryReadCoordinate(bitStream, out var voxelZ))
            return false;

        if (!bitStream.TryReadBitsValue(8 - Util.NumBitsRequiredByte(6), out var sideValue))
            return false;

        if (!TryReadCoordinate(bitStream, out var rawPower))
            return false;

        if (!TryReadPosition(bitStream, out _) ||
            !TryReadPosition(bitStream, out _) ||
            !TryReadPosition(bitStream, out _))
            return false;

        if (!TryReadDirection(bitStream, out var directionX) ||
            !TryReadDirection(bitStream, out var directionY) ||
            !TryReadDirection(bitStream, out var directionZ))
            return false;

        var location = (ActionLocation)locationValue;
        var side = (BlockSide)sideValue;
        var power = rawPower / 32f;

        // A hand holding a placeable block means "build"; anything else means "dig".
        var placing = location is ActionLocation.LeftHand or ActionLocation.RightHand
            && connection.ActiveResource is { } resource
            && GeoDataManager.TryGetResource(resource, out var held)
            && held.IsPlaceableBlock;

        if (Environment.GetEnvironmentVariable("SKYSAGA_LOG_VOXEL") == "1")
        {
            Console.WriteLine($"[voxel] {location} {side} chunk({chunkX},{chunkY},{chunkZ}) "
                + $"voxel({voxelX},{voxelY},{voxelZ}) dir({directionX},{directionY},{directionZ}) "
                + $"power {power:0.##} -> {(placing ? "place" : "dig")}");
        }

        if (placing)
        {
            // The new block goes in the empty voxel next to the face that was clicked.
            connection.PlaceVoxel(chunkX, chunkY, chunkZ,
                voxelX + directionX, voxelY + directionY, voxelZ + directionZ);
        }
        else
        {
            connection.Dig(chunkX, chunkY, chunkZ, voxelX, voxelY, voxelZ);
        }

        return true;
    }

    /// <summary>Chunk and voxel coordinates, and power, are all 6-bit fields (0-63).</summary>
    private static bool TryReadCoordinate(BitStream bitStream, out int value)
        => bitStream.TryReadBitsValue(32 - Util.NumBitsRequiredUInt32(32), out value);

    private static bool TryReadPosition(BitStream bitStream, out float value)
    {
        value = 0;

        if (!bitStream.TryReadBitsValue(32 - Util.NumBitsRequiredUInt32(0x10000), out var raw))
            return false;

        value = raw / 64f;

        return true;
    }

    private static bool TryReadDirection(BitStream bitStream, out int value)
    {
        value = 0;

        if (!bitStream.TryReadBitsValue(32 - Util.NumBitsRequiredUInt32(128), out var raw))
            return false;

        value = raw / 64 - 1;

        return true;
    }
}
