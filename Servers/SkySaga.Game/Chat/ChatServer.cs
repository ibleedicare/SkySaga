using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using SkySaga.Game.GeoData;

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

            case "/ore":
            {
                // Defaults to the carbon seam table, the one ore drop with a distinct item.
                var table = parts.Length >= 2 ? parts[1] : "CarbonOre_Seam_LootTable";

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnResourceNode(table);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/drop":
            {
                if (parts.Length < 2)
                {
                    Reply(client, channel, "usage: /drop <item> [count]");
                    return;
                }

                var item = parts[1];
                var count = parts.Length >= 3 && int.TryParse(parts[2], out var dropped) ? dropped : 1;

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.DropPickupInFront(item, count);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/next":
            case "/prev":
            {
                // /next            -> remove the interactable under test, spawn the next one
                // /prev            -> the previous one
                // /next Workbench  -> jump straight to the first match
                //
                // One at a time, because a grid of all fifty collides with itself: an Airship is
                // vehicle-sized and most devices are 2x2x3 voxel blocks.
                var jumpTo = parts.Length > 1 ? parts[1] : null;

                var step = verb == "/prev" ? -1 : 1;

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnNextInteractable(jumpTo, step);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/spawnall":
            {
                // /spawnall                -> one of every entity with an interaction component
                // /spawnall Chest          -> only those whose name contains "Chest"
                // /spawnall @crafting      -> sweep a different component instead
                //
                // The point is coverage: fifty interactables laid out on a grid, each logged
                // with its grid coordinate, so one walk tells us what opens and what does not.
                var component = "interaction";

                var args = parts.Skip(1).ToList();

                if (args.Count > 0 && args[0].StartsWith('@'))
                {
                    component = args[0][1..];

                    args.RemoveAt(0);
                }

                var nameFilter = args.Count > 0 ? args[0] : null;

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnInteractableGrid(component, nameFilter);

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
                // /chest                       -> a `Chest`, empty
                // /chest Dirt:10 ..            -> also fills the inventory (item or item:count)
                // /chest @ChestMinorLootPvP .. -> spawn a different chest entity
                //
                // The @name form exists to test chest variants against each other. The three
                // chests that declare no clientpickupcomponent (ChestMinorLootPvP,
                // Chest_Adventure_End, Chest_Christmas_Minor) are the useful controls: the
                // server has no class for that component, so on a plain `Chest` four of its
                // parameters can never be sent. See documentations/interactables.md.
                var entityName = "Chest";

                var args = parts.Skip(1).ToList();

                if (args.Count > 0 && args[0].StartsWith('@'))
                {
                    entityName = args[0][1..];
                    args.RemoveAt(0);
                }

                var loot = args.Select(entry =>
                {
                    var bits = entry.Split(':', 2);
                    return (Name: bits[0], Count: bits.Length > 1 && int.TryParse(bits[1], out var c) ? c : 1);
                }).ToList();

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.SpawnChest(loot, entityName);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/mail":
            {
                // /mail <subject> | <body> [item[:count] ...]
                //
                // Sends a message to yourself, so the whole loop — doorbell, MailCheck, sync,
                // read, claim, delete — is testable with one player. The pipe separates subject
                // from body because both are free text; anything after the body's first token
                // that looks like item[:count] becomes an attachment.
                var rest = command["/mail".Length..].Trim();

                if (rest.Length == 0)
                {
                    Reply(client, channel, "usage: /mail [@Entity] <subject> | <body> [item[:count]...]");
                    return;
                }

                // /mail @Chest ... swaps the attachment container. MailItem is the right entity;
                // this is the A/B for "does the client instantiate MailItem at all".
                var containerEntity = "MailItem";

                if (rest.StartsWith('@'))
                {
                    var split = rest.IndexOf(' ');

                    containerEntity = split < 0 ? rest[1..] : rest[1..split];
                    rest = split < 0 ? string.Empty : rest[(split + 1)..].Trim();
                }

                var halves = rest.Split('|', 2);

                var subject = halves[0].Trim();

                var bodyAndItems = halves.Length > 1
                    ? halves[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    : [];

                // An attachment is a trailing token naming a real resource; everything before
                // the first of those is the body.
                var attachments = new List<(string Name, int Count)>();

                var bodyWords = new List<string>();

                foreach (var word in bodyAndItems)
                {
                    var bits = word.Split(':', 2);

                    if (attachments.Count > 0 || GeoDataManager.TryGetResource(bits[0], out _))
                    {
                        attachments.Add((bits[0],
                            bits.Length > 1 && int.TryParse(bits[1], out var c) ? c : 1));

                        continue;
                    }

                    bodyWords.Add(word);
                }

                var body = string.Join(' ', bodyWords);

                _game.Enqueue(() =>
                {
                    var connection = _game.FirstConnection;

                    var result = connection is null
                        ? "no player is online"
                        : connection.ComposeMail(subject, body, attachments, containerEntity);

                    Reply(client, channel, result);
                });

                break;
            }

            case "/help":
                Reply(client, channel, "commands: /give <item> [count], /spawn <entity>, /chest [@Entity] [item[:count]...]");
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
