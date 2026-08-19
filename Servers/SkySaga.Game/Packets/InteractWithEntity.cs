using System;

using RakNet;

namespace SkySaga.Game.Packets;

/// <summary>
/// Sent by the client when the player interacts with an entity (RPCInteractWithEntity):
/// the interactor id and the target id. For a loot chest the client opens the chest UI from
/// the already-synced inventory; this just records that it happened.
/// </summary>
public static class InteractWithEntity
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        if (!bitStream.Read(out int interactingEntityId))
            return false;

        if (!bitStream.Read(out int targetEntityId))
            return false;

        var target = connection.Map.TryGetEntity(targetEntityId, out var entity) ? entity.Name : "?";

        Console.WriteLine($"[interact] entity {interactingEntityId} -> {targetEntityId} ({target})");

        return true;
    }
}
