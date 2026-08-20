using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;

namespace SkySaga.Game.Packets;

/// <summary>
/// "Your inbox is up to date" — the packet that stops the mailbox panel saying "loading".
/// </summary>
/// <remarks>
/// Empty body: the client's deserialiser <c>FUN_0073d300</c> reads nothing at all and just
/// invokes the handler, which fires the panel's completion callback (<c>FUN_00821ec0</c>) and
/// sets <c>panel+0x1a4 = 1</c>. Until that flag is set the panel renders the <c>mailLoading</c>
/// state and draws no rows — <b>even if <c>mailitemlist</c> was synced perfectly</b>.
///
/// Must be sent after the <c>EntitySync</c> carrying the list. The channel is
/// RELIABLE_ORDERED, so ordering the two sends is enough.
/// </remarks>
public class RemoteMailSynced : ISerializablePacket
{
    public BitStream Serialize()
    {
        var bitStream = new BitStream();

        bitStream.WritePacketId(PacketId.RemoteMailSynced);

        return bitStream;
    }
}
