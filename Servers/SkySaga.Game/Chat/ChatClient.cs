using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SkySaga.Game.Chat;

/// <summary>
/// One IRC connection. Speaks the client's non-RFC dialect (see <see cref="ChatServer"/>).
/// </summary>
internal sealed class ChatClient
{
    private readonly ChatServer _server;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly object _writeLock = new();

    public string Nick { get; private set; } = string.Empty;
    public string Uuid { get; private set; } = string.Empty;

    private bool _registered;

    public ChatClient(ChatServer server, TcpClient tcp)
    {
        _server = server;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, new UTF8Encoding(false));
    }

    public void SendLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");

        try
        {
            lock (_writeLock)
            {
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void Numeric(string code, string parameters)
        => SendLine($":{ChatServer.Server} {code} {(Nick.Length > 0 ? Nick : "*")} {parameters}");

    public void Run()
    {
        try
        {
            string? line;

            while ((line = _reader.ReadLine()) is not null)
            {
                line = line.Trim();

                if (line.Length == 0)
                    continue;

                Handle(line);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _server.Unregister(this);

            try { _tcp.Close(); } catch { }
        }
    }

    private void Handle(string line)
    {
        var parts = line.Split(' ');
        var command = parts[0].ToUpperInvariant();

        switch (command)
        {
            case "HELLO":
                // Non-standard greeting the client opens with; nothing to answer.
                break;

            case "NICK":
                // A bare NICK arrives a few seconds after registering; answer 431 so the
                // client keeps its existing nickname instead of blanking it.
                if (parts.Length < 2 || parts[1].Length == 0)
                {
                    Numeric("431", ":No nickname given");
                    break;
                }

                Nick = parts[1];

                if (Uuid.Length > 0 && !_registered)
                    Welcome();

                break;

            case "USER":
                // USER <uuid> <mode> * :<realname> — the first field is the character uuid.
                Uuid = parts.Length > 1 ? parts[1] : string.Empty;

                if (Nick.Length > 0 && !_registered)
                    Welcome();

                break;

            case "PING":
                SendLine($":{ChatServer.Server} PONG {ChatServer.Server} :{(parts.Length > 1 ? parts[1] : string.Empty)}");
                break;

            case "JOIN":
                foreach (var channel in (parts.Length > 1 ? parts[1] : string.Empty).Split(','))
                    if (channel.Length > 0)
                        JoinChannel(channel);
                break;

            case "PART":
            {
                var channel = parts.Length > 1 ? parts[1] : string.Empty;
                _server.Part(this, channel);
                SendLine($":{Nick} PART {channel}");
                break;
            }

            case "PRIVMSG":
                HandlePrivmsg(parts);
                break;

            case "QUIT":
                throw new IOException("client quit");

            case "MODE":
                if (parts.Length > 1)
                    Numeric("221", parts[1].StartsWith('#') ? $"{parts[1]} +nt" : "+i");
                break;

            case "WHO":
            case "WHOIS":
            case "USERHOST":
            case "AWAY":
            case "CAP":
                // Acknowledged by ignoring; the client does not depend on these.
                break;

            default:
                Console.WriteLine($"[chat] unhandled: {line}");
                break;
        }
    }

    private void Welcome()
    {
        _registered = true;

        _server.Register(this);

        Numeric("001", $":Welcome to SkySaga chat, {Nick}");
        Numeric("002", $":Your host is {ChatServer.Server}");
        Numeric("003", ":This server was created today");
        Numeric("004", $"{ChatServer.Server} skysaga-emu o o");
        Numeric("375", $":- {ChatServer.Server} Message of the Day -");
        Numeric("372", ":- SkySaga emulator chat. Type /help for admin commands.");
        Numeric("376", ":End of /MOTD command");
    }

    private void JoinChannel(string channel)
    {
        var members = _server.Join(this, channel);

        SendLine($":{Nick} JOIN {channel}");
        Numeric("332", $"{channel} :SkySaga");
        Numeric("353", $"= {channel} :{string.Join(' ', members)}");
        Numeric("366", $"{channel} :End of /NAMES list");

        _server.Relay(channel, $":{Nick} JOIN {channel}", skipNick: Nick);
    }

    private void HandlePrivmsg(string[] parts)
    {
        var target = parts.Length > 1 ? parts[1] : string.Empty;

        // The client omits the leading ':' on the trailing parameter, so take everything
        // after the target and only strip a ':' if one is actually present.
        var text = parts.Length > 2 ? string.Join(' ', parts[2..]) : string.Empty;

        if (text.StartsWith(':'))
            text = text[1..];

        if (target.Length == 0)
            return;

        if (!target.StartsWith('#'))
            return;

        if (text.StartsWith('/'))
        {
            Console.WriteLine($"[chat] command from {Nick} on {target}: {text}");

            _server.HandleCommand(this, target, text);

            return;
        }

        if (text.Length == 0)
            return; // ignore the empty PRIVMSG the client sends when send is clicked with no input

        // Relay to other members only — the client renders its own outgoing message locally.
        _server.Relay(target, $":{Nick}!{Uuid}@{ChatServer.Server} PRIVMSG {target} :{text}", skipNick: Nick);
    }
}
