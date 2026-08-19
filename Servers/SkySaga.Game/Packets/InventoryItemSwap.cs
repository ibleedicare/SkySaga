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

        // Only the player's own inventory is backed so far; containers come later.
        if (sourceEntityID != connection.Player.Id || targetEntityID != connection.Player.Id)
        {
            Console.WriteLine("[inventory] swap involves another entity's inventory — ignored");

            return true;
        }

        if (!connection.Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return true;

        var slots = inventory.InventoryEntityList;

        if (sourceSlotID < 0 || sourceSlotID >= slots.Count ||
            targetSlotID < 0 || targetSlotID >= slots.Count)
        {
            Console.WriteLine($"[inventory] slot out of range (have {slots.Count})");

            return true;
        }

        (slots[sourceSlotID], slots[targetSlotID]) = (slots[targetSlotID], slots[sourceSlotID]);

        // Reassign so the setter raises the change and the entity syncs.
        inventory.InventoryEntityList = slots;

        return true;
    }
}
