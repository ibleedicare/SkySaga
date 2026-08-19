using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;

namespace SkySaga.Game.Packets;

/// <summary>
/// Marks the tutorial as finished (packet id 0x1c).
/// </summary>
/// <remarks>
/// PacketId.cs annotates this as <c>[C ?? S]</c> — direction unknown — and the emulator
/// keeps no tutorial state, so the client stays in tutorial mode and pushes hint text
/// ("Discover the hidden secrets of the world!") into the chat log. Sending it with an
/// empty body is an experiment: if the client has no handler for the id it simply drops
/// the packet. Disable with SKYSAGA_FINISH_TUTORIAL=0.
/// </remarks>
public class DebugRequestFinishTutorial : ISerializablePacket
{
    public BitStream Serialize()
    {
        var bitStream = new BitStream();

        bitStream.WritePacketId(PacketId.DebugRequestFinishTutorial);

        return bitStream;
    }
}
