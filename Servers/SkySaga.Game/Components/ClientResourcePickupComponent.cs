using System;

using System.Numerics;

using RakNet;

namespace SkySaga.Game.Components;

/// <summary>
/// A resource lying on the floor waiting to be picked up — the entity behind every item that
/// pops out of a broken block or a harvested ore seam.
/// </summary>
/// <remarks>
/// Matches the <c>clientresourcepickupcomponent</c> the <c>Pickup</c> entity declares. A Pickup
/// carries no item data of its own: <see cref="InventoryItemEntity"/> points at a separate
/// <c>BasicInventoryItem</c> entity, which is where the resource name and count live. So a floor
/// drop is always two entities, created in that order.
///
/// Only the four parameters the drop actually needs are synced. The velocities and throwtype
/// describe the arc the item flies along as it pops out, and leaving their bits clear lets the
/// client pick its own — the same approach that made the Chest full-sync work, and one less set
/// of guessed bit widths. Positions reuse the transform encoding (1/32 of a voxel, max 0x10000).
/// </remarks>
public class ClientResourcePickupComponent : Component
{
    /// <summary>Id of the BasicInventoryItem entity holding the resource and count.</summary>
    public int InventoryItemEntity { get; set { field = value; OnParameterChanged(); } }

    /// <summary>Whether a player may pick this up. False makes it purely decorative.</summary>
    public bool PickupEnabled { get; set { field = value; OnParameterChanged(); } } = true;

    /// <summary>Where the item pops out from — normally the block that was broken.</summary>
    public Vector<int> StartPosition { get; set { field = value; OnParameterChanged(); } }

    /// <summary>Where it comes to rest.</summary>
    public Vector<int> TargetPosition { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(InventoryItemEntity), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(InventoryItemEntity);

            return true;
        }
        else if (parameterName.Equals(nameof(PickupEnabled), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(PickupEnabled);

            return true;
        }
        else if (parameterName.Equals(nameof(StartPosition), StringComparison.OrdinalIgnoreCase))
        {
            WritePosition(bitStream, StartPosition);

            return true;
        }
        else if (parameterName.Equals(nameof(TargetPosition), StringComparison.OrdinalIgnoreCase))
        {
            WritePosition(bitStream, TargetPosition);

            return true;
        }

        return false;
    }

    private static void WritePosition(BitStream bitStream, Vector<int> position)
    {
        var bits = 32 - Util.NumBitsRequiredUInt32(0x10000u);

        bitStream.WriteBits(BitConverter.GetBytes(position[0]), bits, true);
        bitStream.WriteBits(BitConverter.GetBytes(position[1]), bits, true);
        bitStream.WriteBits(BitConverter.GetBytes(position[2]), bits, true);
    }
}
