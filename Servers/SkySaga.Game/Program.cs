using System;
using System.IO;
using System.Threading;

using RakNet;

using SkySaga.Game;
using SkySaga.Game.GeoData;

var keepRunning = true;

Console.CancelKeyPress += delegate
{
    keepRunning = false;
};

if (!File.Exists("RakNet.dll"))
{
    Console.WriteLine("""
        The RakNet DLL is missing.
        Press any key to continue . . .
        """);

    Console.ReadKey();

    return;
}

try
{
    _ = new RakString();
}
catch
{
    Console.WriteLine("""
        RakNet DLL issue.
        Most likely the provided DLL wasn't build with the C# wrapper file.
        Press any key to continue . . .
        """);

    Console.ReadKey();

    return;
}

// Load the client's item table up front so a missing or broken Data/GeoData.json is
// reported at startup rather than on the first player connect.
GeoDataManager.Touch();

ushort port = 42069;

using var server = new Server("Something about penguins\0", port);

if (!server.Start())
{
    Console.WriteLine("""
        Failed to start server.
        Press any key to continue . . .
        """);

    Console.ReadKey();

    return;
}

Console.WriteLine($"Server has started on port {port}.");

// The client's chat/IM service (IRC on the chatPort advertised in ServerInfo), which also
// runs admin commands like "/give dirt 3". Must match ClientConnected's SKYSAGA_CHAT_PORT.
var chatPort = ushort.TryParse(Environment.GetEnvironmentVariable("SKYSAGA_CHAT_PORT"), out var parsedChatPort)
    ? parsedChatPort
    : (ushort)4444;

new SkySaga.Game.Chat.ChatServer(server, chatPort).Start();

while (keepRunning)
{
    server.Tick();

    Thread.Sleep(30);
}

Console.WriteLine("""
        Server has stopped.
        Press any key to continue . . .
        """);

Console.ReadKey();