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

    private static readonly Dictionary<string, LootTableData> _lootTablesByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LootListData> _lootListsByName = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<byte, VoxelData> _voxelsByIndex = [];
    private static readonly Dictionary<string, VoxelData> _voxelsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every block in the client's table, in file order.</summary>
    public static IReadOnlyList<VoxelData> Voxels { get; private set; } = [];

    static GeoDataManager()
    {
        LoadResources();

        LoadAdventures();

        LoadVoxels();

        LoadLootTables();
    }

    /// <summary>
    /// Parse <c>GeoData.json &gt; Voxels</c> — the block table. Each entry carries the
    /// <c>VoxelIndex</c> used on the wire and the <c>Resource</c> it drops, which is where the
    /// server's block ids and loot come from.
    /// </summary>
    private static void LoadVoxels()
    {
        var path = Path.Combine("Data", "GeoData.json");

        if (!File.Exists(path))
            return;

        using var fileStream = File.OpenRead(path);

        using var jsonDocument = JsonDocument.Parse(fileStream);

        if (!jsonDocument.RootElement.TryGetPropertyIgnoreCase("Voxels", out var voxelsElement) ||
            voxelsElement.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("[geodata] no Voxels array; block ids unavailable");
            return;
        }

        var voxels = new List<VoxelData>();

        foreach (var element in voxelsElement.EnumerateArray())
        {
            if (!element.TryGetPropertyIgnoreCase("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                continue;

            var name = nameElement.GetString();

            if (string.IsNullOrWhiteSpace(name) ||
                !element.TryGetPropertyIgnoreCase("Voxel", out var inner) ||
                inner.ValueKind != JsonValueKind.Object)
                continue;

            var index = Int(inner, "VoxelIndex");

            if (index is < 0 or > byte.MaxValue)
                continue;

            var voxel = new VoxelData
            {
                Name = name,
                VoxelIndex = (byte)index,
                Resource = String(inner, "Resource"),
                IsPlaceable = Bool(inner, "IsPlaceable"),
                IsRendered = Bool(inner, "IsRendered"),
                IsDiggable = Bool(inner, "IsDiggable"),
                MiningToughness = Int(inner, "MiningToughness"),
                IsTerrain = Bool(inner, "IsTerrain")
            };

            voxels.Add(voxel);

            _voxelsByIndex.TryAdd(voxel.VoxelIndex, voxel);
            _voxelsByName.TryAdd(voxel.Name, voxel);
        }

        Voxels = voxels;

        Console.WriteLine($"[geodata] {voxels.Count} voxels "
            + $"({voxels.Count(x => x.IsPlaceable)} placeable, {voxels.Count(x => x.IsDiggable)} diggable, {voxels.Count(x => !x.IsRendered)} invisible)");
    }

    public static bool TryGetVoxel(byte index, [NotNullWhen(true)] out VoxelData? voxel)
        => _voxelsByIndex.TryGetValue(index, out voxel);

    public static bool TryGetVoxel(string name, [NotNullWhen(true)] out VoxelData? voxel)
        => _voxelsByName.TryGetValue(name, out voxel);

    /// <summary>
    /// Parse <c>GeoData.json &gt; LootTables</c> and <c>LootLists</c> — what harvesting yields.
    /// </summary>
    private static void LoadLootTables()
    {
        var path = Path.Combine("Data", "GeoData.json");

        if (!File.Exists(path))
            return;

        using var fileStream = File.OpenRead(path);

        using var jsonDocument = JsonDocument.Parse(fileStream);

        if (jsonDocument.RootElement.TryGetPropertyIgnoreCase("LootLists", out var listsElement) &&
            listsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var listElement in listsElement.EnumerateArray())
            {
                var name = String(listElement, "Name");

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var resources = new List<LootResource>();

                if (listElement.TryGetPropertyIgnoreCase("LootResources", out var resourcesElement) &&
                    resourcesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var resourceElement in resourcesElement.EnumerateArray())
                    {
                        var resourceName = String(resourceElement, "Name");

                        if (string.IsNullOrWhiteSpace(resourceName))
                            continue;

                        resources.Add(new LootResource
                        {
                            Name = resourceName,
                            Quantity = Math.Max(1, Int(resourceElement, "Quantity")),
                            Frequency = Math.Max(1, Int(resourceElement, "Frequency"))
                        });
                    }
                }

                _lootListsByName.TryAdd(name, new LootListData { Name = name, Resources = resources });
            }
        }

        if (jsonDocument.RootElement.TryGetPropertyIgnoreCase("LootTables", out var tablesElement) &&
            tablesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var tableElement in tablesElement.EnumerateArray())
            {
                var name = String(tableElement, "Name");

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var entries = new List<LootTableEntry>();

                if (tableElement.TryGetPropertyIgnoreCase("LootTableEntries", out var entriesElement) &&
                    entriesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entryElement in entriesElement.EnumerateArray())
                    {
                        var entryName = String(entryElement, "Name");

                        if (string.IsNullOrWhiteSpace(entryName))
                            continue;

                        entries.Add(new LootTableEntry
                        {
                            Name = entryName,
                            Quantity = Math.Max(1, Int(entryElement, "Quantity")),
                            SpawnPercentage = Int(entryElement, "SpawnPercentage")
                        });
                    }
                }

                _lootTablesByName.TryAdd(name, new LootTableData { Name = name, Entries = entries });
            }
        }

        Console.WriteLine($"[geodata] {_lootTablesByName.Count} loot tables, {_lootListsByName.Count} loot lists");
    }

    public static bool TryGetLootTable(string name, [NotNullWhen(true)] out LootTableData? table)
        => _lootTablesByName.TryGetValue(name, out table);

    public static bool TryGetLootList(string name, [NotNullWhen(true)] out LootListData? list)
        => _lootListsByName.TryGetValue(name, out list);

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
