using System;

using System.Text;

using RakNet;

using SkySaga.Game.Extensions;

using SkySaga.Game.Components;

namespace SkySaga.Game.Packets;

/// <summary>
/// Drag and drop inside an inventory: move the item in one slot to another slot.
/// </summary>
/// <remarks>
/// This is what the client sends when you drag an item in the rucksack —
/// <see cref="InventoryItemSwap"/> is a different operation and is not involved.
///
/// Layout worked out from captured packets (client to server, so there is no deserializer
/// in the client to read it from), then confirmed against drags with a known source and
/// target. A 12 byte packet, e.g. dragging one item from slot 9 to slot 10:
/// <code>
/// B9            message id (ID_USER_PACKET_ENUM + 51)
/// 00 00 00 0A   source entity id  (32 bits)
/// 00 00 00 0A   target entity id  (32 bits)
/// 24 04 A0      001001   =  9     source slot  (6 bits)
///               00000001 =  1     count        (8 bits)
///               001010   = 10     target slot  (6 bits)
///               0000              padding to the byte boundary
/// </code>
/// An earlier reading took the source as 6 bits and the target as the next 12, which
/// straddled the count and target fields: a 9 -> 10 drag decoded as 9 -> 18 and the item
/// landed in the wrong square. Verified with 9 -> 10 and 18 -> 9, both of which put the
/// target at bits 14..19.
///
/// The 8 bit count field is confirmed: splitting a stack of 50 sends count 25, partial drags
/// of 5 decode as 5, and a single item as 1. A count smaller than the source stack means a
/// split rather than a move — see Connection.TrySplitStack.
/// </remarks>
public static class InventoryItemTransferToSlot
{
    /// <summary>Enough to address the 45 inventory slots.</summary>
    private const uint SlotBits = 6;

    /// <summary>Stack size being moved; smaller than the stack means a split.</summary>
    private const uint CountBits = 8;

    /// <summary>
    /// Log the slot bits verbatim, without consuming them, so the field layout can be
    /// checked against drags whose source and target are known.
    /// </summary>
    /// <remarks>
    /// Set SKYSAGA_PACKET_BITS=1 to enable. Kept because the count field's width is still
    /// unconfirmed — see the note on the class.
    /// </remarks>
    private static void DumpSlotBits(BitStream bitStream)
    {
        if (Environment.GetEnvironmentVariable("SKYSAGA_PACKET_BITS") != "1")
            return;

        var readOffset = bitStream.GetReadOffset();

        var bitCount = bitStream.GetNumberOfUnreadBits();

        if (bitCount == 0 || bitCount > 256)
            return;

        var buffer = new byte[(bitCount + 7) / 8];

        // alignBitsToRight: false keeps the bits in transmission order, so the string below
        // reads the same way the client wrote them.
        if (bitStream.ReadBits(buffer, bitCount, false))
        {
            var bits = new StringBuilder((int)bitCount);

            for (var i = 0; i < bitCount; i++)
                bits.Append((buffer[i / 8] >> (7 - (i % 8))) & 1);

            Console.WriteLine($"[inventory] slot bits ({bitCount}): {bits}"
                + $"  hex {Convert.ToHexString(buffer)}");
        }

        bitStream.SetReadOffset(readOffset);
    }

    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.Read(out int sourceEntityId))
            return false;

        if (!bitStream.Read(out int targetEntityId))
            return false;

        DumpSlotBits(bitStream);

        if (!bitStream.TryReadBitsValue(SlotBits, out var sourceSlot))
            return false;

        if (!bitStream.TryReadBitsValue(CountBits, out var count))
            return false;

        if (!bitStream.TryReadBitsValue(SlotBits, out var targetSlot))
            return false;

        Console.WriteLine($"[inventory] transfer {sourceEntityId}:{sourceSlot} -> {targetEntityId}:{targetSlot} x{count}");

        // Moving to or from an open container: the two entity ids differ. Handled before the
        // player-only path below, which owns the split/merge behaviour that only makes sense
        // within a single inventory.
        if (sourceEntityId != targetEntityId)
        {
            if (!connection.TryGetInventory(sourceEntityId, out var source) ||
                !connection.TryGetInventory(targetEntityId, out var target))
            {
                Console.WriteLine("[inventory] transfer involves an entity with no inventory");

                return true;
            }

            if (connection.TryTransferBetweenInventories(source, (int)sourceSlot, target, (int)targetSlot, (int)count))
                Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

            return true;
        }

        // Same entity on both sides: a rearrange within one inventory. That is the player's
        // rucksack most of the time, but it is also how the client moves an item from one chest
        // square to another, so resolve by id rather than assuming the player.
        if (!connection.TryGetInventory(sourceEntityId, out var inventory))
            return true;

        var slots = inventory.InventoryEntityList;

        if (sourceSlot < 0 || sourceSlot >= slots.Count || targetSlot < 0 || targetSlot >= slots.Count)
        {
            Console.WriteLine($"[inventory] slot out of range (have {slots.Count})");

            return true;
        }

        // A count smaller than the stack is a split, not a move: dragging half of a 50 stack
        // arrives here as count 25. TrySplitStack returns false when this is an ordinary whole
        // stack move, which then falls through to the swap below.
        if (connection.TrySplitStack(sourceSlot, targetSlot, count, inventory))
        {
            Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

            return true;
        }

        // Dropping onto a square holding the same item tops that stack up instead of swapping.
        // Returns false when they are different items or the target is already full, which
        // falls through to the swap below.
        if (connection.TryMergeStack(sourceSlot, targetSlot, count, inventory))
        {
            Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

            return true;
        }

        // Swap rather than overwrite, so dropping onto an occupied slot exchanges the two
        // instead of destroying one.
        (slots[sourceSlot], slots[targetSlot]) = (slots[targetSlot], slots[sourceSlot]);

        // Reassigning raises the change notification; mutating the list alone would not.
        inventory.InventoryEntityList = slots;

        Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

        return true;
    }
}
