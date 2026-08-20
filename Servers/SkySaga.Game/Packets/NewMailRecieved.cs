using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;

namespace SkySaga.Game.Packets;

/// <summary>
/// The doorbell: "you have new mail, ask me for it". Spelling follows the client's own RPC name.
/// </summary>
/// <remarks>
/// One string, which the client <b>throws away</b> — its handler (<c>LAB_0073ad30</c> →
/// <c>FUN_00794290</c>) does nothing but send <c>MailCheck</c> straight back, byte-identical to
/// the "panel opened" path. The string is still read off the stream, so it must be present and
/// well-formed; by symmetry with every other mail packet it is the new message's uuid.
/// </remarks>
public class NewMailRecieved : ISerializablePacket
{
    public string MessageUuid = string.Empty;

    public BitStream Serialize()
    {
        var bitStream = new BitStream();

        bitStream.WritePacketId(PacketId.NewMailRecieved);

        bitStream.WriteString(MessageUuid);

        return bitStream;
    }
}
