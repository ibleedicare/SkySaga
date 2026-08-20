using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

using SkySaga.Game.GeoData;
using SkySaga.Game.World;

namespace SkySaga.Game.Admin;

/// <summary>
/// A small admin panel served straight out of the game server: a creative-mode item
/// catalogue you drag items from onto a replica of the player's rucksack.
/// </summary>
/// <remarks>
/// It lives here rather than in SkySaga.Web because everything it needs is here — the item
/// table (<see cref="GeoDataManager"/>), the connected player, and the command queue that
/// lets an HTTP thread mutate game state safely. A bare <see cref="HttpListener"/> keeps the
/// game server free of an ASP.NET dependency.
///
/// Bind address from SKYSAGA_ADMIN_BIND (default <c>http://*:5175/</c>); set
/// SKYSAGA_ADMIN=0 to disable.
/// </remarks>
public sealed class AdminServer
{
    private readonly Server _game;
    private readonly HttpListener _listener = new();

    public AdminServer(Server game, string prefix)
    {
        _game = game;

        _listener.Prefixes.Add(prefix);
    }

    public static AdminServer? TryStart(Server game)
    {
        if (Environment.GetEnvironmentVariable("SKYSAGA_ADMIN") == "0")
            return null;

        var prefix = Environment.GetEnvironmentVariable("SKYSAGA_ADMIN_BIND") is { Length: > 0 } configured
            ? configured
            : "http://*:5175/";

        try
        {
            var server = new AdminServer(game, prefix);

            server.Start();

            return server;
        }
        catch (Exception exception)
        {
            // A bad prefix or a taken port must not stop the game server from running.
            Console.WriteLine($"[admin] not started ({exception.Message})");

            return null;
        }
    }

    private void Start()
    {
        _listener.Start();

        Console.WriteLine($"[admin] panel on {string.Join(", ", _listener.Prefixes)}");

        var thread = new Thread(Listen) { IsBackground = true, Name = "admin-http" };

        thread.Start();
    }

    private void Listen()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = _listener.GetContext();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[admin] {context.Request.Url?.AbsolutePath} threw: {exception.Message}");

