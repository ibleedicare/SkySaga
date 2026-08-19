using System;
using System.Diagnostics;

using RakNet;

using SkySaga.Game.GeoData;
using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

public static class ClientConnected
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        var clientVersionNumber = bitStream.ReadString();

        Debug.WriteLine($"{nameof(clientVersionNumber)}: {clientVersionNumber}", nameof(ClientConnected));

        // The world's type (home / quest / pvp / sandbox) is decided by the Adventure the
        // client resolves from ServerAdventureCrc: every world is a GeoData Adventure whose
        // nested WorldType picks the mode (1=home, 2=quest, 3=PVP, 5=sandbox). Choose it with
        // SKYSAGA_ADVENTURE (default the home island); the biome is separate (SKYSAGA_BIOME).
        var adventureName = Environment.GetEnvironmentVariable("SKYSAGA_ADVENTURE") is { Length: > 0 } configured
            ? configured
            : "Home_Island_Adventure";

        var adventureCrc = Util.ComputeCrc32(adventureName);
        var worldType = 1;

        if (GeoDataManager.TryGetAdventure(adventureName, out var adventure))
        {
            adventureCrc = adventure.NameHash;
            worldType = adventure.WorldType;
        }
        else
        {
            Console.WriteLine($"[world] adventure '{adventureName}' is not in GeoData; sending its CRC anyway");
        }

        var isHomeIsland = worldType == 1;

        var serverInfo = new ServerInfo
        {
            ServerOwnerGuid = "482f2571-e9a6-4f52-97bd-2231a87a9f9a",
            ServerOwnerName = "EDITz",
            ServerBiome = Environment.GetEnvironmentVariable("SKYSAGA_BIOME") is { Length: > 0 } biome ? biome : "Desert",
            ServerAdventureCrc = adventureCrc,
            // You own (and can freely edit) a home island; quest/pvp/sandbox worlds are not
            // "your world". IsHomeWorld is left false to match the known-good home config.
            IsMyWorld = isHomeIsland,
            ChatHost = Environment.GetEnvironmentVariable("SKYSAGA_CHAT_HOST") ?? "127.0.0.1",
            // 444 is privileged and nothing implements chat yet; a high port lets
            // tools/chat-capture.py sit here and record what ClientChatService sends.
            ChatPort = ushort.TryParse(Environment.GetEnvironmentVariable("SKYSAGA_CHAT_PORT"), out var chatPort) ? chatPort : (ushort)4444
        };

        Console.WriteLine($"[world] adventure={adventureName} worldType={worldType} ({WorldTypeName(worldType)}) "
            + $"biome={serverInfo.ServerBiome} isMyWorld={serverInfo.IsMyWorld}");

        connection.Send(serverInfo);

        connection.Send(connection.Map.Definition);

        return true;
    }

    private static string WorldTypeName(int worldType) => worldType switch
    {
        0 => "character-creator",
        1 => "home-island",
        2 => "adventure/quest",
        3 => "pvp",
        5 => "sandbox/test",
        _ => "unknown"
    };
}