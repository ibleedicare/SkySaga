using System;
using System.Collections;
using System.Diagnostics;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using RakNet;

using SkySaga.Game.Components;

namespace SkySaga.Game.Entities;

public class Entity
{
    private readonly BitArray _sync;
    private readonly EntityData _entityData;

    private readonly Dictionary<string, Component> _components = new(StringComparer.OrdinalIgnoreCase);

    public int Id { get; }

    public string Name => _entityData.Name;

    public bool SyncRequired => _sync.HasAnySet();

    public Entity(int id, EntityData entityData, IReadOnlyCollection<Component> components)
    {
        Id = id;

        _entityData = entityData;

        foreach (var component in components)
        {
            component.ParameterChanged += OnComponentParameterChanged;

            _components.Add(component.Name, component);
        }

        _sync = new BitArray(_entityData.SyncedParametersCount);
    }

    private void OnComponentParameterChanged(Component component, string parameter)
    {
        var syncIndex = _entityData.GetParameterSyncIndex(component.Name, parameter);

        // Sync indices are dense from 0, and GetParameterSyncIndex returns -1 when
        // the parameter is not synced - so 0 is a valid index, not a miss.
        if (syncIndex >= 0)
            _sync.Set(syncIndex, true);
    }

    [Obsolete]
    public bool TryGetComponent(string name, [NotNullWhen(true)] out Component? component)
    {
        return _components.TryGetValue(name, out component);
    }


    public bool TryGetComponent<T>([NotNullWhen(true)] out T? component) where T : Component
    {
        component = null;

        if (!_components.TryGetValue(typeof(T).Name, out var baseComponent))
            return false;

        if (baseComponent is not T tempComponent)
            return false;

        component = tempComponent;

        return true;
    }

    /// <summary>
    /// Per-synced-parameter report of what this entity can actually put on the wire: the
    /// declaring component, the parameter, and whether we hold a component that serialises it.
    /// A parameter whose component we have no class for can never be sent, so its presence bit
    /// stays clear even under <c>newEntity: true</c> - which is invisible in a packet dump.
    /// </summary>
    public IEnumerable<(int Index, string Component, string Parameter, bool Supported)> DescribeSync()
    {
        for (var i = 0; i < _entityData.SyncedParametersCount; i++)
        {
            if (!_entityData.TryGetSyncedParameterInfo(i, out var componentName, out var parameterName))
            {
                yield return (i, "?", "?", false);

                continue;
            }

            // "Supported" means the bit can actually be set: we must hold the component AND
            // its TrySync must handle the parameter. Holding the component is not enough -
            // e.g. ClientInteractionComponent has no branch for interactionanglesradians, so
            // that bit stays clear even though the component is present.
            var supported = _components.TryGetValue(componentName, out var component)
                && component.TrySync(parameterName, new BitStream());

            yield return (i, componentName, parameterName, supported);
        }
    }

    public BitStream GetSyncData(bool newEntity)
    {
        var bitArray = new BitArray(_entityData.SyncedParametersCount);

        var parameterBitStream = new BitStream();

        for (var i = 0; i < _entityData.SyncedParametersCount; i++)
        {
            if (!newEntity && !_sync[i])
                continue;

            if (!_entityData.TryGetSyncedParameterInfo(i, out var componentName, out var parameterName))
                continue;

            if (!_components.TryGetValue(componentName, out var component))
                continue;

            if (component.TrySync(parameterName, parameterBitStream))
            {
                bitArray.Set(i, true);

                // Debug.WriteLine($"Synced entity. Name: {Name}, Component: {componentName}, Parameter: {parameterName}");
            }
        }

        var syncData = new BitStream();

        var data = new byte[16];

        bitArray.CopyTo(data, 0);

        for (int i = 0; i < bitArray.Count / 32; i++)
            Array.Reverse(data, i * 4, 4);

        syncData.WriteBits(data, (uint)bitArray.Count);

        syncData.WriteBits(BitConverter.GetBytes(parameterBitStream.GetNumberOfBitsUsed()), 32 - Util.NumBitsRequiredUInt32(0x20000));

        if (parameterBitStream.GetNumberOfBitsUsed() > 0)
            syncData.Write(parameterBitStream);

        _sync.SetAll(false);

        return syncData;
    }
}