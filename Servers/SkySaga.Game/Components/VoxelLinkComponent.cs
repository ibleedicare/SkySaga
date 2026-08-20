using System;
using System.Collections.Generic;

using RakNet;

namespace SkySaga.Game.Components;

/// <summary>
/// One entry of <see cref="VoxelLinkComponent.Voxels"/>: a whole-voxel offset from the entity's
/// transform position, plus the <c>VoxelIndex</c> to stamp there (from GeoData.json &gt; Voxels —
/// 39 = <c>Entity</c>, 34 = <c>Entity_Can_Stand_On</c>, 21 = <c>LitVoxel</c>).
/// </summary>
public readonly record struct VoxelLink(int X, int Y, int Z, byte VoxelIndex);

/// <summary>
/// Links an entity to the voxels it occupies in the world grid. This is what makes a chest,
/// anvil or PvP post a thing that exists in the terrain rather than a floating mesh — every one
/// of the 50 entities carrying <c>clientinteractioncomponent</c> also carries this.
/// </summary>
/// <remarks>
/// Wire format read off the client's own deserialiser <c>FUN_008f61f0</c> (dispatch
/// <c>FUN_008f6340</c>, parameter map <c>FUN_008099b0</c>: <c>Voxels</c> is component-local
/// index 0, <c>CanReplaceVoxelsOfEntityID</c> is 1):
/// <code>
///   count : 8 bits, holding (count - 1)      // width = 32 - NumBitsRequiredUInt32(199)
///   if count == 200: 1 escape bit, then a full 32-bit count
///   per element, 23 bits:
///     x, y, z    : 5 bits each, holding (coord + 15)
///     voxelIndex : 8 bits, raw
/// </code>
/// Two traps, both from the client code rather than from this protocol's usual idiom:
/// the count is biased by one, so a zero-length list is <b>not representable</b> — count8 = 0
/// makes the client read one garbage element, which is why we refuse to write an empty list at
/// all; and the coordinates are signed with a +15 bias, giving a usable range of [-15, +15].
///
/// <c>FUN_008f5010</c> resolves each offset against the entity transform as
/// <c>X = t.x + cos*(x+0.5) + sin*(z+0.5)</c>, <c>Y = t.y + (y+0.5)</c>,
/// <c>Z = t.z + cos*(z+0.5) - sin*(x+0.5)</c> — so offsets are in <b>whole voxels</b> (not the
/// transform's own units) and the X/Z pair is rotated by the entity's yaw. For a single voxel at
/// the origin the rotation is a no-op, which is why leaving <c>yawdegrees</c> unsynced is safe
/// for a chest but would not be for a multi-voxel entity.
/// </remarks>
public class VoxelLinkComponent : Component
{
    /// <summary>Client writer clamps to [1, 200] and escapes at exactly 200 (<c>FUN_008f5f50</c>).</summary>
    private const int VoxelsDefaultCount = 200;

    /// <summary>Coordinates are biased by +15 into 5 bits (<c>FUN_008f5c20</c>).</summary>
    private const int VoxelOffsetBias = 15;
    private const uint VoxelOffsetRange = 30;

    public List<VoxelLink> Voxels { get; set { field = value; OnParameterChanged(); } } = [];

    /// <summary>
    /// Entity whose voxels this one may take over, or 0 for none. On a match the client tears
    /// down that entity's own link first (<c>FUN_008f5260</c>); on no match it links anyway.
    /// </summary>
    public int CanReplaceVoxelsOfEntityID { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(Voxels), StringComparison.OrdinalIgnoreCase))
        {
            // Not representable: the client reads count = readBits(8) + 1, so an empty list
            // would decode as one garbage element. Leave the bit clear instead.
            if (Voxels.Count == 0)
                return false;

            var clamped = Math.Min(Voxels.Count, VoxelsDefaultCount);

            bitStream.WriteBits(BitConverter.GetBytes(clamped - 1),
                32 - Util.NumBitsRequiredUInt32(VoxelsDefaultCount - 1), true);

            if (clamped == VoxelsDefaultCount)
            {
                bitStream.Write1();
                bitStream.Write(Voxels.Count);
            }

            var width = 32 - Util.NumBitsRequiredUInt32(VoxelOffsetRange);

            foreach (var voxel in Voxels)
            {
                bitStream.WriteBits(BitConverter.GetBytes(voxel.X + VoxelOffsetBias), width, true);
                bitStream.WriteBits(BitConverter.GetBytes(voxel.Y + VoxelOffsetBias), width, true);
                bitStream.WriteBits(BitConverter.GetBytes(voxel.Z + VoxelOffsetBias), width, true);
                bitStream.WriteBits([voxel.VoxelIndex], 8, true);
            }

            return true;
        }
        else if (parameterName.Equals(nameof(CanReplaceVoxelsOfEntityID), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(CanReplaceVoxelsOfEntityID);

            return true;
        }

        return false;
    }
}
