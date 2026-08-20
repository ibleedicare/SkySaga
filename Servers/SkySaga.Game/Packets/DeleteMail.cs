using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// Discard a message. One string, the message uuid (<c>FUN_007956d0</c>).
/// </summary>
/// <remarks>
/// The client counts unclaimed attachments first and raises a confirmation dialog when there
/// are any, so this only ever arrives for a discard the player agreed to. It does <b>not</b>
/// remove the row itself — the row disappears when the server re-syncs the list without it.
/// </remarks>
public static class DeleteMail
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        connection.DeleteMailMessage(bitStream.ReadString());

        return true;
    }
}
