using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

using RakNet;

using SkySaga.Game.World;
using SkySaga.Game.GeoData;
using SkySaga.Game.Packets;
using SkySaga.Game.Entities;
using SkySaga.Game.Components;
using SkySaga.Game.Interfaces;
using SkySaga.Game.Packets.Common;

namespace SkySaga.Game;

public class Connection
{
    /// <summary>
    /// Layout of <c>ClientInventoryComponent.InventoryEntityList</c> (45 slots).
    /// </summary>
    /// <remarks>
    /// Nothing in Entities.json or GeoData.json records this mapping — it was read off
    /// the client UI with SKYSAGA_SLOTMAP=1, which fills every slot with Dirt using the
    /// stack count as the slot index:
    /// <code>
    ///   0..1    equipment, hands       (no stack count shown - holds 1)
    ///   2       equipment, Head
    ///   3       equipment, Torso
    ///   4       equipment, Legs
    ///   5       equipment, Arms
    ///   6       hotbar                 a single slot
    ///   7..8    not rendered anywhere  (locked hotbar slots?)
    ///   9..44   rucksack               36 squares, 6x6
    /// </code>
    /// Note Legs is 4 and Arms is 5, not the other way round. That comes from the
    /// destination field of RequestEquipInventoryItem, captured while equipping one piece
    /// of each type in turn - see <see cref="Packets.RequestEquipInventoryItem"/>. Earlier
    /// code assumed 4=Arms and 5=Legs and would have equipped both to the wrong slot.
    ///
    /// 7 and 8 are still unexplained: they are inside MaxInventorySlots and the client
    /// accepts items there, but no square in the UI shows them.
    /// </remarks>
    private const int FirstRucksackSlot = 9;

    private readonly Server _server;

    public readonly RakNetGUID Guid;

    /// <summary>Entity id to item name, so inventory logging is readable.</summary>
    public readonly Dictionary<int, string> ItemNames = [];

    public Map Map { get; }
    public Entity Player { get; }

    /// <summary>
    /// The player's latest facing (yaw, degrees), tracked from EntityMoved for /spawn
    /// placement. Kept OFF the entity so it is never synced back: the Player declares
    /// yawdegrees as a synced param, and syncing it to the client mid-gameplay null-derefs
    /// there (crash eip=0x420a43). Position is fine to sync; yawdegrees is not.
    /// </summary>
    public float FacingYawDegrees { get; set; }

    /// <summary>
    /// Hotbar square to the resource bound to it, from RequestUISettingsSlotChange, plus the
    /// square the player currently has selected (RequestUISettingsSetActiveSlot).
    /// </summary>
    /// <remarks>
    /// This is how the server knows what is in the player's hand. It matters because placing
    /// and digging arrive as the same packet: without it, a dig would consume whatever
    /// placeable stack happened to be in the rucksack instead of breaking the block.
    /// </remarks>
    public readonly Dictionary<int, uint> HotbarBindings = [];

    public int ActiveHotbarSlot { get; set; } = -1;

    /// <summary>
    /// What the client last put in a hand slot, from RequestEquipInventoryItem. More reliable
    /// than the hotbar bindings, which are only known for squares bound during this session.
    /// </summary>
    public uint? HeldResource { get; set; }

    /// <summary>The resource the player is holding, or null when the hand is empty.</summary>
    /// <remarks>
    /// The equipped item wins; the hotbar binding is the fallback for a square selected
    /// without a fresh equip.
    /// </remarks>
    public uint? ActiveResource =>
        HeldResource
        ?? (ActiveHotbarSlot >= 0 && HotbarBindings.TryGetValue(ActiveHotbarSlot, out var resource)
            ? resource
            : null);

    public Connection(Server server, Map map, RakNetGUID guid)
    {
        _server = server;

        Map = map;
        Guid = guid;

        Map.TryCreateEntity("Player", out var player);

        ArgumentNullException.ThrowIfNull(player);

        Player = player;
    }

    public void OnConnected()
    {
    }

    public void OnDisconnected()
    {
        Map.RemoveEntity(Player);

        var entityRemoved = new EntityRemoved
        {
            Id = Player.Id
        };

        _server.SendToAll(entityRemoved);
    }

    public bool ProcessPacket(PacketId packetId, BitStream bitStream)
    {
        return packetId switch
        {
            PacketId.ClientConnected => ClientConnected.Handle(this, bitStream),
            PacketId.ClientReadyToSync => ClientReadyToSync.Handle(this, bitStream),
            PacketId.ClientReadyToPlay => ClientReadyToPlay.Handle(this, bitStream),
            PacketId.ClientInitialSyncFinished => ClientInitialSyncFinished.Handle(this, bitStream),
            PacketId.RequestChatChannelData => RequestChatChannelData.Handle(this, bitStream),
            PacketId.InventoryItemSwap => InventoryItemSwap.Handle(this, bitStream),
            PacketId.InventoryItemTransferToSlot => InventoryItemTransferToSlot.Handle(this, bitStream),
            PacketId.InventoryItemDestroy => InventoryItemDestroy.Handle(this, bitStream),
            PacketId.RequestUISettingsSlotChange => RequestUISettingsSlotChange.Handle(this, bitStream),
            PacketId.RequestUISettingsSetActiveSlot => RequestUISettingsSetActiveSlot.Handle(this, bitStream),
            PacketId.RequestEquipInventoryItem => RequestEquipInventoryItem.Handle(this, bitStream),
            PacketId.NotifyPhotoCaptured => NotifyPhotoCaptured.Handle(this, bitStream),
            PacketId.PerformVoxelActions => PerformVoxelActions.Handle(this, bitStream),
            PacketId.ExecuteEntityAction => ExecuteEntityAction.Handle(this, bitStream),
            PacketId.InteractWithEntity => InteractWithEntity.Handle(this, bitStream),
            PacketId.EntityMoved => EntityMoved.Handle(this, bitStream),
            PacketId.SetLookAtDirection => SetLookAtDirection.Handle(this, bitStream),
            _ => false
        };
    }

