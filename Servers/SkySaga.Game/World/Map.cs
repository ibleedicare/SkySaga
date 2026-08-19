using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using SkySaga.Game.Packets;
using SkySaga.Game.Entities;

namespace SkySaga.Game.World;

public class Map
{
    private static int _uniqueEntityId = 1;
    private readonly Dictionary<int, Entity> _entities = [];

    public MapDefinition Definition { get; set; }

    public IEnumerable<Entity> Entities => _entities.Values;

    /// <summary>
    /// Voxels changed since the world was generated, keyed by world voxel coordinate. The
    /// terrain itself is regenerated from a seed, so this overlay is the only record that a
    /// block was dug or placed — without it a dug block comes back as soon as the client asks
    /// the server about it again.
    /// </summary>
    /// <remarks>Air is stored as 255, the same value the generator and the wire format use.</remarks>
    public readonly Dictionary<(int X, int Y, int Z), byte> VoxelEdits = [];

    public Map(MapDefinition definition)
    {
        Definition = definition;
    }

    public bool TryGetEntity(int id, [NotNullWhen(true)] out Entity? entity)
    {
        return _entities.TryGetValue(id, out entity);
    }

    public bool TryCreateEntity(string name, [NotNullWhen(true)] out Entity? entity)
    {
        if (!EntityManager.TryCreateEntity(_uniqueEntityId++, name, out entity))
            return false;

        _entities.TryAdd(entity.Id, entity);

        return true;
    }

    public void RemoveEntity(Entity entity)
    {
        _entities.Remove(entity.Id);
    }
}