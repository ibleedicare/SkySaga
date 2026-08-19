using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.GeoData;

/// <summary>
/// The client's item table, loaded from <c>Data/GeoData.json</c>.
/// </summary>
/// <remarks>
/// <c>GeoData.json</c> is one of four JSON blobs bundled inside <c>SkySaga.exe</c>
/// (extract them with <c>tools/entity-extract/extract_bundled.py</c>). The copy used
/// here is client build 10414 and is linked straight out of <c>Bundled/10414/</c>, so
/// it cannot drift from the client it targets.
///
/// Only the <c>Resources</c> section is parsed. The file also holds Recipes,
/// LootTables, Materials, StatTemplates, InventoryLoadOutLists, Biomes, Jobs and
/// ~60 other sections that nothing consumes yet.
/// </remarks>
public static class GeoDataManager
{
    private static readonly Dictionary<string, ResourceData> _resourcesByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, ResourceData> _resourcesByHash = [];

    private static readonly Dictionary<string, AdventureData> _adventuresByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every item in the client's table, in file order.</summary>
    public static IReadOnlyList<ResourceData> Resources { get; private set; } = [];

    /// <summary>Every world/adventure definition, in file order.</summary>
    public static IReadOnlyList<AdventureData> Adventures { get; private set; } = [];

    static GeoDataManager()
    {
        LoadResources();

        LoadAdventures();
    }

