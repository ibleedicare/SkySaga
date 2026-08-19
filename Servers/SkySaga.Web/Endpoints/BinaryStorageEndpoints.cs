using System;
using System.IO;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SkySaga.Web.Endpoints;

public static class BinaryStorageEndpoints
{
    public static void MapBinaryStorageEndpoint(this WebApplication app)
    {
        // The "which is cooler?" photo voter. The client POSTs {"size": N} (how many photos
        // it wants to show side by side) and votes with the rating/up|down endpoints. An empty
        // 200 made it log error 11001, so return `size` stored photos to compare, in the same
        // per-photo schema the album (_search) uses. Wrapper (result.results) is a best guess;
        // if the voter UI stays empty, capture the real fields with SKYSAGA_HOOK_JSON.
        app.MapPost("/api/binary-storage/photos/_whichIsCooler", async (HttpContext context) =>
        {
            var size = 2;

            try
            {
                using var reader = new StreamReader(context.Request.Body);

                var body = await reader.ReadToEndAsync();

                using var document = System.Text.Json.JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("size", out var requested) &&
                    requested.TryGetInt32(out var n) && n > 0)
                    size = n;
            }
            catch { }

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

            var photos = PhotoStore.All()
                .Take(size)
                .Select(p => PhotoJson(p.Id, baseUrl, string.Empty, Session.AccountName))
                .ToArray();

            Console.WriteLine($"[photos/_whichIsCooler] size={size} -> {photos.Length} photo(s)");

            return Results.Ok(new { result = new { results = photos } });
        });

        // The album lists a character's photos here on open. Returns every stored photo
        // (single-player; see PhotoStore). The per-photo record schema is a best guess and
        // may need fields added once we see how the client renders the list.
        app.MapPost("/api/binary-storage/photos/_search", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);

            var body = await reader.ReadToEndAsync();

            // Echo the character the client searched for as each photo's owner.
            var characterUuid = string.Empty;

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("search", out var search) &&
                    search.TryGetProperty("characterUUID", out var uuid))
                    characterUuid = uuid.GetString() ?? string.Empty;
            }
            catch { }

            // The client parses each photo "url" as absolute and fetches from its authority,
            // so the URLs must be fully qualified at whatever host the client reached us on.
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

            var photos = PhotoStore.All()
                .Select(p => PhotoJson(p.Id, baseUrl, characterUuid, Session.AccountName))
                .ToArray();

            Console.WriteLine($"[photos/_search] query: {body} -> {photos.Length} photo(s)");

            // Envelope schema (hooked FUN_007354a0): the album reads "results" (PLURAL) from
            // the extracted "result" node -> result.results. See memory: photo-feature.
            return Results.Ok(new { result = new { results = photos } });
        });

        // The client uploads the JPEG here after PhotoValidated hands it this id. The body
        // is multipart/form-data with the image; store the raw bytes.
        app.MapPost("/api/binary-storage/photos/{id}/_upload", async (string id, HttpContext context) =>
        {
            byte[] bytes;

            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();

                using var memory = new MemoryStream();

                if (file is not null)
                    await file.CopyToAsync(memory);
                else
                    await context.Request.Body.CopyToAsync(memory);

                bytes = memory.ToArray();
            }
            else
            {
                using var memory = new MemoryStream();
                await context.Request.Body.CopyToAsync(memory);
                bytes = memory.ToArray();
            }

            PhotoStore.Save(id, bytes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Console.WriteLine($"[photos] stored {id} ({bytes.Length} bytes)");

            return Results.Ok(new { result = new { officialUUID = id } });
        });

        // Serve a stored image. The client builds this URL from the photo id to display it.
        app.MapGet("/api/binary-storage/photos/{id}", (string id) =>
        {
            return PhotoStore.TryGet(id, out var photo)
                ? Results.Bytes(photo.Bytes, "image/jpeg")
                : Results.NotFound();
        });

        // The album's image fetch parses the "url" authority and requests just /photos/<uuid>
        // (it drops the /api/binary-storage prefix), so serve the same bytes here too.
        app.MapGet("/photos/{id}", (string id) =>
        {
            return PhotoStore.TryGet(id, out var photo)
                ? Results.Bytes(photo.Bytes, "image/jpeg")
                : Results.NotFound();
        });

        // Acknowledge the ancillary photo actions so they do not 404. Access returns the
        // fetch URL; ratings/report just succeed.
        app.MapPost("/api/binary-storage/photos/{id}/access", (string id) =>
            Results.Ok(new { result = new { url = $"/api/binary-storage/photos/{id}" } }));

        app.MapPost("/api/binary-storage/photos/{id}/rating/up", (string id) => Results.Ok());
        app.MapPost("/api/binary-storage/photos/{id}/rating/down", (string id) => Results.Ok());
        app.MapPost("/api/binary-storage/photos/_report", () => Results.Ok());
    }

    /// <summary>
    /// One photo in the schema the client's deserializer (FUN_0077e5a0) expects — shared by
    /// _search (the album) and _whichIsCooler (the voter). "thumbnail", "scaled", "file",
    /// "gallery", "game" and "privateData" are TOP-LEVEL siblings (not nested). The client
    /// treats each "url" as an absolute URL and fetches from its authority, so they must be
    /// fully qualified at our host; it then GETs &lt;base&gt;/photos/&lt;uuid&gt;.
    /// </summary>
    private static object PhotoJson(string id, string baseUrl, string ownerUuid, string ownerName) => new
    {
        uuid = id,
        thumbnail = new { url = $"{baseUrl}/api/binary-storage/photos/{id}" },
        scaled = new { url = $"{baseUrl}/api/binary-storage/photos/{id}" },
        file = new { url = $"{baseUrl}/api/binary-storage/photos/{id}" },
        gallery = $"{baseUrl}/api/binary-storage/photos/{id}",
        // Caption under each photo is "<ownerName> - <biome>" (per an old video, e.g.
        // "boubechcraft - Forêt"). biome must be a REAL biome name (GeoData > Biomes) so the
        // client can resolve+localise it; an unknown biome collapses the whole caption.
        game = new { biome = "Desert", thumbsUp = 0, thumbsDown = 0, thumbsTotal = 0 },
        privateData = new { owner = "9f3f28ed-90f4-43e1-a120-4be07c3f2b27", ownerName = "EDITz" }
    };
}
