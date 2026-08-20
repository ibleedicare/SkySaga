using System;

using RakNet;

using SkySaga.Game.Components;

namespace SkySaga.Game.Packets;

/// <summary>
/// The client asks to move an item between two inventory slots (drag and drop).
/// </summary>
/// <remarks>
/// The client does not move the item locally — it waits for the server to apply the change
/// and sync the inventory back, so a handler that only decodes the packet leaves the UI
/// looking frozen.
///
/// Note <see cref="InventoryComponent.InventoryEntityList"/> is a list: mutating an element
/// does not run the property setter, so the property is reassigned afterwards to raise the
/// change notification that queues the EntitySync.
/// </remarks>
public static class InventoryItemSwap
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.Read(out int sourceEntityID))
            return false;

        var inOutByteArray = new byte[4];

        if (!bitStream.ReadBits(inOutByteArray, 32 - Util.NumBitsRequiredUInt32(45), true))
            return false;

        var sourceSlotID = BitConverter.ToInt32(inOutByteArray, 0);

        if (!bitStream.Read(out int targetEntityID))
            return false;

        if (!bitStream.ReadBits(inOutByteArray, 32 - Util.NumBitsRequiredUInt32(45), true))
            return false;

        var targetSlotID = BitConverter.ToInt32(inOutByteArray, 0);

        Console.WriteLine($"[inventory] swap {sourceEntityID}:{sourceSlotID} <-> {targetEntityID}:{targetSlotID}");

        // A drop onto an occupied square in a container: the entity ids differ. Same-inventory
        // drops fall through to the merge/swap path below.
        if (sourceEntityID != targetEntityID)
        {
            if (!connection.TryGetInventory(sourceEntityID, out var source) ||
                !connection.TryGetInventory(targetEntityID, out var target))
            {
                Console.WriteLine("[inventory] swap involves an entity with no inventory");

                return true;
            }

            if (connection.TryTransferBetweenInventories(source, sourceSlotID, target, targetSlotID))
                Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

            return true;
        }

        // Same entity on both sides: a drop within one inventory — the rucksack, or one chest
        // square onto another. Resolve by id rather than assuming the player.
        if (!connection.TryGetInventory(sourceEntityID, out var inventory))
            return true;

        var slots = inventory.InventoryEntityList;

        if (sourceSlotID < 0 || sourceSlotID >= slots.Count ||
            targetSlotID < 0 || targetSlotID >= slots.Count)
        {
            Console.WriteLine($"[inventory] slot out of range (have {slots.Count})");

            return true;
        }

        // Dropping onto a square that already holds the SAME item tops that stack up instead of
        // exchanging the two. This is the packet the client sends for a drop on an occupied
        // square — InventoryItemTransferToSlot only covers empty ones — so the merge has to
        // live here. Count 0 means "as much of the stack as fits".
        if (connection.TryMergeStack(sourceSlotID, targetSlotID, 0, inventory))
        {
            Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

            return true;
        }

        (slots[sourceSlotID], slots[targetSlotID]) = (slots[targetSlotID], slots[sourceSlotID]);

        // Reassign so the setter raises the change and the entity syncs.
        inventory.InventoryEntityList = slots;

        return true;
    }
}
