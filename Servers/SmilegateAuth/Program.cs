using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

using SmilegateAuth;
using SmilegateAuth.Packet;

var packetBuffer = new byte[1024];

var tcpListener = new TcpListener(IPAddress.Any, 10106);

tcpListener.Start();

while (true)
{
    var tcpClient = tcpListener.AcceptTcpClient();

    var networkStream = tcpClient.GetStream();

    var readLength = networkStream.Read(packetBuffer, 0, packetBuffer.Length);

    // We need at least 5 bytes to process a packet.
    if (readLength < 5 || packetBuffer[0] != PacketHeader.Magic)
        continue;

    var packetHeader = packetBuffer.ToStructure<PacketHeader>();

    if (packetHeader is null || packetHeader.Length != readLength)
        continue;

    // There is only one packet id we're supposed to handle.
    if (packetHeader.Id != (ushort)PacketId.LoginRequest)
        continue;

    var loginRequest = packetBuffer.ToStructure<LoginRequest>();

    if (loginRequest is null)
        continue;

    Debug.WriteLine(loginRequest, nameof(LoginRequest));

    var loginReply = new LoginReply();

    // Accept any non-empty credentials so the launcher can sign in with any account.
    // Set SKYSAGA_ACCOUNTS=user:pass,other:pass to restrict it to a fixed list.
    var accounts = Environment.GetEnvironmentVariable("SKYSAGA_ACCOUNTS");

    var accepted = string.IsNullOrWhiteSpace(accounts)
        ? !string.IsNullOrWhiteSpace(loginRequest.Username)
        : accounts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => entry.Split(':', 2) is [var user, var password]
                && user == loginRequest.Username && password == loginRequest.Password);

    Console.WriteLine($"[auth] login {loginRequest.Username} -> {(accepted ? "accepted" : "rejected")}");

    loginReply.Result = accepted ? 0 : 1;

    loginReply.Username = loginRequest.Username;
    loginReply.Token = Guid.NewGuid().ToString();

    var loginReplyData = loginReply.ToArray();

    Debug.WriteLine(Convert.ToHexString(loginReplyData), nameof(LoginReply));

    networkStream.Write(loginReplyData);

    tcpClient.Close();
}