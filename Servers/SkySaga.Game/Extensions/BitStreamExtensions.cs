using System;
using System.Text;
using System.Collections;

using RakNet;

namespace SkySaga.Game.Extensions;

public static class BitStreamExtensions
{
    #region Read

    public static int ReadMessageId(this BitStream bitStream)
    {
        bitStream.Read(out byte messageIdPart);

        if (messageIdPart != byte.MaxValue)
            return messageIdPart;

        bitStream.Read(out messageIdPart);

        // Extended (2-byte) id: WritePacketId emits 0xFF then (packetIndex - 121) for
        // indices >= 121. The decoded message id must be 255 + that second byte, so that
        // ProcessPackets' `(PacketId)messageId - ID_USER_PACKET_ENUM` (with its byte wrap)
        // recovers the real index. The old form subtracted ID_USER_PACKET_ENUM a second
        // time, collapsing every extended packet (e.g. NotifyPhotoCaptured = 150) onto a
        // low id (16 = RequestUISettingsSetActiveSlot).
        return byte.MaxValue + messageIdPart;
    }

    public static string ReadString(this BitStream bitStream)
    {
        var hasData = bitStream.ReadBit();

        if (!hasData)
            return string.Empty;

        var largeLength = bitStream.ReadBit();

        if (largeLength)
            throw new NotImplementedException();

        var lengthData = new byte[1];

        if (!bitStream.ReadBits(lengthData, 8u))
            return string.Empty;

        var length = lengthData[0];

        var stringData = new byte[length];

        if (!bitStream.ReadBits(stringData, length * 8u))
            return string.Empty;

        return Encoding.UTF8.GetString(stringData);
    }

    #endregion

    #region Write

    public static void WritePacketId(this BitStream bitStream, PacketId packetIdEnum)
    {
        var packetId = (byte)packetIdEnum;

        var packetIdPart = (byte)(packetId - (byte.MaxValue + 1 - (byte)DefaultMessageIDTypes.ID_USER_PACKET_ENUM));

        if (packetId + (byte)DefaultMessageIDTypes.ID_USER_PACKET_ENUM >= byte.MaxValue)
        {
            bitStream.Write(byte.MaxValue);
            packetIdPart++;
        }

        bitStream.Write(packetIdPart);
    }

    public static void WriteUInt64(this BitStream bitStream, ulong value)
    {
        bitStream.WriteBits(BitConverter.GetBytes(value), sizeof(ulong) * 8, true);
    }

    /// <summary>
    /// The client's small-int-optimised uint used by the photo packets (FUN_00791210 /
    /// FUN_0073dab0): one flag bit, then 7 bits if the value is &lt; 128, else the full 32.
    /// </summary>
    public static void WriteCompressedUInt(this BitStream bitStream, uint value)
    {
        if (value < 0x80)
        {
            bitStream.Write0();
            bitStream.WriteBits(BitConverter.GetBytes(value), 7, true);
        }
        else
        {
            bitStream.Write1();
            bitStream.WriteBits(BitConverter.GetBytes(value), 32, true);
        }
    }

    public static uint ReadCompressedUInt(this BitStream bitStream)
    {
        var large = bitStream.ReadBit();

        var buffer = new byte[4];

        bitStream.ReadBits(buffer, large ? 32u : 7u, true);

        return BitConverter.ToUInt32(buffer, 0);
    }

    public static void WriteString(this BitStream bitStream, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            bitStream.Write0();

            return;
        }

        bitStream.Write1();

        var bytes = Encoding.UTF8.GetBytes(value);

        var length = bytes.Length;

        if (length < 255)
        {
            bitStream.Write0();
            bitStream.Write((byte)length);
        }
        else
        {
            bitStream.Write1();
            bitStream.Write(length);
        }

        bitStream.WriteBits(bytes, (uint)length * 8u, true);
    }

    public static void WriteOptional<T>(this BitStream bitStream, T? value, Action<T> write) where T : struct
    {
        bitStream.Write(value.HasValue);

        if (value.HasValue)
            write(value.Value);
    }

    public static void WriteOptional<T>(this BitStream bitStream, T? value, Action<T> write) where T : class
    {
        bitStream.Write(value is not null);

        if (value is not null)
            write(value);
    }

    public static void WriteGuid(this BitStream bitStream, Guid? value)
    {
        bitStream.WriteOptional(value, (value) =>
        {
            var guidBytes = value.ToByteArray();

            bitStream.Write(guidBytes, (uint)guidBytes.Length);
        });
    }

    public static void WriteParameterFlags(this BitStream bitStream, int count, params int[] flags)
    {
        var data = new byte[16];

        var bitArray = new BitArray(count);

        foreach (var flag in flags)
            bitArray.Set(flag, true);

        bitArray.CopyTo(data, 0);

        for (int i = 0; i < count / 32; i++)
            Array.Reverse(data, i * 4, 4);

        bitStream.WriteBits(data, (uint)count);
    }

    /// <summary>
    /// Reads an integer of an arbitrary bit width.
    /// </summary>
    /// <remarks>
    /// <c>ReadBits</c> fills the buffer most significant byte first, with only the final
    /// partial byte right aligned, so reinterpreting the buffer with BitConverter only
    /// works when the width is a multiple of eight. Reading 12 bits of the value 19 gives
    /// the bytes 01 03, which BitConverter reads as 769.
    /// </remarks>
    public static bool TryReadBitsValue(this BitStream bitStream, uint numberOfBits, out int value)
    {
        value = 0;

        var buffer = new byte[(numberOfBits + 7) / 8];

        if (!bitStream.ReadBits(buffer, numberOfBits, true))
            return false;

        var wholeBytes = (int)(numberOfBits / 8);
        var remainingBits = (int)(numberOfBits % 8);

        for (var i = 0; i < wholeBytes; i++)
            value = (value << 8) | buffer[i];

        if (remainingBits > 0)
            value = (value << remainingBits) | buffer[wholeBytes];

        return true;
    }

    #endregion
}