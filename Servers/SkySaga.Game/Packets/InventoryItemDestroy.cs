using System;
using System.Text;

using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Components;

namespace SkySaga.Game.Packets;

/// <summary>
/// Dropping an item on the rucksack's trash can: destroy some or all of a stack.
/// </summary>
/// <remarks>
/// Client to server, so there is no deserializer in the client to read the layout from. The
/// packet is 7 bytes and the fields match <see cref="InventoryItemTransferToSlot"/>'s, which
/// is the same UI sending a very similar request:
/// <code>
/// B3            message id (ID_USER_PACKET_ENUM + 45)
/// 00 00 00 0A   entity id  (32 bits) - the inventory's owner
/// .. ..         slot       (6 bits)
///               count      (8 bits)   how much of the stack to destroy
/// </code>
/// Set SKYSAGA_PACKET_BITS=1 to dump the trailing bits verbatim if a deletion ever lands on
/// the wrong square — that is how the transfer packet's layout was pinned down.
/// </remarks>
public static class InventoryItemDestroy
{
    private const uint SlotBits = 6;
    private const uint CountBits = 8;

    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.Read(out int entityId))
            return false;

        DumpBits(bitStream);

        if (!bitStream.TryReadBitsValue(SlotBits, out var slot))
            return false;

        if (!bitStream.TryReadBitsValue(CountBits, out var count))
            return false;

        Console.WriteLine($"[inventory] destroy {entityId}:{slot} x{count}");

        if (entityId != connection.Player.Id)
            return true;

        connection.DestroyStack(slot, count);

        return true;
    }

    /// <summary>Log the trailing bits without consuming them, to check the field layout.</summary>
    private static void DumpBits(BitStream bitStream)
    {
        if (Environment.GetEnvironmentVariable("SKYSAGA_PACKET_BITS") != "1")
            return;

        var readOffset = bitStream.GetReadOffset();

        var bitCount = bitStream.GetNumberOfUnreadBits();

        if (bitCount == 0 || bitCount > 256)
            return;

        var buffer = new byte[(bitCount + 7) / 8];

        if (bitStream.ReadBits(buffer, bitCount, false))
        {
            var bits = new StringBuilder((int)bitCount);

            for (var i = 0; i < bitCount; i++)
                bits.Append((buffer[i / 8] >> (7 - (i % 8))) & 1);

            Console.WriteLine($"[inventory] destroy bits ({bitCount}): {bits}");
        }

        bitStream.SetReadOffset(readOffset);
    }
}
