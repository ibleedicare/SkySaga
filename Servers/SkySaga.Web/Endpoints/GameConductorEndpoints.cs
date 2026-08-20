using System;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace SkySaga.Web.Endpoints;

public static class GameConductorEndpoints
{
    public record GameConductorReserve(int Character, Guid ImUuid);

    // Address handed to the client for the web/game servers. Must be reachable
    // from wherever the client runs (a VM or container sees no 127.0.0.1 of ours).
    private static readonly string PublicIp =
        Environment.GetEnvironmentVariable("SKYSAGA_PUBLIC_IP") ?? "127.0.0.1";

    public static void MapGameConductorEndpoints(this WebApplication app)
    {
        app.MapGet("/api/game-conductor/geonode", () => new
        {
            result = new[]
            {
                new
                {
                    uuid = Guid.NewGuid(),
                    datacentre = "UK",
                    ip = PublicIp,
                    port = 5164
                }
            }
        });

        // Ping-test results. Build 10414 posts these to /api/matchmaking/userdatacentre/create
        // (see MatchMakingEndpoints); by Alpha V10 b36731 the route had moved under
        // game-conductor, as RPC `HTTPRPCSendPingTestResults`. Both are served so either
        // client works. The client sends the datacentre list from its geonode ping tests.
        app.MapPost("/api/game-conductor/userdatacentre/create", (string[] dataCentres) =>
            Results.Ok());

        app.MapPut("/api/game-conductor/reserve", (GameConductorReserve reserve) => new
        {
            result = new
            {
            }
        });

        app.MapPost("/api/game-conductor/retrieve", () => new
        {
            result = new
            {
                retryInMillis = 5000,
                world = Guid.NewGuid(),
                ip = PublicIp,
                port = 42069,
                server = Guid.NewGuid()
            }
        });
    }
}