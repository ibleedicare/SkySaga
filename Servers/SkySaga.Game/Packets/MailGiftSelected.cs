using System;

using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// The player picked one of the gift options. One string, the message uuid
/// (<c>FUN_007954a0</c>).
/// </summary>
/// <remarks>
/// <b>It does not say which gift.</b> The UI callback <c>FUN_007c5900(panel, index)</c> gets the
/// clicked index, highlights the button and sends this — the index never reaches the wire. The
/// choice is committed by the <c>TakeMailAttachment</c> that follows, whose <c>itemUUID</c> does
/// identify the item, so all this packet means is "a choice was made": it sets flag bit 3, which
/// is what stops the client offering the buttons again.
///
/// Nothing here generates gift mail yet, so this is recorded and logged rather than acted on.
/// </remarks>
public static class MailGiftSelected
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        connection.MarkMailGiftChosen(bitStream.ReadString());

        return true;
    }
}