                TryRespond(context, 500, "text/plain", "error");
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        switch (path)
        {
            case "/":
            case "/index.html":
                TryRespond(context, 200, "text/html; charset=utf-8", AdminPage.Html);
                break;

            case "/editor":
            case "/editor.html":
                TryRespond(context, 200, "text/html; charset=utf-8", EditorPage.Html);
                break;

            case "/api/items":
                TryRespond(context, 200, "application/json", ItemsJson());
                break;

            case "/api/inventory":
                TryRespond(context, 200, "application/json", InventoryJson());
                break;

            case "/api/give":
                TryRespond(context, 200, "application/json", Give(context));
                break;

            case "/api/icons":
                TryRespond(context, 200, "application/json", IconsJson());
                break;

            case "/api/world/blocks":
                TryRespond(context, 200, "application/json", BlocksJson());
                break;

            case "/api/world/info":
                TryRespond(context, 200, "application/json", WorldInfoJson());
                break;

            case "/api/world/chunk":
                TryRespond(context, 200, "application/json", ChunkJson(context));
                break;

            case "/api/world/voxel":
                TryRespond(context, 200, "application/json", SetVoxel(context));
                break;

            default:
                if (path.StartsWith("/icons/", StringComparison.Ordinal))
                    SendIcon(context, path["/icons/".Length..]);
                else
                    TryRespond(context, 404, "text/plain", "not found");
                break;
        }
    }

    /// <summary>
    /// Item icons extracted from the client's .pc archives. Only ~37 of the 314 items have a
    /// 2D sprite (the game renders the rest from their 3D model), so the panel asks for this
    /// list and falls back to a lettered tile for the others.
    /// </summary>
    /// <remarks>
    /// Not in source control — they are extracted from the client you own:
    /// <code>uv run --with pillow tools/icon-extract/extract_icons.py --size 64</code>
    /// The panel works without them; every item just shows its lettered tile.
    /// </remarks>
    private static readonly string IconDirectory =
        Path.Combine(AppContext.BaseDirectory, "Admin", "icons");

    private static string IconsJson()
    {
        if (!Directory.Exists(IconDirectory))
            return "[]";

        var names = Directory.EnumerateFiles(IconDirectory, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(names);
    }

    private static void SendIcon(HttpListenerContext context, string fileName)
    {
        // Serve only plain names out of the icon directory — never a caller-supplied path.
        if (fileName.Length == 0 || fileName.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.')))
        {
            TryRespond(context, 400, "text/plain", "bad name");
            return;
        }

        var path = Path.Combine(IconDirectory, fileName);

        if (!File.Exists(path))
        {
            TryRespond(context, 404, "text/plain", "no icon");
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "image/png";
            context.Response.Headers["Cache-Control"] = "max-age=86400";
            context.Response.ContentLength64 = bytes.Length;

            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            // The browser hung up.
        }
    }

    /// <summary>
    /// The client's block table, for the world editor's palette: id, name, the item it drops,
    /// and whether a player could place it (ore deposits cannot, but an admin still can).
    /// </summary>
    private static string BlocksJson()
    {
        var blocks = GeoDataManager.Voxels
            .OrderBy(voxel => voxel.VoxelIndex)
            .Select(voxel => new
            {
                id = voxel.VoxelIndex,
                name = voxel.Name,
                resource = voxel.Resource,
                placeable = voxel.IsPlaceable,
                rendered = voxel.IsRendered,
                diggable = voxel.IsDiggable,
                toughness = voxel.MiningToughness
            });

        return JsonSerializer.Serialize(blocks);
    }

    private static string WorldInfoJson()
    {
        var (spawnX, spawnY, spawnZ) = TerrainGenerator.Spawn();

        return JsonSerializer.Serialize(new
        {
            chunkSize = TerrainGenerator.ChunkSize,
            sizeChunks = TerrainGenerator.SizeChunks,
            seed = TerrainGenerator.Seed,
            spawn = new { x = spawnX, y = spawnY, z = spawnZ }
        });
    }

    /// <summary>
    /// One chunk as the server currently sees it — generated terrain with the players' edits
    /// applied — so the editor shows the live world rather than a saved file.
    /// </summary>
    private string ChunkJson(HttpListenerContext context)
    {
        var query = context.Request.QueryString;

        _ = int.TryParse(query["x"], out var chunkX);
        _ = int.TryParse(query["y"], out var chunkY);
        _ = int.TryParse(query["z"], out var chunkZ);

        const int size = TerrainGenerator.ChunkSize;

        var voxels = new byte[size * size * size];

        var counts = new Dictionary<byte, int>();

        var edits = _game.World?.VoxelEdits;

        for (var y = 0; y < size; y++)
        {
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    var world = (X: chunkX * size + x, Y: chunkY * size + y, Z: chunkZ * size + z);

                    var material = edits is not null && edits.TryGetValue(world, out var edited)
                        ? edited
                        : TerrainGenerator.MaterialAt(world.X, world.Y, world.Z);

                    voxels[y * size * size + z * size + x] = material;

                    counts[material] = counts.GetValueOrDefault(material) + 1;
                }
            }
        }

        // Histogram by name as well, which makes "is there any ore down here?" answerable
        // without rendering anything.
        var summary = counts
            .OrderByDescending(pair => pair.Value)
            .ToDictionary(
                pair => GeoDataManager.TryGetVoxel(pair.Key, out var voxel) ? voxel.Name : pair.Key.ToString(),
                pair => pair.Value);

        return JsonSerializer.Serialize(new
        {
            chunk = new { x = chunkX, y = chunkY, z = chunkZ },
            size,
            summary,
            voxels = Convert.ToBase64String(voxels)
        });
    }

    /// <summary>
    /// Set one voxel from the editor. Applies it to the world overlay and pushes it to the
    /// connected client, so an edit made in the browser shows up in the running game.
    /// </summary>
    private string SetVoxel(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);

        int x = 0, y = 0, z = 0, material = 0;

        try
        {
            using var document = JsonDocument.Parse(reader.ReadToEnd());

            x = document.RootElement.GetProperty("x").GetInt32();
            y = document.RootElement.GetProperty("y").GetInt32();
            z = document.RootElement.GetProperty("z").GetInt32();
            material = document.RootElement.GetProperty("material").GetInt32();
        }
        catch (Exception)
        {
            return JsonSerializer.Serialize(new { ok = false, message = "bad request body" });
        }

        if (material is < 0 or > byte.MaxValue)
            return JsonSerializer.Serialize(new { ok = false, message = "material out of range" });

        var world = _game.World;

        if (world is null)
            return JsonSerializer.Serialize(new { ok = false, message = "world not ready" });

        var done = new ManualResetEventSlim(false);
        var message = "timed out";

        // Runs on the game thread so the edit and the packet cannot race a tick. Editing works
        // with nobody connected; the push is simply skipped until a player joins, and the
        // change is in the world either way.
        _game.Enqueue(() =>
        {
            try
            {
                world.VoxelEdits[(x, y, z)] = (byte)material;

                _game.FirstConnection?.SendWorldVoxel(x, y, z, (byte)material);

                message = $"({x},{y},{z}) = {material}";
            }
            catch (Exception exception)
            {
                message = exception.Message;
            }
            finally
            {
                done.Set();
            }
        });

        var completed = done.Wait(TimeSpan.FromSeconds(5));

        return JsonSerializer.Serialize(new { ok = completed, message });
    }

    /// <summary>The whole item catalogue, so the panel can render and filter it client-side.</summary>
    private static string ItemsJson()
    {
        var items = GeoDataManager.Resources
            .Where(resource => resource.IsInventoryItem)
            .OrderBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .Select(resource => new
            {
                name = resource.Name,
                subCategory = resource.SubCategory,
                rarity = resource.RarityLevel,
                equippable = resource.IsEquippableItem,
                armour = resource.ClothingProtection is not null
            });

        return JsonSerializer.Serialize(items);
    }

    private string InventoryJson()
    {
        var connection = _game.FirstConnection;

        if (connection is null)
            return JsonSerializer.Serialize(new { online = false, slots = new Dictionary<int, string>() });

        // Read on the HTTP thread: SlotContents only reads, and a slightly stale view is fine
        // for a panel that polls.
        return JsonSerializer.Serialize(new { online = true, slots = connection.SlotContents() });
    }

    /// <summary>Place (or clear) an item in a slot. Body: <c>{"name":"Dirt","count":10,"slot":9}</c>.</summary>
    private string Give(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);

        var body = reader.ReadToEnd();

        string? name = null;
        var count = 1;
        var slot = -1;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("name", out var nameElement))
                name = nameElement.GetString();

            if (document.RootElement.TryGetProperty("count", out var countElement))
                count = countElement.GetInt32();

            if (document.RootElement.TryGetProperty("slot", out var slotElement))
                slot = slotElement.GetInt32();
        }
        catch (Exception)
        {
            return JsonSerializer.Serialize(new { ok = false, message = "bad request body" });
        }

        if (slot < 0)
            return JsonSerializer.Serialize(new { ok = false, message = "no slot" });

        var connection = _game.FirstConnection;

        if (connection is null)
            return JsonSerializer.Serialize(new { ok = false, message = "no player is online" });

        // Inventory mutation has to happen on the game thread; wait for it so the panel can
        // report the real result rather than an optimistic one.
        var done = new ManualResetEventSlim(false);
        var message = "timed out";

        _game.Enqueue(() =>
        {
            try
            {
                message = connection.SetSlot(name, count, slot);
            }
            catch (Exception exception)
            {
                message = exception.Message;
            }
            finally
            {
                done.Set();
            }
        });

        var completed = done.Wait(TimeSpan.FromSeconds(5));

        return JsonSerializer.Serialize(new { ok = completed, message });
    }

    private static void TryRespond(HttpListenerContext context, int status, string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);

            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;

            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            // The browser hung up; nothing useful to do.
        }
    }
}
