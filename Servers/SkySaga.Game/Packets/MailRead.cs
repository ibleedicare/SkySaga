using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// The player opened a message. One string, the message uuid (<c>FUN_00795270</c>).
/// </summary>
/// <remarks>
/// The client sets its own read bit <em>before</em> sending (<c>FUN_007942d0</c>), so there is
/// no reply to make — but the server must persist it, because the next re-sync of
/// <c>mailitemlist</c> with the bit clear would pop the message back to unread.
/// </remarks>
public static class MailRead
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        connection.MarkMailRead(bitStream.ReadString());

        return true;
    }
}
