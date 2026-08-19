using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace SkySaga.Web.Endpoints;

/// <summary>
/// Friends list, friend requests, blocked players and character search.
/// </summary>
/// <remarks>
/// Endpoints (from the client's URL templates):
/// <code>
/// GET /api/social-graph/character/{uuid}/friends/info
/// GET /api/social-graph/character/{uuid}/blocked
/// GET /api/social-graph/friendrequest/{uuid}
/// GET /api/social-graph/friendrequest/{uuid}/outgoing
/// GET /api/social-graph/character/find/name/{name}
///     /api/social-graph/friendrequest/create|accept|reject
///     /api/social-graph/character/friend/remove, /character/block, /character/block/remove
/// </code>
/// The <c>{uuid}</c> is the character's own id, which the client takes from its player
/// entity's OwnerComponent — see SkySaga.Game's ClientOwnerComponent.
///
/// Schemas reversed from the client:
/// - friends/info: <c>result.onlinePlayers</c> / <c>result.offlinePlayers</c> (FUN_00751890);
///   each entry is searched for <c>uuid</c> first, falling back to <c>characterUuid</c>
///   (FUN_00751600).
/// - blocked / find-by-name share a parser (FUN_0083b660) reading <c>uuid</c>|<c>characterUuid</c>,
///   <c>name</c>|<c>characterName</c>, <c>currentWorld</c>, <c>homeworld</c>,
///   <c>currentWorldAdventure</c>, <c>currentWorldBiome</c>, <c>cost</c> and
///   <c>details.blocked</c>. It skips entries without a uuid or name, and skips blocked ones.
/// - friend request lists (FUN_007a6fd0) only count the entries returned.
///
/// Test data: SKYSAGA_FAKE_FRIENDS, SKYSAGA_FAKE_BLOCKED, SKYSAGA_FAKE_REQUESTS take
/// comma-separated uuids; character search echoes the searched name back as one result.
/// </remarks>
public static class SocialGraphEndpoints
{
    private static string[] Configured(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>An entry in the shape the client's player-list parser expects.</summary>
    private static object Player(string uuid, string name, bool blocked = false) => new
    {
        uuid,
        characterUuid = uuid,
        name,
        characterName = name,
        currentWorld = "Home Island",
        homeworld = "Home Island",
        currentWorldBiome = "Desert",
        currentWorldAdventure = "",
        cost = 0,
        details = new { blocked }
    };

    // In-memory social graph so the panel is interactive: accepting a request moves that
    // character into the friends list, rejecting drops it, blocking moves it to the blocked
    // list. Seeded from the SKYSAGA_FAKE_* variables on first use.
    private static readonly Dictionary<string, string> _friends = [];
    private static readonly Dictionary<string, string> _requests = [];
    private static readonly Dictionary<string, string> _blocked = [];

    private static bool _seeded;

    private static void Seed()
    {
        if (_seeded)
            return;

        _seeded = true;

        foreach (var (uuid, index) in Configured("SKYSAGA_FAKE_FRIENDS").Select((value, index) => (value, index)))
            _friends[uuid] = $"TestFriend{index + 1}";

        foreach (var (uuid, index) in Configured("SKYSAGA_FAKE_REQUESTS").Select((value, index) => (value, index)))
            _requests[uuid] = $"RequestFrom{index + 1}";

        foreach (var (uuid, index) in Configured("SKYSAGA_FAKE_BLOCKED").Select((value, index) => (value, index)))
            _blocked[uuid] = $"BlockedPlayer{index + 1}";
    }

    /// <summary>Pulls the first uuid out of a request body, whatever the field is called.</summary>
    private static string? UuidIn(string body)
    {
        var match = Regex.Match(body, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        return match.Success ? match.Value : null;
    }

    public static void MapSocialGraphEndpoints(this WebApplication app)
    {
        app.Map("/api/social-graph/{**rest}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;

            Seed();

            // Actions first: they live under /friendrequest and /character too, so they have
            // to be matched before the list endpoints below.
            if (path.EndsWith("/create", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/remove", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/block", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(context.Request.Body);

                var body = await reader.ReadToEndAsync();

                Console.WriteLine($"[social-graph] {context.Request.Method} {path} body: {body}");

                var uuid = UuidIn(body);

                if (uuid is not null)
                {
                    var name = _requests.GetValueOrDefault(uuid)
                        ?? _friends.GetValueOrDefault(uuid)
                        ?? _blocked.GetValueOrDefault(uuid)
                        ?? "Player";

                    if (path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase))
                    {
                        _requests.Remove(uuid);
                        _friends[uuid] = name;
                    }
                    else if (path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase))
                    {
                        _requests.Remove(uuid);
                    }
                    else if (path.EndsWith("/block", StringComparison.OrdinalIgnoreCase))
                    {
                        _requests.Remove(uuid);
                        _friends.Remove(uuid);
                        _blocked[uuid] = name;
                    }
                    else if (path.Contains("/block/remove", StringComparison.OrdinalIgnoreCase))
                    {
                        _blocked.Remove(uuid);
                    }
                    else if (path.Contains("/friend/remove", StringComparison.OrdinalIgnoreCase))
                    {
                        _friends.Remove(uuid);
                    }
                    else if (path.EndsWith("/create", StringComparison.OrdinalIgnoreCase))
                    {
                        _requests[uuid] = name;
                    }

                    Console.WriteLine($"[social-graph] friends={_friends.Count} requests={_requests.Count} blocked={_blocked.Count}");
                }

                return Results.Ok(new { result = new { } });
            }

            if (path.EndsWith("/friends/info", StringComparison.OrdinalIgnoreCase))
            {
                var online = _friends.Select(entry => Player(entry.Key, entry.Value)).ToArray();

                return Results.Ok(new
                {
                    result = new
                    {
                        onlinePlayers = online,
                        offlinePlayers = Array.Empty<object>()
                    }
                });
            }

            if (path.EndsWith("/blocked", StringComparison.OrdinalIgnoreCase))
            {
                var blocked = _blocked.Select(entry => Player(entry.Key, entry.Value, blocked: true)).ToArray();

                // Top level array: the parser iterates the document's children as
                // entries, so a { "result": [...] } wrapper hides them one level down.
                return Results.Ok(blocked);
            }

            if (path.Contains("/character/find/name/", StringComparison.OrdinalIgnoreCase))
            {
                // Echo the searched name back as a single match so the tab has something
                // to show; the uuid is derived from the name so it stays stable.
                var name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);

                var matches = string.IsNullOrWhiteSpace(name)
                    ? []
                    : new[] { Player(DeterministicUuid(name), name) };

                return Results.Ok(matches);
            }

            if (path.Contains("/friendrequest", StringComparison.OrdinalIgnoreCase))
            {
                // Covers both the incoming list and the /outgoing variant. The panel
                // (FUN_0083a4c0) wants a "character" and a "friendRequest" object per entry
                // and drops the entry unless uuid, name, made and message are all present;
                // "made" is a millisecond epoch which it renders as a date.
                var made = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var requests = _requests
                    .Select(entry => (object)new
                    {
                        character = new
                        {
                            uuid = entry.Key,
                            name = entry.Value
                        },
                        friendRequest = new
                        {
                            made,
                            message = "Let's be friends"
                        }
                    })
                    .ToArray();

                return Results.Ok(requests);
            }

            Console.WriteLine($"[social-graph] unimplemented: {context.Request.Method} {path}");

            return Results.Ok(new { result = new { } });
        });
    }

    private static string DeterministicUuid(string value)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));

        return new Guid(bytes).ToString();
    }
}
