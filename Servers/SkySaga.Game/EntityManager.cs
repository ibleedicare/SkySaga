using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using SkySaga.Game.Entities;
using SkySaga.Game.Components;
using SkySaga.Game.Extensions;

namespace SkySaga.Game;

public static class EntityManager
{
    private static Dictionary<string, Type> _components = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, EntityData> _entities = new(StringComparer.OrdinalIgnoreCase);

    static EntityManager()
    {
        LoadEntityData();

        LoadAssemblyComponents();
    }

    private static void LoadAssemblyComponents()
    {
        var componentType = typeof(Component);

        foreach (var assemblyType in componentType.Assembly.GetTypes())
        {
            if (!assemblyType.IsClass
                || assemblyType.IsAbstract
                || !assemblyType.IsSubclassOf(componentType))
                continue;

            _components.Add(assemblyType.Name.ToLower(), assemblyType);
        }
    }

    private static void LoadEntityData()
    {
        using var fileStream = File.OpenRead(Path.Combine("Data", "Entities.json"));

        using var jsonDocument = JsonDocument.Parse(fileStream);

        if (!jsonDocument.RootElement.TryGetPropertyIgnoreCase("Entities", out var entitiesElement) &&
            entitiesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException();

        foreach (var entityElement in entitiesElement.EnumerateArray())
        {
            if (!entityElement.TryGetPropertyIgnoreCase("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException();

            var entityName = nameElement.GetString();

            ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

            if (!entityElement.TryGetPropertyIgnoreCase("Parameters", out var parametersElement) ||
                parametersElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();

            List<EntityData.ParameterData> parameters = [];

            foreach (var parameterProperty in parametersElement.EnumerateObject())
            {
                var parameterName = parameterProperty.Name;

                var parameterInfo = new EntityData.ParameterData
                {
                    Name = parameterName
                };

                if (parameterProperty.Value.TryGetPropertyIgnoreCase("SyncIndex", out var syncIndexElement))
                {
                    if (!syncIndexElement.TryGetInt32(out int syncIndex))
                        throw new InvalidOperationException();

                    parameterInfo.SyncIndex = syncIndex;
                }

                if (parameterProperty.Value.TryGetPropertyIgnoreCase("Value", out var valueElement))
                {
                    // Clone: a JsonElement is a view into the JsonDocument, which this method
                    // disposes on the way out. Nothing read Value until GetDefaultVoxelLinks,
                    // so the dangling reference had never been noticed.
                    parameterInfo.Value = valueElement.Clone();
                }

                parameters.Add(parameterInfo);
            }

            // Client/Server
            if (!entityElement.TryGetPropertyIgnoreCase("Client", out var clientElement) ||
                clientElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();

            if (!clientElement.TryGetPropertyIgnoreCase("Components", out var componentsElement) ||
                componentsElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();

            List<EntityData.ComponentData> components = [];

            foreach (var componentProperty in componentsElement.EnumerateObject())
            {
                var componentName = componentProperty.Name;

                var componentInfo = new EntityData.ComponentData
                {
                    Name = componentName
                };

                if (!componentProperty.Value.TryGetPropertyIgnoreCase("Bindings", out var bindingsElement) ||
                    bindingsElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var bindingProperty in bindingsElement.EnumerateObject())
                {
                    var bindingName = bindingProperty.Name;

                    if (!bindingProperty.Value.TryGetPropertyIgnoreCase("MapsTo", out var mapsToElement) ||
                        mapsToElement.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException();

                    var mapsTo = mapsToElement.GetString();

                    ArgumentException.ThrowIfNullOrWhiteSpace(mapsTo);

                    componentInfo.Bindings.TryAdd(bindingName, mapsTo);
                }

                components.Add(componentInfo);
            }

            _entities.Add(entityName, new EntityData(entityName, parameters, components));
        }
    }

    /// <summary>
    /// Every entity whose client side declares a component whose name contains
    /// <paramref name="componentSubstring"/>, in Entities.json order.
    /// </summary>
    /// <remarks>
    /// The use this exists for is "spawn one of every interactable and see which ones work"
    /// (<c>/spawnall</c>): pass <c>"interaction"</c> and you get the 50 entities that offer an
    /// E-press. Matching on a substring rather than the exact component name keeps it usable for
    /// the other sweeps too — <c>"crafting"</c>, <c>"inventory"</c>.
    /// </remarks>
    public static List<string> GetEntityNamesWithComponent(string componentSubstring)
    {
        List<string> names = [];

        foreach (var (name, entityData) in _entities)
        {
            foreach (var component in entityData.Components)
            {
                if (component.Name.Contains(componentSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);

                    break;
                }
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);

        return names;
    }

    /// <summary>
    /// True when the entity declares a voxel-link component, i.e. it is a voxel BLOCK
    /// (Chest, Barrel, Crate, Rock) rather than a free entity (Chicken, Tree, items).
    /// </summary>
    /// <remarks>
    /// Blocks carry a <c>voxels</c> grid in <c>clientvoxellinkcomponent</c>, which is not
    /// implemented yet. Spawning one without it hangs the client while it tries to build the
    /// voxel structure — and because the map outlives the connection, the entity then re-hangs
    /// the client on every reconnect until the server restarts. So spawning is refused until
    /// the voxels wire format is reversed.
    /// </remarks>
    public static bool IsVoxelLinked(string name)
    {
        if (!_entities.TryGetValue(name, out var entityData))
            return false;

        foreach (var component in entityData.Components)
        {
            if (component.Name.Contains("voxellink", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The <c>voxels</c> default an entity declares in Entities.json, as
    /// <see cref="VoxelLink"/>s. Shaped <c>[[[x,y,z], voxelIndex], ...]</c> — e.g. <c>Chest</c>
    /// is <c>[[[0,0,0], 39]]</c> and <c>PVP_Post</c> a 3-tall stack of the same index. Returns
    /// an empty list when the entity declares no default, which callers must treat as
    /// "do not send" (an empty list is not representable on the wire — see
    /// <see cref="VoxelLinkComponent"/>).
    /// </summary>
    public static List<VoxelLink> GetDefaultVoxelLinks(string name)
    {
        var links = new List<VoxelLink>();

        if (!_entities.TryGetValue(name, out var entityData))
            return links;

        var parameter = entityData.Parameters
            .FirstOrDefault(x => x.Name.Equals("voxels", StringComparison.OrdinalIgnoreCase));

        if (parameter is null || parameter.Value.ValueKind != JsonValueKind.Array)
            return links;

        foreach (var element in parameter.Value.EnumerateArray())
        {
            // Each element is [[x, y, z], voxelIndex].
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
                continue;

            var offset = element[0];

            if (offset.ValueKind != JsonValueKind.Array || offset.GetArrayLength() != 3)
                continue;

            links.Add(new VoxelLink(
                offset[0].GetInt32(),
                offset[1].GetInt32(),
                offset[2].GetInt32(),
                (byte)element[1].GetInt32()));
        }

        return links;
    }

    public static bool TryCreateEntity(int id, string name, [NotNullWhen(true)] out Entity? entity)
    {
        if (!_entities.TryGetValue(name, out var entityData))
        {
            entity = null;
            return false;
        }

        List<Component> components = new(entityData.Components.Count);

        foreach (var componentData in entityData.Components)
        {
            if (!_components.TryGetValue(componentData.Name, out var componentType))
            {
                Debug.WriteLine(componentData.Name, "Unimplemented Component");
                continue;
            }

            var component = (Component?)Activator.CreateInstance(componentType);

            ArgumentNullException.ThrowIfNull(component);

            components.Add(component);
        }

        entity = new Entity(id, entityData, components);

        return true;
    }
}