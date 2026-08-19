using System;

using RakNet;

using SkySaga.Game.GeoData;
using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// The player selecting a different hotbar square (mouse wheel or the number keys).
/// </summary>
/// <remarks>
/// Two bytes on the wire, so the payload is a single small field: the square's index, in the
/// same 5 bit form <see cref="RequestUISettingsSlotChange"/> uses.
///
/// The server tracks this because placing a block and digging arrive as the same
/// <see cref="PerformVoxelActions"/> packet — which of the two it is depends on whether the
/// selected square holds a placeable block or a tool.
/// </remarks>
public static class RequestUISettingsSetActiveSlot
{
    private const uint SlotBits = 5;

    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.TryReadBitsValue(SlotBits, out var slot))
            return false;

        connection.ActiveHotbarSlot = slot;

        var held = connection.ActiveResource is { } resource &&
                   GeoDataManager.TryGetResource(resource, out var item)
            ? item.Name
            : "(nothing)";

        Console.WriteLine($"[hotbar] active slot {slot} holding {held}");

        return true;
    }
}
