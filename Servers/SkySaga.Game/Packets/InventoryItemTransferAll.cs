using System;

using RakNet;

namespace SkySaga.Game.Packets;

/// <summary>
/// "Take All" in the loot window: move every stack from one inventory into another.
/// </summary>
/// <remarks>
/// Nine bytes, and the simplest packet in the inventory set — just the two entity ids, with no
/// slots and no counts:
/// <code>
/// BA            message id (ID_USER_PACKET_ENUM + 52)
/// 00 00 00 0C   source entity id (the chest)
/// 00 00 00 0E   target entity id (the player)
/// </code>
/// Captured from four presses of the button against chest 12 with player 14. Like the other
/// inventory packets the client applies nothing locally and waits for the sync, so while this
/// was unhandled the button appeared to do nothing at all.
/// </remarks>
public static class InventoryItemTransferAll
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.Read(out int sourceEntityId))
            return false;

        if (!bitStream.Read(out int targetEntityId))
            return false;

        Console.WriteLine($"[inventory] transfer all {sourceEntityId} -> {targetEntityId}");

        if (!connection.TryGetInventory(sourceEntityId, out var source) ||
            !connection.TryGetInventory(targetEntityId, out var target))
        {
            Console.WriteLine("[inventory] transfer all involves an entity with no inventory");

            return true;
        }

        var moved = connection.TransferAll(source, target);

        Console.WriteLine($"[inventory] transfer all moved {moved} stack(s); "
            + $"now: {connection.DescribeInventory()}");

        return true;
    }
}
