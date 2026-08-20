using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// Claim one attachment into the rucksack. Two strings: the message uuid and the item's uuid
/// (<c>FUN_00795900</c>).
/// </summary>
/// <remarks>
/// The client blanks its own copy of the slot optimistically before sending, so silence here
/// does not leave the icon on screen — it leaves the item <em>nowhere</em> until the next
/// re-bind puts it back. The server's move and re-sync are what make the claim stick.
/// </remarks>
public static class TakeMailAttachment
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        var messageUuid = bitStream.ReadString();
        var itemUuid = bitStream.ReadString();

        connection.ClaimMailAttachment(messageUuid, itemUuid);

        return true;
    }
}
