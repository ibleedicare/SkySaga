using System;
using System.Collections.Generic;
using System.Text;

using RakNet;

using SkySaga.Game.GeoData;
using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// The client rebinding its hotbar: sent when an item is dragged onto a hotbar square.
/// </summary>
/// <remarks>
/// The hotbar is not storage. The Player's <c>clientuisettingscomponent</c> holds
/// <c>hotbarslotresources</c> (syncindex 34) and <c>activeslot</c> (syncindex 2), and the
/// name says what the entries are: **resources**, i.e. item name hashes, not entity ids. So
/// binding an item to the hotbar legitimately leaves it in the rucksack — what looks like a
/// duplicate is one item referenced from two places.
///
/// Layout, decoded by dumping the payload and sliding a 32 bit window over every bit offset
/// until a known resource hash appeared (Dirt at bit 5), then reading the tail as text:
/// <code>
///   slot          5 bits    hotbar square
///   resource     32 bits    item name hash (GeoData Resources)
///   unknown       4 bits    zero in every capture
///   itemUUID     string     standard framing: hasData, largeLength, 8 bit length, chars
/// </code>
/// Two captures that differed only in the hotbar square (1 then 3) confirmed the slot field;
/// both carried the same Dirt hash and the same item UUID.
/// </remarks>
public static class RequestUISettingsSlotChange
{
    /// <summary>Enough for the hotbar's squares.</summary>
    private const uint SlotBits = 5;

    /// <summary>Always zero in every capture so far; purpose unknown.</summary>
    private const uint UnknownBits = 4;

    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.TryReadBitsValue(SlotBits, out var slot))
            return false;

        if (!bitStream.TryReadBitsValue(32, out var resourceHash))
            return false;

        if (!bitStream.TryReadBitsValue(UnknownBits, out _))
            return false;

        var itemUuid = bitStream.ReadString();

        var name = GeoDataManager.TryGetResource((uint)resourceHash, out var resource)
            ? resource.Name
            : $"0x{resourceHash:x8}";

        // Remembered so the server knows what the player is holding: placing and digging are
        // the same packet, and the difference is whether the active hotbar square holds a
        // placeable block or a tool.
        connection.HotbarBindings[slot] = (uint)resourceHash;

        // A fresh bind is also what the player just selected — the client does not always
        // follow it with a SetActiveSlot.
        connection.ActiveHotbarSlot = slot;

        // Not synced back: the hotbar lives in the Player's clientuisettingscomponent
        // (hotbarslotresources, syncindex 34) and the client keeps its own copy, so echoing a
        // wrongly-encoded list would be worse than staying quiet until that format is known.
        Console.WriteLine($"[hotbar] slot {slot} bound to {name} (item {itemUuid})");

        return true;
    }
}
