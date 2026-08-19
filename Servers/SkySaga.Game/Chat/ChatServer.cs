using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SkySaga.Game.Chat;

/// <summary>
/// The client's chat/IM service: a minimal IRC server it connects to on the chatHost/
/// chatPort advertised in <c>ServerInfo</c>. Ported from tools/chat-server.py into the
/// emulator so chat lines can run <b>admin commands</b> against live game state — e.g.
/// <c>/give dirt 3</c> drops three Dirt into the player's rucksack.
/// </summary>
/// <remarks>
/// The client speaks a non-RFC dialect: it opens with <c>HELLO</c>, sends
/// <c>NICK</c>/<c>USER &lt;uuid&gt;</c>, prefixes <c>#</c> to channel names itself, and omits
/// the leading <c>:</c> on a PRIVMSG body. It also renders its own outgoing messages
/// locally, so we relay to <b>other</b> members only.
///
/// Runs on its own thread; commands touch game state, so they are handed to the game
/// thread via <see cref="Server.Enqueue"/> rather than executed here.
/// </remarks>
public sealed class ChatServer
{
    private const string ServerName = "skysaga.local";

    private readonly Server _game;
    private readonly ushort _port;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _channels = new(StringComparer.OrdinalIgnoreCase);

    public ChatServer(Server game, ushort port)
    {
        _game = game;
        _port = port;
    }

    public void Start()
    {
        var thread = new Thread(Listen) { IsBackground = true, Name = "chat-server" };

        thread.Start();
    }

    private void Listen()
    {
        var listener = new TcpListener(IPAddress.Any, _port);

        listener.Start();

        Console.WriteLine($"[chat] IRC server listening on {_port}");

        while (true)
        {
            var tcp = listener.AcceptTcpClient();

            var client = new ChatClient(this, tcp);

            var thread = new Thread(client.Run) { IsBackground = true };

            thread.Start();
        }
    }

    // ---------------------------------------------------------------- registry

    internal void Register(ChatClient client)
    {
        lock (_lock)
            _clients[client.Nick] = client;

        Console.WriteLine($"[chat] registered {client.Nick} (uuid {client.Uuid})");
    }

    internal void Unregister(ChatClient client)
    {
        lock (_lock)
        {
            _clients.Remove(client.Nick);

            foreach (var members in _channels.Values)
                members.Remove(client.Nick);
        }
    }

    internal IReadOnlyList<string> Join(ChatClient client, string channel)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(channel, out var members))
                _channels[channel] = members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            members.Add(client.Nick);

            return members.ToList();
        }
    }

    internal void Part(ChatClient client, string channel)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(channel, out var members))
                members.Remove(client.Nick);
        }
    }

    /// <summary>Relay a raw line to a channel's members (optionally skipping the sender).</summary>
    internal void Relay(string channel, string line, string? skipNick = null)
    {
        List<ChatClient> targets;

        lock (_lock)
        {
            if (!_channels.TryGetValue(channel, out var members))
                return;

            targets = members
                .Where(nick => skipNick is null || !string.Equals(nick, skipNick, StringComparison.OrdinalIgnoreCase))
                .Select(nick => _clients.TryGetValue(nick, out var c) ? c : null)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();
        }

        foreach (var target in targets)
            target.SendLine(line);
    }

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Handle a chat line that starts with '/'. Runs on the client's thread; anything that
    /// touches game state is queued onto the game thread and the reply is sent back to the
    /// same channel from a "Server" nick.
    /// </summary>
    internal void HandleCommand(ChatClient client, string channel, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "/give":
            {
                if (parts.Length < 2)
                {
                    Reply(client, channel, "usage: /give <item> [count]");
                    return;
                }

                var item = parts[1];
                var count = parts.Length >= 3 && int.TryParse(parts[2], out var parsed) ? parsed : 1;

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.GiveItem(item, count);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/spawn":
            {
                if (parts.Length < 2)
                {
                    Reply(client, channel, "usage: /spawn <entity>");
                    return;
                }

                var entity = parts[1];

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnEntity(entity);

                    Reply(client, channel, result);
                });

                break;
            }

            // Diagnostic: spawn with ONLY the transform synced, bypassing the voxel-block
            // guard. Tells us whether a block hangs the client because of a parameter we send
            // or because its `voxels` grid is missing.
            case "/spawnmin":
            {
                if (parts.Length < 2)
                {
                    Reply(client, channel, "usage: /spawnmin <entity>");
                    return;
                }

                var entity = parts[1];

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnEntity(entity, minimal: true);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/chest":
            {
                // /chest            -> empty chest (minimal: interaction + transform only)
                // /chest Dirt:10 .. -> also fills the inventory (each arg item or item:count)
                var loot = parts.Skip(1).Select(entry =>
                {
                    var bits = entry.Split(':', 2);
                    return (Name: bits[0], Count: bits.Length > 1 && int.TryParse(bits[1], out var c) ? c : 1);
                }).ToList();

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnChest(loot);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/help":
                Reply(client, channel, "commands: /give <item> [count], /spawn <entity>, /chest [item[:count]...]");
                break;

            default:
                Reply(client, channel, $"unknown command {verb} (try /help)");
                break;
        }
    }

    /// <summary>Send a line into the channel as if from a "Server" user, so it renders as a normal message.</summary>
    private void Reply(ChatClient client, string channel, string text)
    {
        client.SendLine($":Server!server@{ServerName} PRIVMSG {channel} :{text}");
    }

    internal static string Server => ServerName;
}