    public void Tick()
    {
    }

    /// <summary>Occupied slots and their item names, for correlating with the UI.</summary>
    public string DescribeInventory()
    {
        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return "(no inventory)";

        var slots = inventory.InventoryEntityList;

        var occupied = new List<string>();

        for (var slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot] == 0)
                continue;

            occupied.Add($"{slot}={ItemNames.GetValueOrDefault(slots[slot], slots[slot].ToString())}");
        }

        return string.Join(" ", occupied);
    }

    public void Send(BitStream bitStream)
    {
        _server.Send(bitStream, Guid);
    }

    public void Send(ISerializablePacket packet)
    {
        _server.Send(packet.Serialize(), Guid);
    }

    public void InitialChunkSync()
    {
        // Generated terrain. Chunks are 32x32x32 voxels, one byte each, Y-major; see
        // World/TerrainGenerator.cs for the layout and the client's compression modes.
        // SKYSAGA_CHUNK_PROBE=1 sends the axis probe pattern instead of terrain.
        var size = TerrainGenerator.SizeChunks;

        var chunks = new List<(int X, int Y, int Z, byte[] Data)>(size * size);

        for (var x = 0; x < size; x++)
        {
            for (var z = 0; z < size; z++)
            {
                var data = Environment.GetEnvironmentVariable("SKYSAGA_CHUNK_PROBE") == "1"
                    ? AxisProbeChunk()
                    : TerrainGenerator.GenerateChunk(x, 0, z);

                if (data is null)
                    continue;

                ApplyVoxelEdits(data, x, 0, z);

                chunks.Add((x, 0, z, data));
            }
        }

        Console.WriteLine($"[world] sending {chunks.Count} chunks ({size}x{size}), seed {TerrainGenerator.Seed}");

        Send(new BeginSync
        {
            NumChunksToSync = chunks.Count
        });

        foreach (var chunk in chunks)
        {
            Send(new ChunkSync
            {
                Coords = new Vector<int>([chunk.X, chunk.Y, chunk.Z, 0, 0, 0, 0, 0]),
                Data1 = chunk.Data
            });
        }
    }

    /// <summary>
    /// Overwrite a freshly generated chunk with the voxels the players have changed, so dug
    /// holes and placed blocks survive a reconnect instead of the terrain seed winning.
    /// </summary>
    /// <remarks>
    /// Layout matches TerrainGenerator.GenerateChunk: byte 0 is the compression mode and the
    /// voxels follow at <c>1 + y * 32 * 32 + h1 * 32 + h2</c>, where h1 is Z and h2 is X.
    /// </remarks>
    private void ApplyVoxelEdits(byte[] data, int chunkX, int chunkY, int chunkZ)
    {
        const int size = TerrainGenerator.ChunkSize;

        foreach (var ((worldX, worldY, worldZ), material) in Map.VoxelEdits)
        {
            var localX = worldX - chunkX * size;
            var localY = worldY - chunkY * size;
            var localZ = worldZ - chunkZ * size;

            if (localX < 0 || localX >= size || localY < 0 || localY >= size || localZ < 0 || localZ >= size)
                continue;

            data[1 + localY * size * size + localZ * size + localX] = material;
        }
    }

    /// <summary>
    /// Three material slabs, one perpendicular to each axis of the flat voxel array, used
    /// to identify the axis order by looking at it: the slab that renders as the floor is
    /// the vertical axis. This is what established the Y-major layout.
    /// </summary>
    private static byte[] AxisProbeChunk()
    {
        const int size = TerrainGenerator.ChunkSize;

        var voxels = new byte[TerrainGenerator.VoxelsPerChunk + 1];

        for (var i = 0; i < TerrainGenerator.VoxelsPerChunk; i++)
        {
            var a = i / (size * size);
            var b = i / size % size;
            var c = i % size;

            voxels[i + 1] = a < 4 ? (byte)24
                : b < 4 ? (byte)13
                : c < 4 ? (byte)14
                : byte.MaxValue;
        }

        return voxels;
    }

    public void InitialEntitiySync()
    {
        // TODO: Component property defaults and database

        if (Player.TryGetComponent<ClientHealthComponent>(out var transformComponent))
            transformComponent.HalfHearts = 100;

        // The client reads this back as the id for its social-graph (friends) requests.
        if (Player.TryGetComponent<ClientOwnerComponent>(out var clientOwnerComponent))
            clientOwnerComponent.Owner = Util.CharacterUuid();

        if (Player.TryGetComponent<SmoothedTransformComponent>(out var smoothedTransformComponent))
        {
            // Position units are 1/32 of a voxel (a chunk origin is chunkCoord * 32 voxels),
            // so voxel coordinates are scaled by 32 here.
            var spawn = TerrainGenerator.Spawn();

            smoothedTransformComponent.Position =
                new Vector<int>([spawn.X * 32, spawn.Y * 32, spawn.Z * 32, 0, 0, 0, 0, 0]);
        }

        if (Player.TryGetComponent<ClientFeatureUnlockComponent>(out var clientFeatureUnlockComponent))
            clientFeatureUnlockComponent.FeatureIsLockedStatusList.Add(true);

        if (Player.TryGetComponent<ClientCraftingDropSlotsComponent>(out var clientCraftingDropSlotsComponent))
            clientCraftingDropSlotsComponent.CraftingDropSlots = [0, 0];

        if (Player.TryGetComponent<ClientPlayerNameComponent>(out var clientPlayerNameComponent))
            clientPlayerNameComponent.PlayerName =
                Environment.GetEnvironmentVariable("SKYSAGA_PLAYER_NAME") is { Length: > 0 } name ? name : "Adventurer";

        if (Player.TryGetComponent<ClientPlayerAspectsComponent>(out var clientPlayerAspectsComponent))
        {
            clientPlayerAspectsComponent.CanEditMap = true;
            clientPlayerAspectsComponent.CanDamageEntities = true;
            clientPlayerAspectsComponent.CanDamagePlayers = true;
            clientPlayerAspectsComponent.CanCreateDevices = true;
            clientPlayerAspectsComponent.CanDamageDevices = true;
            clientPlayerAspectsComponent.IsDebugPlayer = true;

            clientPlayerAspectsComponent.AccountLevel = 2;
        }

        if (Player.TryGetComponent<ClientWalletComponent>(out var clientWalletComponent))
        {
            // Currencies are resources too - Life_Ticket and Portal_Ticket are both rows
            // in GeoData.json > Resources - so resolve them the same way as items.
            (string Name, int Value)[] currencies = [("Life_Ticket", 69), ("Portal_Ticket", 420)];

            foreach (var (name, value) in currencies)
            {
                if (!GeoDataManager.TryGetResource(name, out var resource))
                {
                    Console.WriteLine($"[wallet] {name} is not in the client's resource table; skipped");

                    continue;
                }

                clientWalletComponent.Currency.CurrencyList.Add(new WalletData.CurrencyData
                {
                    NameHash = resource.NameHash,
                    Value = value
                });
            }
        }

        if (Player.TryGetComponent<ClientInventoryComponent>(out var clientInventoryComponent))
        {
            clientInventoryComponent.MaxInventorySlots = 45;

            for (var i = 0; i < clientInventoryComponent.MaxInventorySlots; i++)
                clientInventoryComponent.InventoryEntityList.Add(0);

            // Which InventoryEntityList index maps to which square in the UI is not
            // recorded anywhere in the data files, and the values below are still a guess.
            // SKYSAGA_SLOTMAP=1 resolves it empirically: it fills every slot with the same
            // stackable item, using the stack COUNT as the slot index, so each square in
            // the client is labelled with the index that produced it. Read the numbers off
            // the screen and the layout is known.
            if (Environment.GetEnvironmentVariable("SKYSAGA_SLOTMAP") == "1")
            {
                // Slot 0 is skipped: a count of 0 would read as an empty square.
                for (var slotIndex = 1; slotIndex < clientInventoryComponent.MaxInventorySlots; slotIndex++)
                    PlaceItem(clientInventoryComponent, slotIndex, "Dirt", slotIndex);

                Console.WriteLine("[inventory] slot map: every square shows its own slot index as the stack count");
            }
            else
            {
                // Item identity is crc32 of a name in the client's resource table
                // (GeoData.json > Resources), so every name is resolved through
                // GeoDataManager rather than hashed blind — see PlaceItem.
                //
                // SKYSAGA_LOADOUT="MetalPick:1,Arrow:64,..." fills from FirstRucksackSlot
                // onwards; "12=MetalPick:1" targets an explicit slot. Unknown names are
                // dropped with a warning instead of being sent.
                var loadout = Environment.GetEnvironmentVariable("SKYSAGA_LOADOUT") is { Length: > 0 } configured
                    ? configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ParseLoadoutEntry)
                        .ToArray()
                    : [(null, "GuardianArmourTorso", 1)];

                var slot = FirstRucksackSlot;

                foreach (var (explicitSlot, name, count) in loadout)
                {
                    if (explicitSlot is { } targetSlot)
                    {
                        PlaceItem(clientInventoryComponent, targetSlot, name, count);

                        continue;
                    }

                    if (slot >= clientInventoryComponent.MaxInventorySlots)
                        break;

                    if (PlaceItem(clientInventoryComponent, slot, name, count))
                        slot++;
                }
            }
        }

        foreach (var entity in Map.Entities)
        {
            var entityAdd = new EntityAdd
            {
                Id = entity.Id,
                NameHash = Util.ComputeCrc32(entity.Name),
                SyncData = entity.GetSyncData(newEntity: true)
            };

            Send(entityAdd);
        }

        Send(new ClientEntitiesSyncFinished());
    }

    /// <summary>
    /// Add an item to the first free rucksack slot mid-session (the /give admin command).
    /// Unlike the spawn-time loadout, this also sends the new item entity's EntityAdd so
    /// the already-connected client learns about it before the inventory sync references it.
    /// Must run on the game thread (see <see cref="Server"/>'s command queue).
    /// </summary>
    public string GiveItem(string name, int count)
    {
        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return "player has no inventory";

        if (!GeoDataManager.TryGetResource(name, out var resource) || !resource.IsInventoryItem)
            return $"unknown item '{name}'";

        var slots = inventory.InventoryEntityList;

        var slot = -1;

        for (var i = FirstRucksackSlot; i < slots.Count; i++)
        {
            if (slots[i] == 0)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
            return "rucksack is full";

        if (!Map.TryCreateEntity("BasicInventoryItem", out var item))
            return "could not create the item entity";

        if (item.TryGetComponent<InventoryItemComponent>(out var inventoryItemComponent))
        {
            inventoryItemComponent.InventorySlotData.Name = resource.NameHash;
            inventoryItemComponent.InventorySlotData.Count = count;
            inventoryItemComponent.InventorySlotData.ItemUUID = Util.NewGuid();
        }

        // The client must know the entity before the inventory sync points a slot at its id.
        Send(new EntityAdd
        {
            Id = item.Id,
            NameHash = Util.ComputeCrc32(item.Name),
            SyncData = item.GetSyncData(newEntity: true)
        });

        slots[slot] = item.Id;

        // Reassign so the setter raises the change and the next tick syncs the inventory.
        inventory.InventoryEntityList = slots;

        ItemNames[item.Id] = resource.Name;

        Console.WriteLine($"[give] {resource.Name} x{count} -> rucksack slot {slot}");

        return $"gave {resource.Name} x{count} (rucksack slot {slot})";
    }

    /// <summary>
    /// Spawn a world entity by its <c>Entities.json</c> name, a few voxels in front of the
    /// player (the /spawn admin command). Uses the player's live position and facing, both
    /// tracked from <see cref="Packets.EntityMoved"/>. Explicit x/y/z can come later.
    /// Must run on the game thread (see <see cref="Server"/>'s command queue).
    /// </summary>
    public string SpawnEntity(string name, bool minimal = false)
    {
        // Voxel blocks hang the client (see EntityManager.IsVoxelLinked) and then poison the
        // map for every later connection, so refuse rather than spawn a broken one. `minimal`
        // is the diagnostic escape hatch: it syncs ONLY the parameters we explicitly set
        // (position) instead of every parameter our components own, which tells us whether a
        // block hangs because of a value we send or because `voxels` is missing entirely.
        if (EntityManager.IsVoxelLinked(name) && !minimal)
            return $"'{name}' is a voxel block (needs clientvoxellinkcomponent); not supported yet";

        if (!Map.TryCreateEntity(name, out var entity))
            return $"unknown entity '{name}'";

        var placed = "at origin";

        // Position it in front of the player. Position units are 1/32 of a voxel; the player's
        // facing is yaw in degrees, so forward in the XZ plane is (sin, cos).
        if (Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform) &&
            TryGetTransform(entity, out var transform))
        {
            var position = playerTransform.Position;
            var yawRadians = FacingYawDegrees * MathF.PI / 180f;

            // ~3 voxels ahead so it does not spawn inside the player.
            const int distance = 3 * 32;

            var forwardX = (int)MathF.Round(MathF.Sin(yawRadians) * distance);
            var forwardZ = (int)MathF.Round(MathF.Cos(yawRadians) * distance);

            transform.Position = new Vector<int>(
                [position[0] + forwardX, position[1], position[2] + forwardZ, 0, 0, 0, 0, 0]);

            // Entities that declare `size` default to [1,1,1] in Entities.json. We sync the
            // parameter whenever the entity declares it, so leaving it at the C# default would
            // send a degenerate (0,0,0) size.
            transform.Size = Vector3.One;

            placed = $"at ({transform.Position[0]}, {transform.Position[1]}, {transform.Position[2]}) yaw {FacingYawDegrees:0}";
        }

        // newEntity: true syncs every parameter our components own (defaults included);
        // false syncs only what changed above, i.e. just the transform.
        Send(new EntityAdd
        {
            Id = entity.Id,
            NameHash = Util.ComputeCrc32(entity.Name),
            SyncData = entity.GetSyncData(newEntity: !minimal)
        });

        Console.WriteLine($"[spawn] {entity.Name} (id {entity.Id}) {placed}{(minimal ? " [minimal sync]" : string.Empty)}");

        return $"spawned {entity.Name} (id {entity.Id}) {placed}{(minimal ? " [minimal]" : string.Empty)}";
    }

    /// <summary>
    /// Spawn a loot chest in front of the player (the /chest admin command). With no
    /// <paramref name="loot"/> the chest is empty, which is the minimal case for isolating
    /// what the client dislikes: a bare Chest exercises only the interaction/transform sync,
    /// while loot additionally exercises the inventory list. Must run on the game thread.
    /// </summary>
    public string SpawnChest(IReadOnlyList<(string Name, int Count)> loot)
    {
        if (!Map.TryCreateEntity("Chest", out var chest))
            return "could not create the chest entity";

        if (Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform) &&
            TryGetTransform(chest, out var transform))
        {
            var position = playerTransform.Position;
            var yawRadians = FacingYawDegrees * MathF.PI / 180f;

            const int distance = 3 * 32;

            transform.Position = new Vector<int>(
                [position[0] + (int)MathF.Round(MathF.Sin(yawRadians) * distance),
                 position[1],
                 position[2] + (int)MathF.Round(MathF.Cos(yawRadians) * distance), 0, 0, 0, 0, 0]);

            transform.Size = Vector3.One;
        }

        // islootchest is what makes E *open* the chest. Left unset (false) the client instead
        // treats it as a pickup — it tries to put the chest in your bag and reports
        // "Inventory Full" because clientpickupcomponent's inventoryitementity is not sent.
        // Send ONLY the flags whose Entities.json default is wrong for a public loot chest:
        // islootchest (default false -> E would try to pick the chest up) and owneronly
        // (default true -> nobody may open it, since we send no owner). `enabled` already
        // defaults to true, and sending the full set stopped the client offering any
        // interaction at all, so every parameter we do not need stays unsent.
        // The client only instantiates an entity's components from the full new-entity payload,
        // so the chest is sent with newEntity:true below and EVERY parameter we own is given an
        // explicit, valid value here. That matters most for `owner`: it is a string and its C#
        // default is null, so an unset owner used to serialise as a null string — the poison
        // that hung the client when we last sent a full sync.
        if (chest.TryGetComponent<ClientOwnerComponent>(out var owner))
            owner.Owner = Util.CharacterUuid();

        if (chest.TryGetComponent<ClientInteractionComponent>(out var interaction))
        {
            interaction.Enabled = true;
            interaction.IsLootChest = true;
            interaction.OwnerOnly = false;
            interaction.AllowMultipleUsers = true;
            interaction.HasBeenOpened = false;
        }

        const int slots = 25;

        var placed = 0;

        // Always give the chest a fully-defined container, even when empty: with no
        // maxinventoryslots/inventoryentitylist the client has no valid inventory to open
        // into and answers an InteractAction with "Inventory Full".
        if (chest.TryGetComponent<ClientInventoryComponent>(out var inventory))
        {
            var contents = new List<int>(new int[slots]);

            inventory.MaxInventorySlots = slots;
            inventory.TakeOnly = true;

            var slot = 0;

            foreach (var (name, count) in loot)
            {
                if (slot >= slots)
                    break;

                if (!GeoDataManager.TryGetResource(name, out var resource) || !resource.IsInventoryItem)
                {
                    Console.WriteLine($"[chest] unknown item '{name}'; skipped");

                    continue;
                }

                if (!Map.TryCreateEntity("BasicInventoryItem", out var item))
                    continue;

                if (item.TryGetComponent<InventoryItemComponent>(out var itemComponent))
                {
                    itemComponent.InventorySlotData.Name = resource.NameHash;
                    itemComponent.InventorySlotData.Count = count;
                    itemComponent.InventorySlotData.ItemUUID = Util.NewGuid();
                }

                // The client must know the item entity before the chest's inventory list
                // references its id.
                Send(new EntityAdd
                {
                    Id = item.Id,
                    NameHash = Util.ComputeCrc32(item.Name),
                    SyncData = item.GetSyncData(newEntity: true)
                });

                ItemNames[item.Id] = resource.Name;

                contents[slot++] = item.Id;
                placed++;
            }

            inventory.InventoryEntityList = contents;
        }

        // newEntity:true — the client builds the entity's components from this payload, and
        // FUN_007fe280 needs a real ClientInteractionComponent to offer InteractMode at all.
        // Safe now that every parameter above has an explicit value.
        Send(new EntityAdd
        {
            Id = chest.Id,
            NameHash = Util.ComputeCrc32(chest.Name),
            SyncData = chest.GetSyncData(newEntity: true)
        });

        Console.WriteLine($"[chest] spawned (id {chest.Id}) with {placed} item(s)");

        return $"spawned loot chest (id {chest.Id}) with {placed} item(s)";
    }

    /// <summary>
    /// Fetch an entity's transform, whether it is declared as a plain
    /// <see cref="TransformComponent"/> or the <see cref="SmoothedTransformComponent"/>
    /// subclass (components are keyed by their concrete class name).
    /// </summary>
    private static bool TryGetTransform(Entity entity, [NotNullWhen(true)] out TransformComponent? transform)
    {
        if (entity.TryGetComponent<SmoothedTransformComponent>(out var smoothed))
        {
            transform = smoothed;
            return true;
        }

        if (entity.TryGetComponent<TransformComponent>(out var plain))
        {
            transform = plain;
            return true;
        }

        transform = null;
        return false;
    }

    /// <summary>
    /// Put an item into one specific inventory slot mid-session — the admin panel's
    /// drag-and-drop. Same as <see cref="GiveItem"/> but the slot is chosen by the caller,
    /// so it can target the rucksack, the hotbar or an equipment square. Passing a null
    /// <paramref name="name"/> clears the slot. Must run on the game thread.
    /// </summary>
    public string SetSlot(string? name, int count, int slot)
    {
        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return "player has no inventory";

        var slots = inventory.InventoryEntityList;

        if (slot < 0 || slot >= slots.Count)
            return $"slot {slot} is out of range (0-{slots.Count - 1})";

        if (string.IsNullOrEmpty(name))
        {
            slots[slot] = 0;
            inventory.InventoryEntityList = slots;

            Console.WriteLine($"[admin] cleared slot {slot}");

            return $"cleared slot {slot}";
        }

        if (!GeoDataManager.TryGetResource(name, out var resource) || !resource.IsInventoryItem)
            return $"unknown item '{name}'";

        if (!Map.TryCreateEntity("BasicInventoryItem", out var item))
            return "could not create the item entity";

        if (item.TryGetComponent<InventoryItemComponent>(out var inventoryItemComponent))
        {
            inventoryItemComponent.InventorySlotData.Name = resource.NameHash;
            inventoryItemComponent.InventorySlotData.Count = count;
            inventoryItemComponent.InventorySlotData.ItemUUID = Util.NewGuid();
        }

        // The client must know the entity before the inventory sync points a slot at its id.
        Send(new EntityAdd
        {
            Id = item.Id,
            NameHash = Util.ComputeCrc32(item.Name),
            SyncData = item.GetSyncData(newEntity: true)
        });

        slots[slot] = item.Id;

        // Reassign so the setter raises the change and the next tick syncs the inventory.
        inventory.InventoryEntityList = slots;

        ItemNames[item.Id] = resource.Name;

        Console.WriteLine($"[admin] {resource.Name} x{count} -> slot {slot}");

        return $"{resource.Name} x{count} -> slot {slot}";
    }

    /// <summary>
    /// Split <paramref name="count"/> items off the stack in <paramref name="sourceSlot"/> into
    /// the empty <paramref name="targetSlot"/>, as a new item entity. Returns false when the
    /// move is not a split (whole stack, empty source, occupied target), leaving the caller to
    /// fall back to its normal move/swap.
    /// </summary>
    /// <remarks>
    /// The client asks for this by sending a transfer whose count is smaller than the stack —
    /// dragging half of a 50 stack arrives as count 25. A stack is one entity carrying an
    /// <see cref="InventoryItemComponent"/>, so splitting means shrinking that entity's count
    /// and creating a second entity for the remainder.
    /// </remarks>
    public bool TrySplitStack(int sourceSlot, int targetSlot, int count)
    {
        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return false;

        var slots = inventory.InventoryEntityList;

        if (sourceSlot < 0 || sourceSlot >= slots.Count || targetSlot < 0 || targetSlot >= slots.Count)
            return false;

        // Only ever split into an empty square; merging into an occupied one is a different
        // operation and would need the stack limit checking.
        if (slots[targetSlot] != 0 || slots[sourceSlot] == 0)
            return false;

        if (!Map.TryGetEntity(slots[sourceSlot], out var source) ||
            !source.TryGetComponent<InventoryItemComponent>(out var sourceItem))
            return false;

        var data = sourceItem.InventorySlotData;

        // count == the whole stack is a plain move, not a split.
        if (count <= 0 || count >= data.Count)
            return false;

        data.Count -= count;

        // Reassign so the setter raises the change; mutating the object alone would not sync.
        sourceItem.InventorySlotData = data;

        if (!Map.TryCreateEntity("BasicInventoryItem", out var item))
            return false;

        if (item.TryGetComponent<InventoryItemComponent>(out var splitItem))
        {
            splitItem.InventorySlotData.Name = data.Name;
            splitItem.InventorySlotData.Count = count;
            splitItem.InventorySlotData.ItemUUID = Util.NewGuid();
        }

        // The client must know the entity before the inventory sync points a slot at its id.
        Send(new EntityAdd
        {
            Id = item.Id,
            NameHash = Util.ComputeCrc32(item.Name),
            SyncData = item.GetSyncData(newEntity: true)
        });

        slots[targetSlot] = item.Id;

        inventory.InventoryEntityList = slots;

        if (ItemNames.TryGetValue(source.Id, out var itemName))
            ItemNames[item.Id] = itemName;

        Console.WriteLine($"[inventory] split {count} off slot {sourceSlot} -> slot {targetSlot} "
            + $"({data.Count} left)");

        return true;
    }

    /// <summary>
    /// Destroy <paramref name="count"/> items from <paramref name="slot"/> — the rucksack's
    /// trash can. Destroying the whole stack empties the square and removes the entity;
    /// destroying part of it just shrinks the stack.
    /// </summary>
    public void DestroyStack(int slot, int count)
    {
        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return;

        var slots = inventory.InventoryEntityList;

        if (slot < 0 || slot >= slots.Count || slots[slot] == 0)
            return;

        var entityId = slots[slot];

        var remaining = 0;

        if (Map.TryGetEntity(entityId, out var item) &&
            item.TryGetComponent<InventoryItemComponent>(out var itemComponent))
        {
            var data = itemComponent.InventorySlotData;

            // A count of 0 means "all of it" — the client sends the whole stack for a plain
            // delete, but be tolerant of a zero.
            remaining = count <= 0 ? 0 : data.Count - count;

            if (remaining > 0)
            {
                data.Count = remaining;

                // Reassign so the setter raises the change and the stack's new size syncs.
                itemComponent.InventorySlotData = data;

                Console.WriteLine($"[inventory] destroyed {count} from slot {slot} ({remaining} left)");

                return;
            }
        }

        slots[slot] = 0;

        inventory.InventoryEntityList = slots;

        Map.RemoveEntity(item!);

        Send(new EntityRemoved { Id = entityId });

        ItemNames.Remove(entityId);

        Console.WriteLine($"[inventory] destroyed slot {slot} (entity {entityId})");
    }

    /// <summary>
    /// Take one block off whatever placeable stack the player is holding, after they place a
    /// voxel. Returns the item's name when something was consumed.
    /// </summary>
    /// <remarks>
    /// Only items whose GeoData <c>ActionVoxel</c> is <c>PlaceVoxel</c> are consumed — a
    /// pickaxe (<c>Dig</c>) triggers the same voxel action but must not shrink.
    ///
    /// The stack is looked up by the resource bound to the player's active hotbar square
    /// (<see cref="ActiveResource"/>), not by scanning for any placeable item: binding a stack
    /// to the hotbar leaves it in the rucksack, so a dig with a tool would otherwise eat the
    /// Dirt sitting in the bag instead of breaking the block.
    /// </remarks>
    public string? ConsumeHeldBlock()
    {
        if (ActiveResource is not { } activeResource)
            return null;

        // Holding a tool (Dig) or anything unplaceable is not a placement.
        if (!GeoDataManager.TryGetResource(activeResource, out var held) || !held.IsPlaceableBlock)
            return null;

        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return null;

        var slots = inventory.InventoryEntityList;

        for (var slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot] == 0)
                continue;

            if (!Map.TryGetEntity(slots[slot], out var entity) ||
                !entity.TryGetComponent<InventoryItemComponent>(out var item))
                continue;

            var data = item.InventorySlotData;

            // Only the stack of the resource actually being held.
            if (data.Name != activeResource ||
                !GeoDataManager.TryGetResource(activeResource, out var resource))
                continue;

            if (data.Count > 1)
            {
                data.Count--;

                // Reassign so the setter raises the change and the new count syncs.
                item.InventorySlotData = data;

                // Quiet by default: one line per placed block is enough to cause a hitch
                // while building. SKYSAGA_LOG_VOXEL=1 turns it on with the voxel decode.
                if (Environment.GetEnvironmentVariable("SKYSAGA_LOG_VOXEL") == "1")
                    Console.WriteLine($"[inventory] placed {resource.Name}, {data.Count} left in slot {slot}");
            }
            else
            {
                var entityId = slots[slot];

                // A held stack is referenced from both the hand slot and the rucksack square
                // it came from (see RequestEquipInventoryItem), so clear every slot pointing at
                // it — leaving one behind would dangle at a removed entity.
                for (var i = 0; i < slots.Count; i++)
                {
                    if (slots[i] == entityId)
                        slots[i] = 0;
                }

                inventory.InventoryEntityList = slots;

                Map.RemoveEntity(entity);

                Send(new EntityRemoved { Id = entityId });

                ItemNames.Remove(entityId);

                Console.WriteLine($"[inventory] placed the last {resource.Name} from slot {slot}");
            }

            return resource.Name;
        }

        return null;
    }

    /// <summary>How many dig packets on one voxel before it breaks.</summary>
    /// <remarks>
    /// The client streams a PerformVoxelActions packet per dig tick and shows three crack
    /// stages, but nothing in the packet says which tick finished the block — the trailing
    /// bits are padding, and every field is identical across the run. The server is the
    /// authority, so it counts ticks and decides.
    /// </remarks>
    private const int DigTicksToBreak = 3;

    /// <summary>Accumulated dig ticks per world voxel.</summary>
    private readonly Dictionary<(int X, int Y, int Z), int> _digDamage = [];

    /// <summary>
    /// A dig tick on one voxel. Once enough ticks land the voxel is removed from the world,
    /// the change is pushed to the client, and its material's item goes into the rucksack.
    /// </summary>
    public string? Dig(int chunkX, int chunkY, int chunkZ, int voxelX, int voxelY, int voxelZ)
    {
        const int chunkSize = TerrainGenerator.ChunkSize;

        var world = (X: chunkX * chunkSize + voxelX,
                     Y: chunkY * chunkSize + voxelY,
                     Z: chunkZ * chunkSize + voxelZ);

        var ticks = _digDamage.GetValueOrDefault(world) + 1;

        if (ticks < DigTicksToBreak)
        {
            _digDamage[world] = ticks;

            return null;
        }

        _digDamage.Remove(world);

        var material = MaterialAt(world);

        // Record the hole and tell the client, so the block actually goes away instead of
        // lingering as an invisible one that reappears when hit.
        Map.VoxelEdits[world] = Air;

        SendVoxelEdit(chunkX, chunkY, chunkZ, voxelX, voxelY, voxelZ, Air);

        var loot = TerrainGenerator.LootFor(material);

        if (loot is null)
        {
            Console.WriteLine($"[dig] broke voxel ({world.X},{world.Y},{world.Z}) material {material} - no loot");

            return null;
        }

        Console.WriteLine($"[dig] broke voxel ({world.X},{world.Y},{world.Z}) -> {loot}");

        GiveItem(loot, 1);

        return loot;
    }

    /// <summary>
    /// Put the held block into a voxel and tell the client. The caller has already offset the
    /// coordinate by the clicked face's direction.
    /// </summary>
    public void PlaceVoxel(int chunkX, int chunkY, int chunkZ, int voxelX, int voxelY, int voxelZ)
    {
        // Consuming also identifies the block: it returns the item name it took.
        var placed = ConsumeHeldBlock();

        if (placed is null)
            return;

        var material = TerrainGenerator.MaterialFor(placed);

        if (material is not { } value)
            return;

        const int chunkSize = TerrainGenerator.ChunkSize;

        var world = (X: chunkX * chunkSize + voxelX,
                     Y: chunkY * chunkSize + voxelY,
                     Z: chunkZ * chunkSize + voxelZ);

        Map.VoxelEdits[world] = value;

        SendVoxelEdit(chunkX, chunkY, chunkZ, voxelX, voxelY, voxelZ, value);
    }

    /// <summary>Air, matching the generator and the wire format.</summary>
    private const byte Air = byte.MaxValue;

    /// <summary>The material at a world voxel, edits included.</summary>
    private byte MaterialAt((int X, int Y, int Z) world)
        => Map.VoxelEdits.TryGetValue(world, out var edited)
            ? edited
            : TerrainGenerator.MaterialAt(world.X, world.Y, world.Z);

    private void SendVoxelEdit(int chunkX, int chunkY, int chunkZ, int voxelX, int voxelY, int voxelZ, byte material)
    {
        Send(new PartialChunkEditsSync
        {
            ChunkX = chunkX,
            ChunkY = chunkY,
            ChunkZ = chunkZ,
            Edits =
            [
                new PartialChunkEditsSync.Edit
                {
                    VoxelIndex = material,
                    Voxels = [(voxelX, voxelY, voxelZ)]
                }
            ]
        });
    }

    /// <summary>Slot index to item name and stack count, for the admin panel's live view.</summary>
    public Dictionary<int, string> SlotContents()
    {
        var contents = new Dictionary<int, string>();

        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return contents;

        var slots = inventory.InventoryEntityList;

        for (var slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot] != 0 && ItemNames.TryGetValue(slots[slot], out var itemName))
                contents[slot] = itemName;
        }

        return contents;
    }

    /// <summary>
    /// Parse one SKYSAGA_LOADOUT entry: <c>Name</c>, <c>Name:count</c>, or
    /// <c>slot=Name:count</c> to target a specific slot instead of filling in order.
    /// </summary>
    private static (int? Slot, string Name, int Count) ParseLoadoutEntry(string entry)
    {
        int? slot = null;

        var equals = entry.IndexOf('=');

        if (equals > 0 && int.TryParse(entry[..equals], out var parsedSlot))
        {
            slot = parsedSlot;
            entry = entry[(equals + 1)..];
        }

        var parts = entry.Split(':', 2);

        return (slot, parts[0], parts.Length > 1 && int.TryParse(parts[1], out var count) ? count : 1);
    }

    /// <summary>
    /// Put <paramref name="name"/> into an inventory slot, backed by its own
    /// <c>BasicInventoryItem</c> entity. Returns false if nothing was placed.
    /// </summary>
    /// <remarks>
    /// The name is resolved through the client's own resource table first. An unknown
    /// hash makes the client dereference a null item definition and die with
    /// <c>Unhandled page fault on read access to 00000008 at 0071A461</c>, which shows
    /// up as a silent client exit with nothing in the server log — so a name that is not
    /// in the table is dropped here, loudly, instead of being sent.
    /// </remarks>
    private bool PlaceItem(ClientInventoryComponent inventory, int slot, string name, int count)
    {
        if (slot < 0 || slot >= inventory.InventoryEntityList.Count)
        {
            Console.WriteLine($"[inventory] slot {slot} is out of range for {name}");

            return false;
        }

        if (!GeoDataManager.TryGetResource(name, out var resource))
        {
            Console.WriteLine($"[inventory] {name} is not in the client's resource table; skipped");

            return false;
        }

        if (!resource.IsInventoryItem)
        {
            Console.WriteLine($"[inventory] {name} is not an inventory item; skipped");

            return false;
        }

        if (!Map.TryCreateEntity("BasicInventoryItem", out var item))
            return false;

        if (item.TryGetComponent<InventoryItemComponent>(out var inventoryItemComponent))
        {
            inventoryItemComponent.InventorySlotData.Name = resource.NameHash;
            inventoryItemComponent.InventorySlotData.Count = count;
            inventoryItemComponent.InventorySlotData.ItemUUID = Util.NewGuid();
        }

        inventory.InventoryEntityList[slot] = item.Id;

        ItemNames[item.Id] = resource.Name;

        var detail = resource.IsArmour
            ? $"{resource.SubCategory} armour, protection {resource.ClothingProtection}"
            : resource.SubCategory is { Length: > 0 } subCategory ? subCategory : resource.RarityLevel;

        Console.WriteLine($"[inventory] slot {slot} = {resource.Name} x{count} ({detail})");

        return true;
    }
}