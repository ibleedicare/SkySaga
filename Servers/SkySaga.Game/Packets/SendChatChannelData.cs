using System;

using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;

namespace SkySaga.Game.Packets;

/// <summary>
/// Reply to <see cref="PacketId.RequestChatChannelData"/>: the chat channels the client
/// should use on the IRC service advertised in <see cref="ServerInfo"/>.
/// </summary>
/// <remarks>
/// Layout reversed from the client's deserializer (FUN_007430b0, called by the packet's
/// handler FUN_00743550 which logs "RPCSendChatChannelData, %s"):
/// <code>
/// count                                   32 - NumBitsRequired(8) bits, right aligned
/// per channel:
///     type                                32 - NumBitsRequired(8) bits, right aligned
///     name                                string
///     flag                                1 bit
/// trailing string x2                      read after the array by the packet handler
/// </code>
/// The consumer (FUN_0077b7f0) only acts on channels whose type is 0 or 3; type 8 is
/// skipped. It also requires the chat service to be in state 3 with chat enabled, else it
/// logs "Not yet ready to receive channels" / "Not yet enabled chat".
/// </remarks>
public class SendChatChannelData : ISerializablePacket
{
    public record Channel(int Type, string Name, bool Flag);

    public Channel[] Channels = [];

    /// <summary>Two strings the client reads after the channel array; purpose unconfirmed.</summary>
    public string? Trailing1;
    public string? Trailing2;

    public BitStream Serialize()
    {
        var bitStream = new BitStream();

        bitStream.WritePacketId(PacketId.SendChatChannelData);

        bitStream.WriteBits(BitConverter.GetBytes(Channels.Length), 32 - Util.NumBitsRequiredUInt32(8), true);

        foreach (var channel in Channels)
        {
            bitStream.WriteBits(BitConverter.GetBytes(channel.Type), 32 - Util.NumBitsRequiredUInt32(8), true);

            bitStream.WriteString(channel.Name);

            if (channel.Flag)
                bitStream.Write1();
            else
                bitStream.Write0();
        }

        bitStream.WriteString(Trailing1);
        bitStream.WriteString(Trailing2);

        return bitStream;
    }
}
