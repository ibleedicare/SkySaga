using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkySaga.Web;

/// <summary>
/// Where uploaded avatar photos live. The game server hands the client a photo id in
/// <c>PhotoValidated</c>; the client then uploads the JPEG to
/// <c>/api/binary-storage/photos/&lt;id&gt;/_upload</c>, and the album lists them via
/// <c>_search</c> and fetches each with <c>GET /api/binary-storage/photos/&lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// In-memory index plus on-disk bytes (SKYSAGA_PHOTO_DIR, default ./photos), so photos
/// survive a web-server restart and can be inspected. Single-process, single-player: no
/// per-character partitioning yet — the game and web servers are separate processes and
/// don't share the id->character mapping, so <see cref="All"/> returns everything.
/// </remarks>
public static class PhotoStore
{
    public sealed record Photo(string Id, byte[] Bytes, long TakenAtUnixMs);

    private static readonly ConcurrentDictionary<string, Photo> _photos = new();

    private static readonly string _dir =
        Environment.GetEnvironmentVariable("SKYSAGA_PHOTO_DIR") ?? "photos";

    static PhotoStore()
    {
        try
        {
            Directory.CreateDirectory(_dir);

            foreach (var path in Directory.EnumerateFiles(_dir, "*.jpg"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var info = new FileInfo(path);

                _photos[id] = new Photo(id, File.ReadAllBytes(path),
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[photos] could not load existing photos: {exception.Message}");
        }
    }

    public static void Save(string id, byte[] bytes, long takenAtUnixMs)
    {
        _photos[id] = new Photo(id, bytes, takenAtUnixMs);

        try
        {
            File.WriteAllBytes(Path.Combine(_dir, $"{id}.jpg"), bytes);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[photos] could not write {id}: {exception.Message}");
        }
    }

    public static bool TryGet(string id, out Photo photo) => _photos.TryGetValue(id, out photo!);

    /// <summary>Newest first — how an album normally lists shots.</summary>
    public static IReadOnlyList<Photo> All()
        => _photos.Values.OrderByDescending(p => p.TakenAtUnixMs).ToList();
}
