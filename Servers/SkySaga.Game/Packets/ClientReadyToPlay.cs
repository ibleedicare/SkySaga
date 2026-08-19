using System;
using System.Diagnostics;

using RakNet;

namespace SkySaga.Game.Packets;

public static class ClientReadyToPlay
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        Debug.WriteLine("", nameof(ClientReadyToPlay));

        var setClientEntity = new SetClientEntity
        {
            EntityId = connection.Player.Id
        };

        connection.Send(setClientEntity);

        // The client otherwise stays in tutorial mode and spills hint text into the chat
        // log. Experimental — see DebugRequestFinishTutorial. SKYSAGA_FINISH_TUTORIAL=0 off.
        if (Environment.GetEnvironmentVariable("SKYSAGA_FINISH_TUTORIAL") != "0")
        {
            Console.WriteLine("[tutorial] sending DebugRequestFinishTutorial");

            connection.Send(new DebugRequestFinishTutorial());
        }

        return true;
    }
}