using System;

using RakNet;

namespace SkySaga.Game.Packets;

/// <summary>
/// The client asks for its chat channels once the chat service is up (payload is empty).
/// </summary>
public static class RequestChatChannelData
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        Console.WriteLine("[chat] RequestChatChannelData -> SendChatChannelData");

        // The client acts on channels of type 0 and 3 and skips type 8; the types appear to
        // select which chat tab a channel feeds. Configure as "type:name" entries, e.g.
        // SKYSAGA_CHAT_CHANNELS=0:global,3:local — note the client prefixes '#' itself.
        var configured = Environment.GetEnvironmentVariable("SKYSAGA_CHAT_CHANNELS");

        var entries = string.IsNullOrWhiteSpace(configured)
            ? ["0:global"]
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var channels = new SendChatChannelData.Channel[entries.Length];

        for (var i = 0; i < entries.Length; i++)
        {
            var parts = entries[i].Split(':', 2);

            var type = parts.Length == 2 && int.TryParse(parts[0], out var parsed) ? parsed : 0;
            var name = parts.Length == 2 ? parts[1] : parts[0];

            channels[i] = new SendChatChannelData.Channel(type, name, true);
        }

        connection.Send(new SendChatChannelData
        {
            Channels = channels
        });

        return true;
    }
}
