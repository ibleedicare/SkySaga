using System;

using RakNet;

using SkySaga.Game.Extensions;

using SkySaga.Game.Components;

namespace SkySaga.Game.Packets;

/// <summary>
/// The client asks to equip an item sitting in the rucksack.
/// </summary>
/// <remarks>
/// Distinct from <see cref="InventoryItemTransferToSlot"/>, which only moves items
/// between bag squares. Both address the same 45-entry
/// <c>ClientInventoryComponent.InventoryEntityList</c>, so equipping is just a move into
/// one of the equipment indices.
///
/// A 7 byte packet, e.g. equipping the Head armour in bag slot 9:
/// <code>
/// 93            message id
/// 2             0010     = 2   destination equipment slot (4 bits)
///               ...1010  = 10  entity id                  (32 bits)
///               001001   = 9   source bag slot            (6 bits)
///               100000         unidentified               (6 bits)
/// </code>
/// Layout confirmed by equipping one piece of each armour type in turn, with the source
/// slots known in advance:
/// <code>
/// 9320000000A260   Head  @9  -> equip slot 2
/// 9330000000A2A0   Torso @10 -> equip slot 3
/// 9350000000A2E0   Arms  @11 -> equip slot 5
/// 9340000000A320   Legs  @12 -> equip slot 4
/// </code>
/// Note Arms is 5 and Legs is 4. Earlier code assumed the opposite.
///
/// The trailing 6 bits read 100000 in every capture so far, across all four armour types
/// and two different source slots, so nothing yet indicates what they mean.
/// </remarks>
public static class RequestEquipInventoryItem
{
    /// <summary>Destination equipment slot: 2 Head, 3 Torso, 4 Legs, 5 Arms.</summary>
    private const uint EquipSlotBits = 4;

    /// <summary>Enough to address the 45 inventory slots.</summary>
    private const uint SlotBits = 6;

    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.TryReadBitsValue(EquipSlotBits, out var equipSlot))
            return false;

        // Read as raw bits rather than Read(out int): the field starts 4 bits into the
        // payload, and this keeps the decoding identical to the captures above.
        if (!bitStream.TryReadBitsValue(32, out var entityId))
            return false;

        if (!bitStream.TryReadBitsValue(SlotBits, out var bagSlot))
            return false;

        Console.WriteLine($"[inventory] equip {entityId}:{bagSlot} -> equipment slot {equipSlot}");

        // A single early build appeared to crash the client on equip, which sent this whole
        // effort down an anti-debug/injection path. It does not reproduce: the stock client
        // (no DLL, no patch) equips and unequips every armour slot repeatedly and keeps
        // playing. Root cause of that one-off was never confirmed; the VEH/injection harness
        // (scripts/build-patches.sh, run-client-patched.sh) is ready if it ever recurs.
        if (entityId != connection.Player.Id)
            return true;

        if (!connection.Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return true;

        var slots = inventory.InventoryEntityList;

        if (equipSlot < 0 || equipSlot >= slots.Count || bagSlot < 0 || bagSlot >= slots.Count)
        {
            Console.WriteLine($"[inventory] slot out of range (have {slots.Count})");

            return true;
        }

        // Swap, so whatever was already equipped drops back into the square the new piece
        // came from instead of being destroyed.
        (slots[bagSlot], slots[equipSlot]) = (slots[equipSlot], slots[bagSlot]);

        // Reassigning raises the change notification; mutating the list alone would not.
        inventory.InventoryEntityList = slots;

        Console.WriteLine($"[inventory] now: {connection.DescribeInventory()}");

        return true;
    }
}