    /// <summary>
    /// Parse <c>GeoData.json &gt; Adventures</c>. Each entry names a world and carries a nested
    /// <c>Adventure</c> object whose <c>WorldType</c> decides the mode (home/quest/pvp/sandbox).
    /// </summary>
    private static void LoadAdventures()
    {
        var path = Path.Combine("Data", "GeoData.json");

        if (!File.Exists(path))
            return;

        using var fileStream = File.OpenRead(path);

        using var jsonDocument = JsonDocument.Parse(fileStream);

        if (!jsonDocument.RootElement.TryGetPropertyIgnoreCase("Adventures", out var adventuresElement) ||
            adventuresElement.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("[geodata] no Adventures array; world-type switching disabled");
            return;
        }

        var adventures = new List<AdventureData>();

        foreach (var adventureElement in adventuresElement.EnumerateArray())
        {
            if (!adventureElement.TryGetPropertyIgnoreCase("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                continue;

            var name = nameElement.GetString();

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var worldType = 0;

            // WorldType lives in the nested "Adventure" object alongside PVPType, MaxPlayers, etc.
            if (adventureElement.TryGetPropertyIgnoreCase("Adventure", out var innerElement) &&
                innerElement.ValueKind == JsonValueKind.Object)
                worldType = Int(innerElement, "WorldType");

            var adventure = new AdventureData
            {
                Name = name,
                NameHash = Util.ComputeCrc32(name),
                WorldType = worldType,
                MapSize = Int(adventureElement, "MapSize"),
                RootFeatureName = String(adventureElement, "RootFeatureName")
            };

            adventures.Add(adventure);

            if (!_adventuresByName.TryAdd(adventure.Name, adventure))
                Console.WriteLine($"[geodata] duplicate adventure name {adventure.Name}");
        }

        Adventures = adventures;

        Console.WriteLine($"[geodata] {adventures.Count} adventures ("
            + string.Join(", ", adventures.GroupBy(a => a.WorldType)
                .OrderBy(g => g.Key)
                .Select(g => $"type{g.Key}:{g.Count()}")) + ")");
    }

    public static bool TryGetAdventure(string name, [NotNullWhen(true)] out AdventureData? adventure)
        => _adventuresByName.TryGetValue(name, out adventure);

    private static void LoadResources()
    {
        var path = Path.Combine("Data", "GeoData.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"[geodata] {path} is missing; item names cannot be validated");
            return;
        }

        using var fileStream = File.OpenRead(path);

        using var jsonDocument = JsonDocument.Parse(fileStream);

        if (!jsonDocument.RootElement.TryGetPropertyIgnoreCase("Resources", out var resourcesElement) ||
            resourcesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GeoData.json has no Resources array");

        // Behaviour blocks are the object-valued fields; every scalar field is present on
        // all 365 entries, so anything that is an object is a behaviour (Attack, Clothing,
        // Dig, Eat, CreateEntity, ...).
        var resources = new List<ResourceData>();

        foreach (var resourceElement in resourcesElement.EnumerateArray())
        {
            if (!resourceElement.TryGetPropertyIgnoreCase("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Resource entry has no Name");

            var name = nameElement.GetString();

            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            var behaviours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in resourceElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    behaviours.Add(property.Name);
            }

            int? clothingProtection = null;

            if (resourceElement.TryGetPropertyIgnoreCase("Clothing", out var clothingElement) &&
                clothingElement.ValueKind == JsonValueKind.Object &&
                clothingElement.TryGetPropertyIgnoreCase("Protection", out var protectionElement) &&
                protectionElement.TryGetInt32(out var protection))
                clothingProtection = protection;

            var resource = new ResourceData
            {
                Name = name,
                NameHash = Util.ComputeCrc32(name),

                IsInventoryItem = Bool(resourceElement, "IsInventoryItem"),
                IsEquippableItem = Bool(resourceElement, "IsEquippableItem"),
                IsStorable = Bool(resourceElement, "IsStorable"),
                IsDiscardable = Bool(resourceElement, "IsDiscardable"),
                IsDestroyable = Bool(resourceElement, "IsDestroyable"),
                IsTwoHanded = Bool(resourceElement, "IsTwoHanded"),
                IsUsableByPlayer = Bool(resourceElement, "IsUsableByPlayer"),

                ActionVoxel = String(resourceElement, "ActionVoxel"),

                SubCategory = String(resourceElement, "SubCategory"),
                RarityLevel = String(resourceElement, "RarityLevel"),
                StatTemplateName = String(resourceElement, "StatTemplateName"),
                RequiredJob = String(resourceElement, "RequiredJob"),

                RequiredJobRank = Int(resourceElement, "RequiredJobRank"),
                ObjectType = Int(resourceElement, "ObjectType"),
                InGameCurrency = Int(resourceElement, "InGameCurrency"),
                StackLimitOverride = Int(resourceElement, "StackLimitOverride"),
                IsOverridingStackLimit = Bool(resourceElement, "IsOverridingStackLimit"),

                ClothingProtection = clothingProtection,
                Behaviours = behaviours
            };

            resources.Add(resource);

            // Both indexes are expected to be collision free for 10414; a duplicate would
            // mean the client cannot address the item unambiguously either, so say so
            // rather than silently keeping one of them.
            if (!_resourcesByName.TryAdd(resource.Name, resource))
                Console.WriteLine($"[geodata] duplicate resource name {resource.Name}");

            if (!_resourcesByHash.TryAdd(resource.NameHash, resource))
                Console.WriteLine($"[geodata] crc32 collision on {resource.Name} ({resource.NameHash:x8})");
        }

        Resources = resources;

        Console.WriteLine($"[geodata] {resources.Count} resources "
            + $"({resources.Count(x => x.IsInventoryItem)} inventory, "
            + $"{resources.Count(x => x.IsArmour)} armour)");
    }

    /// <summary>Force the static constructor to run, so load failures surface at startup.</summary>
    public static void Touch()
    {
    }

    private static bool Bool(JsonElement element, string name)
        => element.TryGetPropertyIgnoreCase(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement element, string name)
        => element.TryGetPropertyIgnoreCase(name, out var value)
            && value.TryGetInt32(out var result) ? result : 0;

    private static string String(JsonElement element, string name)
        => element.TryGetPropertyIgnoreCase(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    public static bool TryGetResource(string name, [NotNullWhen(true)] out ResourceData? resource)
        => _resourcesByName.TryGetValue(name, out resource);

    public static bool TryGetResource(uint nameHash, [NotNullWhen(true)] out ResourceData? resource)
        => _resourcesByHash.TryGetValue(nameHash, out resource);

    /// <summary>
    /// Resolve an item name to the hash the client expects.
    /// </summary>
    /// <remarks>
    /// Returns false for a name the client's table does not contain. That check matters:
    /// on an unknown hash the client dereferences a null item definition and dies with
    /// <c>Unhandled page fault on read access to 00000008 at 0071A461</c>, so a typo in a
    /// loadout used to be a client crash with no server-side symptom.
    /// </remarks>
    public static bool TryGetItemHash(string name, out uint nameHash)
    {
        if (_resourcesByName.TryGetValue(name, out var resource) && resource.IsInventoryItem)
        {
            nameHash = resource.NameHash;

            return true;
        }

        nameHash = 0;

        return false;
    }

    /// <summary>Items whose <c>SubCategory</c> matches, e.g. <c>Head</c> or <c>Edged Weapons</c>.</summary>
    public static IEnumerable<ResourceData> BySubCategory(string subCategory)
        => Resources.Where(x => string.Equals(x.SubCategory, subCategory, StringComparison.OrdinalIgnoreCase));
}
