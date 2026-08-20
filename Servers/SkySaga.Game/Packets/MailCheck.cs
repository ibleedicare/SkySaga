using RakNet;

namespace SkySaga.Game.Packets;

/// <summary>
/// "Send me my inbox." Empty body — the client writes no fields at all (<c>FUN_00794bd0</c>),
/// so this is one byte on the wire.
/// </summary>
/// <remarks>
/// Sent when the mailbox panel opens (<c>FUN_00821ee0</c> → <c>FUN_007941e0</c>) and again
/// whenever a <c>NewMailRecieved</c> doorbell arrives. Before this handler existed the packet
/// was logged as unhandled and the panel span on "loading" forever:
///
/// <code>[warn] unhandled packet MailCheck ( Length: 1 ) E6</code>
/// </remarks>
public static class MailCheck
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        connection.SyncMailbox();

        return true;
    }
}
