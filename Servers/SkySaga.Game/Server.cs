using System;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Concurrent;

using RakNet;

using SkySaga.Game.World;
using SkySaga.Game.Packets;
using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;
using SkySaga.Game.Components;

namespace SkySaga.Game;

public class Server : IDisposable
{
    private const int MaxConnections = 100;

    private readonly ushort _port;
    private readonly RakPeerInterface _peer;

    private readonly Dictionary<int, Map> _maps = new();
    private readonly Dictionary<ulong, Connection> _connections = new();

    // Work handed in from other threads (the chat/IRC server) to run on the game thread,
    // where touching connections and entities is safe. Drained each Tick.
    private readonly ConcurrentQueue<Action> _commands = new();

    /// <summary>The first (typically only) connected player, for single-player admin commands.</summary>
    public Connection? FirstConnection => _connections.Values.FirstOrDefault();

    /// <summary>Queue work to run on the game thread at the next tick.</summary>
    public void Enqueue(Action action) => _commands.Enqueue(action);

    public Server(string password, ushort port)
    {
        _port = port;
        _peer = RakPeerInterface.GetInstance();

        /* Causing Crash !?

        #if DEBUG
                var packetLogger = new PacketLogger();
                _peer.AttachPlugin(packetLogger);
        #endif

        */

        _peer.SetIncomingPassword(password, password.Length);
        _peer.SetMaximumIncomingConnections(MaxConnections);

        // TODO: Map system that'll load entities and their states

        var map = new Map(new MapDefinition
        {
            MapSizeChunks = new Vector<int>(4),
            BiomeType = Util.ComputeCrc32("Sky_Island"),
            GameMode = 1
        });

        if (map.TryCreateEntity("AirShip", out var airShip))
        {
            if (airShip.TryGetComponent<TransformComponent>(out var transformComponent))
            {
                transformComponent.Position = new Vector<int>([2000, 70, 629, 0, 0, 0, 0, 0]);
            }
        }

        if (map.TryCreateEntity("TimeOfDay", out var timeOfDay))
        {
            if (timeOfDay.TryGetComponent<ClientTimeOfDayComponent>(out var clientTimeOfDayComponent))
            {
                // Freeze at midday by default: the 64 second day/night cycle otherwise
                // leaves the world dark half the time, which makes terrain work impossible
                // to look at. SKYSAGA_TIME_OF_DAY overrides the value (65536 = full cycle,
                // so 32768 is midday); SKYSAGA_TIME_OF_DAY=cycle restores the cycle.
                var timeOfDaySetting = Environment.GetEnvironmentVariable("SKYSAGA_TIME_OF_DAY");

                var cycling = string.Equals(timeOfDaySetting, "cycle", StringComparison.OrdinalIgnoreCase);

                clientTimeOfDayComponent.StartTimeOfDay =
                    int.TryParse(timeOfDaySetting, out var configuredTime) ? configuredTime : 65536 / 2;

                clientTimeOfDayComponent.FixedTimeOfDay = !cycling;
                clientTimeOfDayComponent.DayNightCycleDuration = 64;
                clientTimeOfDayComponent.RealWorldStartTime = RakNet.RakNet.GetTime();
                clientTimeOfDayComponent.TimeStretch = 64;
                clientTimeOfDayComponent.TimeOfDayOffset = 0;
            }
        }

        if (map.TryCreateEntity("Sheep", out var sheep))
        {
            if (sheep.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([2000, 70, 629, 0, 0, 0, 0, 0]);

            if (sheep.TryGetComponent<ClientHealthComponent>(out var clientHealthComponent))
                clientHealthComponent.HalfHearts = 50;

            if (sheep.TryGetComponent<ClientCharacterPhysicsComponent>(out var clientCharacterPhysicsComponent))
                clientCharacterPhysicsComponent.IsMoveable = true;
        }

        if (map.TryCreateEntity("Bear", out var bear))
        {
            if (bear.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([2200, 70, 629, 0, 0, 0, 0, 0]);
        }

        if (map.TryCreateEntity("Chicken", out var chicken))
        {
            if (chicken.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([2400, 70, 629, 0, 0, 0, 0, 0]);
        }

        if (map.TryCreateEntity("Goat", out var goat))
        {
            if (goat.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([2600, 70, 629, 0, 0, 0, 0, 0]);
        }

        if (map.TryCreateEntity("Knight", out var knight))
        {
            if (knight.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([2800, 70, 629, 0, 0, 0, 0, 0]);
        }

        if (map.TryCreateEntity("Monkey", out var monkey))
        {
            if (monkey.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([3000, 70, 629, 0, 0, 0, 0, 0]);
        }

        if (map.TryCreateEntity("Tree", out var tree))
        {
            if (tree.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
                smoothedTransformComponent.Position = new Vector<int>([3000, 70, 1000, 0, 0, 0, 0, 0]);
        }

        _maps.TryAdd(0, map);
    }

    public bool Start()
    {
        var status = _peer.Startup(MaxConnections, new SocketDescriptor(_port, string.Empty), 1);

        return status == StartupResult.RAKNET_STARTED;
    }

    public void Tick()
    {
        ProcessCommands();

        ProcessPackets();

        ProcessMaps();
        ProcessConnections();
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                command();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[command] failed: {exception.Message}");
            }
        }
    }

    private void ProcessPackets()
    {
        var packet = _peer.Receive();

        if (packet is null)
            return;

        var bitStream = new BitStream(packet.data, packet.length, false);

        var messageId = bitStream.ReadMessageId();

        Console.WriteLine($"[recv] guid {packet.guid.g} msgId {messageId} "
            + $"({(DefaultMessageIDTypes)messageId}) length {packet.length}");

        if (!_connections.TryGetValue(packet.guid.g, out var connection)
            && messageId == (byte)DefaultMessageIDTypes.ID_NEW_INCOMING_CONNECTION)
        {
            // TODO: Decide which map the connection uses
            connection = new Connection(this, _maps[0], packet.guid);

            _connections.TryAdd(packet.guid.g, connection);

            OnConnectionAdded(connection);

            goto Deallocate;
        }

        if (connection is null)
            goto Deallocate;

        if (messageId == (byte)DefaultMessageIDTypes.ID_CONNECTION_LOST ||
            messageId == (byte)DefaultMessageIDTypes.ID_DISCONNECTION_NOTIFICATION)
        {
            if (!_connections.Remove(packet.guid.g, out connection))
                throw new InvalidOperationException();

            OnConnectionRemoved(connection);

            goto Deallocate;
        }

        if (messageId >= (byte)DefaultMessageIDTypes.ID_USER_PACKET_ENUM)
        {
            var packetId = (PacketId)messageId - (byte)DefaultMessageIDTypes.ID_USER_PACKET_ENUM;

            bool handled;

            // A malformed or half-understood packet must never take the whole server down;
            // log it and keep serving.
            try
            {
                handled = connection.ProcessPacket(packetId, bitStream);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[error] handling {packetId} threw: {exception.Message}");

                handled = true;
            }

            if (!handled)
            {
                // Dump the bytes too: these are client -> server packets, so there is no
                // deserializer in the client to read the layout from. Correlating hex
                // against a known action (drag item from slot A to slot B) is how their
                // formats get worked out.
                var bytes = new byte[Math.Min(packet.length, 64u)];

                for (var i = 0; i < bytes.Length; i++)
                    bytes[i] = packet.data[i];

                Console.WriteLine($"[warn] unhandled packet {packetId} ( Length: {packet.length} ) {Convert.ToHexString(bytes)}");
            }
        }

    Deallocate:
        _peer.DeallocatePacket(packet);
    }

    private void ProcessMaps()
    {
        foreach (var map in _maps.ToFrozenSet())
        {
            var entities = map.Value.Entities;

            foreach (var entity in entities)
            {
                if (!entity.SyncRequired)
                    continue;

                var entitySync = new EntitySync
                {
                    Id = entity.Id,
                    SyncData = entity.GetSyncData(newEntity: false)
                };

                SendToAll(entitySync);
            }
        }
    }

    private void ProcessConnections()
    {
        foreach (var connection in _connections.ToFrozenSet())
        {
            connection.Value.Tick();
        }
    }

    public void Send(BitStream bitStream, AddressOrGUID systemIdentifier)
    {
        var sent = _peer.Send(bitStream, PacketPriority.HIGH_PRIORITY, PacketReliability.RELIABLE_ORDERED, (char)0, systemIdentifier, false);

        // bitStream.GetData()[0] is the RakNet message id; game ids start at ID_USER_PACKET_ENUM.
        var messageId = bitStream.GetNumberOfBytesUsed() > 0 ? bitStream.GetData()[0] : (byte)0;
        var packetId = (PacketId)(messageId - (byte)DefaultMessageIDTypes.ID_USER_PACKET_ENUM);

        Console.WriteLine($"[send] {packetId} bytes {bitStream.GetNumberOfBytesUsed()} sent {sent}");
    }

    public void SendToAll(ISerializablePacket packet)
    {
        var bitStream = packet.Serialize();

        foreach (var connection in _connections.ToFrozenDictionary())
            connection.Value.Send(bitStream);
    }

    private void OnConnectionAdded(Connection connection)
    {
        Console.WriteLine($"[conn] client {connection.Guid.g} connected");

        connection.OnConnected();
    }

    private void OnConnectionRemoved(Connection connection)
    {
        _connections.Remove(connection.Guid.g);

        connection.OnDisconnected();
    }

    public void Dispose()
    {
        RakPeerInterface.DestroyInstance(_peer);
    }
}