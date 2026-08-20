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
            PacketId.InventoryItemTransferAll => InventoryItemTransferAll.Handle(this, bitStream),
            PacketId.RequestUISettingsSlotChange => RequestUISettingsSlotChange.Handle(this, bitStream),
            PacketId.RequestUISettingsSetActiveSlot => RequestUISettingsSetActiveSlot.Handle(this, bitStream),
            PacketId.RequestEquipInventoryItem => RequestEquipInventoryItem.Handle(this, bitStream),
            PacketId.NotifyPhotoCaptured => NotifyPhotoCaptured.Handle(this, bitStream),
            PacketId.PerformVoxelActions => PerformVoxelActions.Handle(this, bitStream),
            PacketId.ExecuteEntityAction => ExecuteEntityAction.Handle(this, bitStream),
            PacketId.InteractWithEntity => InteractWithEntity.Handle(this, bitStream),
            PacketId.EntityMoved => EntityMoved.Handle(this, bitStream),
            PacketId.MailCheck => MailCheck.Handle(this, bitStream),
            PacketId.MailRead => MailRead.Handle(this, bitStream),
            PacketId.MailGiftSelected => MailGiftSelected.Handle(this, bitStream),
            PacketId.TakeMailAttachment => TakeMailAttachment.Handle(this, bitStream),
            PacketId.DeleteMail => DeleteMail.Handle(this, bitStream),
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
    /// Drop a resource on the floor as a pickup the player can walk over and collect — what
    /// breaking a block or harvesting an ore seam should produce.
    /// </summary>
    /// <remarks>
    /// A floor drop is two entities. The <c>BasicInventoryItem</c> holds the resource and count
    /// (exactly as a rucksack slot does), and the <c>Pickup</c> points at it and gives it a
    /// position. Both must reach the client before the Pickup's id is referenced, so the item is
    /// added first.
    /// </remarks>
    /// <summary>Tracks the loot table each spawned resource node rolls when harvested.</summary>
    private readonly Dictionary<int, string> _nodeLootTables = [];

    /// <summary>The marker voxel anchoring each node, so harvesting can clear it again.</summary>
    private readonly Dictionary<int, (int X, int Y, int Z)> _nodeVoxels = [];

    /// <summary>
    /// Spawn a harvestable resource node next to the player — the /ore command.
    /// </summary>
    /// <remarks>
    /// Uses the <c>Tree</c> entity, which build 10414 already has. Later builds add dedicated ore
    /// nodes (<c>TreeMetalOre</c>, <c>TreeKeystoneOre</c>) but they are the same entity shape with
    /// a different <c>treetype</c>, so a Tree carrying an ore loot table exercises the real
    /// harvest path on the client we target. The node looks like a tree; only its loot is ore.
    /// </remarks>
    public string SpawnResourceNode(string lootTableName)
    {
        if (!GeoDataManager.TryGetLootTable(lootTableName, out var table))
            return $"unknown loot table '{lootTableName}'";

        if (!Map.TryCreateEntity("Tree", out var node))
            return "could not create the Tree entity";

        if (node.TryGetComponent<ClientTreeComponent>(out var tree))
            tree.TrunkLootTable = Util.ComputeCrc32(table.Name);

        var placed = "at origin";

        if (Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform) &&
            TryGetTransform(node, out var transform))
        {
            var position = playerTransform.Position;
            var yawRadians = FacingYawDegrees * MathF.PI / 180f;

            // Two voxels ahead, close enough to hit without walking.
            const int distance = 2 * VoxelUnits;

            var forwardX = (int)MathF.Round(MathF.Sin(yawRadians) * distance);
            var forwardZ = (int)MathF.Round(MathF.Cos(yawRadians) * distance);

            transform.Position = new Vector<int>(
                [position[0] + forwardX, position[1], position[2] + forwardZ, 0, 0, 0, 0, 0]);

            placed = $"at ({transform.Position[0]},{transform.Position[1]},{transform.Position[2]})";
        }

        // NOTE: the anchor voxel is deliberately NOT placed. FUN_00846960 shows the client
        // grows a tree from its own part list and checks every part against the voxel already in
        // the world ("Expected a tree voxel (%d), but got %d"), so a single marker is not enough
        // — and writing one only punches an invisible hole in the ground. Rendering needs the
        // treetype's part data from TreeList/TreePartList in the .pc archives.

        Send(new EntityAdd
        {
            Id = node.Id,
            NameHash = Util.ComputeCrc32(node.Name),
            SyncData = node.GetSyncData(newEntity: true)
        });

        _nodeLootTables[node.Id] = table.Name;

        Console.WriteLine($"[node] Tree {node.Id} {placed} loot {table.Name}");

        return $"spawned a resource node {placed} dropping {table.Name}";
    }

    /// <summary>
    /// Harvest a spawned resource node: roll its loot table, drop the results at its feet and
    /// remove it. Returns false when the entity is not one of ours.
    /// </summary>
    public bool HarvestResourceNode(int entityId)
    {
        if (!_nodeLootTables.TryGetValue(entityId, out var tableName) ||
            !Map.TryGetEntity(entityId, out var node))
            return false;

        var (dropX, dropY, dropZ) = (0, 0, 0);

        if (TryGetTransform(node, out var transform))
        {
            dropX = transform.Position[0];
            dropY = transform.Position[1];
            dropZ = transform.Position[2];
        }

        foreach (var (name, count) in LootTables.Roll(tableName, _loot))
        {
            Console.WriteLine($"[node] harvested {entityId} -> {tableName} -> {name} x{count}");

            DropPickup(name, count, dropX, dropY, dropZ);
        }

        _nodeLootTables.Remove(entityId);

        // Take the anchor voxel away too, or the client keeps drawing the tree.
        if (_nodeVoxels.Remove(entityId, out var voxel))
        {
            Map.VoxelEdits[voxel] = Air;

            SendWorldVoxel(voxel.X, voxel.Y, voxel.Z, Air);
        }

        Map.RemoveEntity(node);
        Send(new EntityRemoved { Id = entityId });

        return true;
    }

    /// <summary>
    /// Collect a floor pickup into the rucksack — the server side of the client's
    /// <c>ResourcePickupAction</c>, which it fires when the player walks over or clicks a drop.
    /// </summary>
    /// <remarks>
    /// The Pickup entity is only a wrapper; the item entity it points at is the thing that goes
    /// into a slot. That entity is already known to the client, so it just needs a slot pointed
    /// at it — no EntityAdd, unlike <see cref="GiveItem"/>. Stacking into an existing pile is
    /// tried first so collecting dirt reads as "+1" rather than filling a fresh slot each time,
    /// which is what the client does with its own drops.
    ///
    /// The Pickup is removed either way. Leaving it alive would let the same drop be collected
    /// repeatedly, since the client keeps firing the action while the player stands on it — the
    /// log shows a dozen of them for a single item.
    /// </remarks>
    public bool CollectPickup(int pickupEntityId)
    {
        if (!Map.TryGetEntity(pickupEntityId, out var pickup) ||
            !pickup.TryGetComponent<ClientResourcePickupComponent>(out var pickupComponent))
            return false;

        var itemId = pickupComponent.InventoryItemEntity;

        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory) ||
            !Map.TryGetEntity(itemId, out var item) ||
            !item.TryGetComponent<InventoryItemComponent>(out var itemComponent))
            return false;

        var slots = inventory.InventoryEntityList;
        var data = itemComponent.InventorySlotData;

        var collected = false;

        // Merge into a matching stack that still has room.
        if (data.Name is { } nameHash && GeoDataManager.TryGetResource(nameHash, out var resource))
        {
            var limit = StackLimit(resource);

            for (var i = FirstRucksackSlot; i < slots.Count && !collected; i++)
            {
                if (slots[i] == 0 ||
                    !Map.TryGetEntity(slots[i], out var existing) ||
                    !existing.TryGetComponent<InventoryItemComponent>(out var existingItem))
                    continue;

                var existingData = existingItem.InventorySlotData;

                if (existingData.Name != nameHash || existingData.Count + data.Count > limit)
                    continue;

                existingData.Count += data.Count;

                // Reassign so the setter raises the change and the stack syncs.
                existingItem.InventorySlotData = existingData;

                // The item entity is now redundant — the stack it merged into carries the count.
                Map.RemoveEntity(item);
                Send(new EntityRemoved { Id = itemId });

                collected = true;

                Console.WriteLine($"[pickup] {resource.Name} x{data.Count} merged into slot {i}");
            }
        }

        // Otherwise it needs a slot of its own.
        if (!collected)
        {
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
            {
                Console.WriteLine("[pickup] rucksack is full; leaving the drop on the floor");

                return false;
            }

            slots[slot] = itemId;

            // Reassign so the setter raises the change and the next tick syncs the inventory.
            inventory.InventoryEntityList = slots;

            Console.WriteLine($"[pickup] item {itemId} -> rucksack slot {slot}");
        }

        Map.RemoveEntity(pickup);
        Send(new EntityRemoved { Id = pickupEntityId });

        return true;
    }

    /// <summary>
    /// Drop a resource a few voxels in front of the player — the /drop admin command, and the
    /// quickest way to exercise the pickup path without breaking anything.
    /// </summary>
    public string DropPickupInFront(string itemName, int count)
    {
        if (!Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform))
            return "player has no transform yet";

        var position = playerTransform.Position;
        var yawRadians = FacingYawDegrees * MathF.PI / 180f;

        // Position units are 1/32 of a voxel; forward in the XZ plane is (sin, cos).
        const int distance = 2 * 32;

        var forwardX = (int)MathF.Round(MathF.Sin(yawRadians) * distance);
        var forwardZ = (int)MathF.Round(MathF.Cos(yawRadians) * distance);

        return DropPickup(itemName, count, position[0] + forwardX, position[1], position[2] + forwardZ);
    }

    public string DropPickup(string itemName, int count, int worldX, int worldY, int worldZ)
    {
        if (!GeoDataManager.TryGetResource(itemName, out var resource))
            return $"unknown item '{itemName}'";

        if (!Map.TryCreateEntity("BasicInventoryItem", out var item))
            return "could not create the item entity";

        if (item.TryGetComponent<InventoryItemComponent>(out var inventoryItemComponent))
        {
            inventoryItemComponent.InventorySlotData.Name = resource.NameHash;
            inventoryItemComponent.InventorySlotData.Count = count;
            inventoryItemComponent.InventorySlotData.ItemUUID = Util.NewGuid();
        }

        Send(new EntityAdd
        {
            Id = item.Id,
            NameHash = Util.ComputeCrc32(item.Name),
            SyncData = item.GetSyncData(newEntity: true)
        });

        if (!Map.TryCreateEntity("Pickup", out var pickup))
            return "could not create the pickup entity";

        if (pickup.TryGetComponent<ClientResourcePickupComponent>(out var pickupComponent))
        {
            var position = new Vector<int>([worldX, worldY, worldZ, 0, 0, 0, 0, 0]);

            pickupComponent.InventoryItemEntity = item.Id;
            pickupComponent.PickupEnabled = true;
            pickupComponent.StartPosition = position;
            pickupComponent.TargetPosition = position;
        }

        Send(new EntityAdd
        {
            Id = pickup.Id,
            NameHash = Util.ComputeCrc32(pickup.Name),
            SyncData = pickup.GetSyncData(newEntity: true)
        });

        ItemNames[item.Id] = resource.Name;

        Console.WriteLine($"[drop] {resource.Name} x{count} at ({worldX},{worldY},{worldZ}) " +
                          $"item={item.Id} pickup={pickup.Id}");

        return $"dropped {resource.Name} x{count} at ({worldX},{worldY},{worldZ})";
    }

    /// <summary>
    /// Spawn a world entity by its <c>Entities.json</c> name, a few voxels in front of the
    /// player (the /spawn admin command). Uses the player's live position and facing, both
    /// tracked from <see cref="Packets.EntityMoved"/>. Explicit x/y/z can come later.
    /// Must run on the game thread (see <see cref="Server"/>'s command queue).
    /// </summary>
    public string SpawnEntity(string name, bool minimal = false) => SpawnEntityCore(name, minimal, out _);

    private string SpawnEntityCore(string name, bool minimal, out Entity? spawned)
    {
        spawned = null;

        // The old refusal of voxel-linked entities is gone: ClientVoxelLinkComponent is
        // implemented, so `voxels` is sent and these spawn like anything else. `minimal` remains
        // the diagnostic escape hatch — it syncs ONLY what we explicitly set (position) rather
        // than every parameter our components own.
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

        if (!minimal)
        {
            ApplyPlacedDefaults(entity);

            var unsendable = entity.DescribeSync().Where(x => !x.Supported).ToList();

            if (unsendable.Count > 0)
            {
                Console.WriteLine($"[spawn] {entity.Name}: {unsendable.Count} parameter(s) cannot be sent: "
                    + string.Join(", ", unsendable.Select(x => $"{x.Parameter} ({x.Component})")));
            }
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

        spawned = entity;

        return $"spawned {entity.Name} (id {entity.Id}) {placed}{(minimal ? " [minimal]" : string.Empty)}";
    }

    /// <summary>
    /// The baseline every entity we drop into the world needs: its anchor voxels, an
    /// interaction the client will offer, a real owner uuid and a describable pickup.
    /// </summary>
    /// <remarks>
    /// Factored out of <see cref="SpawnEntityCore"/> so the grid and step-through spawners give
    /// their entities exactly the same treatment a /spawn does — otherwise a failure there is
    /// ambiguous between "the entity is broken" and "we forgot a baseline parameter".
    /// </remarks>
    private void ApplyPlacedDefaults(Entity entity)
    {
        // Anything voxel-linked needs its anchor voxels, or it is a mesh floating outside
        // the world grid. The shape comes from the entity's own Entities.json default.
        if (entity.TryGetComponent<ClientVoxelLinkComponent>(out var voxelLink))
        {
            voxelLink.Voxels = EntityManager.GetDefaultVoxelLinks(entity.Name);
            voxelLink.CanReplaceVoxelsOfEntityID = 0;
        }

        // Give every interactable the same baseline the chest needed: enabled, openable by
        // anyone, and an owner string that is a real uuid rather than a null.
        if (entity.TryGetComponent<ClientInteractionComponent>(out var interaction))
        {
            interaction.Enabled = true;
            interaction.OwnerOnly = false;
            interaction.AllowMultipleUsers = true;
            interaction.HasBeenOpened = false;
        }

        if (entity.TryGetComponent<ClientOwnerComponent>(out var owner))
            owner.Owner = Util.CharacterUuid();

        if (entity.TryGetComponent<ClientPickupComponent>(out var pickup))
        {
            pickup.InventoryItemEntity = 0;
            pickup.PlacedByUUID = Util.CharacterUuid();
            pickup.OnlyOwnerCanPickup = true;
            pickup.CanPickUpPopulatedInventories = false;
        }
    }

    /// <summary>
    /// Step through the interactables one at a time: remove the one under test and put the next
    /// one in front of the player.
    /// </summary>
    /// <remarks>
    /// This, not <see cref="SpawnInteractableGrid"/>, is the way to walk the list. A grid of all
    /// fifty collides with itself no matter how it is spaced — an Airship is a vehicle-sized
    /// mesh and most devices are 2x2x3 voxel blocks — and overlapping meshes make "which entity
    /// did I just press E on" unanswerable. One at a time keeps every test unambiguous and keeps
    /// the map small.
    ///
    /// The previous test entity is removed, so the map does not accumulate; nothing else the
    /// player placed is touched.
    /// </remarks>
    public string SpawnNextInteractable(string? jumpTo = null, int step = 1)
    {
        var names = EntityManager.GetEntityNamesWithComponent("interaction");

        if (names.Count == 0)
            return "no interactable entities found";

        if (jumpTo is { Length: > 0 })
        {
            var index = names.FindIndex(x => x.Contains(jumpTo, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return $"no interactable matches '{jumpTo}'";

            _testInteractableIndex = index;
        }
        else
        {
            // Wrap in both directions so /prev off the start lands on the last one.
            _testInteractableIndex = ((_testInteractableIndex + step) % names.Count + names.Count) % names.Count;
        }

        if (_testInteractableEntityId != 0 && Map.TryGetEntity(_testInteractableEntityId, out var previous))
        {
            Map.RemoveEntity(previous);

            Send(new EntityRemoved { Id = previous.Id });

            Console.WriteLine($"[next] removed {previous.Name} (id {previous.Id})");
        }

        _testInteractableEntityId = 0;

        var name = names[_testInteractableIndex];

        var result = SpawnEntityCore(name, minimal: false, out var entity);

        if (entity is null)
            return $"{_testInteractableIndex + 1}/{names.Count} {name}: {result}";

        _testInteractableEntityId = entity.Id;

        var unsendable = entity.DescribeSync().Where(x => !x.Supported).Select(x => x.Parameter).ToList();

        Console.WriteLine($"[next] {_testInteractableIndex + 1}/{names.Count} {name} (id {entity.Id})"
            + (unsendable.Count > 0 ? $" — cannot send: {string.Join(", ", unsendable)}" : string.Empty));

        return $"{_testInteractableIndex + 1}/{names.Count} {name} (id {entity.Id})"
            + (unsendable.Count > 0 ? $" — cannot send: {string.Join(", ", unsendable)}" : string.Empty);
    }

    private int _testInteractableIndex = -1;
    private int _testInteractableEntityId;

    /// <summary>
    /// Spawn one of every interactable entity in a grid in front of the player, so the whole
    /// interaction surface can be walked and tested in a single session.
    /// </summary>
    /// <remarks>
    /// <see cref="SpawnEntity"/> puts everything at the same spot three voxels ahead, which is
    /// useless for fifty entities, so this lays them out on a grid in the world axes rather than
    /// along the player's facing. Row/column are voxel-aligned and the manifest is logged with
    /// the grid coordinate of each entity, so a client-side failure can be tied back to a name
    /// without guessing which mesh is which.
    ///
    /// Everything gets the same <see cref="ApplyPlacedDefaults"/> treatment a /spawn does, so a
    /// failure here is a failure of the entity, not of a missing baseline parameter.
    /// </remarks>
    public string SpawnInteractableGrid(string componentSubstring = "interaction", string? filter = null)
    {
        var names = EntityManager.GetEntityNamesWithComponent(componentSubstring);

        if (filter is { Length: > 0 })
            names = names.Where(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (names.Count == 0)
            return $"no entity declares a '{componentSubstring}' component matching '{filter}'";

        if (!Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform))
            return "the player has no transform; cannot place a grid";

        // Voxels are 32 units. Four voxels apart is wide enough that the bigger devices (an
        // Anvil is 2x2x3) do not overlap, and tight enough to walk in a few seconds.
        const int spacing = 4 * 32;
        const int columns = 8;

        // Start one row behind and to the left of the player so the grid grows away from them
        // rather than on top of them.
        var originX = playerTransform.Position[0] - 2 * spacing;
        var originZ = playerTransform.Position[2] + 2 * spacing;

        var spawned = 0;
        var failed = new List<string>();

        Console.WriteLine($"[spawnall] placing {names.Count} '{componentSubstring}' entities "
            + $"on a {columns}-wide grid, {spacing / 32} voxels apart");

        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];

            if (!Map.TryCreateEntity(name, out var entity))
            {
                failed.Add(name);

                continue;
            }

            var column = index % columns;
            var row = index / columns;

            if (TryGetTransform(entity, out var transform))
            {
                transform.Position = new Vector<int>(
                [
                    originX + column * spacing,
                    playerTransform.Position[1],
                    originZ + row * spacing,
                    0, 0, 0, 0, 0
                ]);

                transform.Size = Vector3.One;
            }

            ApplyPlacedDefaults(entity);

            Send(new EntityAdd
            {
                Id = entity.Id,
                NameHash = Util.ComputeCrc32(entity.Name),
                SyncData = entity.GetSyncData(newEntity: true)
            });

            var unsendable = entity.DescribeSync().Where(x => !x.Supported).ToList();

            Console.WriteLine($"[spawnall] r{row}c{column} {entity.Name} (id {entity.Id})"
                + (unsendable.Count > 0
                    ? $" — cannot send: {string.Join(", ", unsendable.Select(x => x.Parameter))}"
                    : string.Empty));

            spawned++;
        }

        return $"spawned {spawned} interactable(s) on a {columns}-wide grid"
            + (failed.Count > 0 ? $"; {failed.Count} could not be created: {string.Join(", ", failed)}" : string.Empty);
    }

    /// <summary>
    /// Close the container the player has open once they walk away from it.
    /// </summary>
    /// <remarks>
    /// The client sends <b>nothing</b> when the loot window is dismissed with Escape or the
    /// close button — the only packets around a close are <c>SetPlayerState</c> and
    /// <c>SetLookAtDirection</c>. So the server cannot be told; it has to notice. Without this
    /// the player's <c>usingentityid</c> stays pointing at the chest after the window is gone,
    /// the lid stays open, and the next E press is read as the close half of the toggle.
    ///
    /// Called from <see cref="Packets.EntityMoved"/> on every position update.
    /// </remarks>
    public void UpdateContainerRange()
    {
        if (!Player.TryGetComponent<ClientUseEntityComponent>(out var useEntity)
            || useEntity.UsingEntityID == 0)
            return;

        if (!Map.TryGetEntity(useEntity.UsingEntityID, out var target)
            || !TryGetTransform(target, out var targetTransform)
            || !Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform))
            return;

        var dx = playerTransform.Position[0] - targetTransform.Position[0];
        var dy = playerTransform.Position[1] - targetTransform.Position[1];
        var dz = playerTransform.Position[2] - targetTransform.Position[2];

        var distanceSquared = (long)dx * dx + (long)dy * dy + (long)dz * dz;

        if (distanceSquared <= (long)ContainerRange * ContainerRange)
            return;

        Console.WriteLine($"[interact] out of range of '{target.Name}' ({useEntity.UsingEntityID}) "
            + $"at {Math.Sqrt(distanceSquared):F0} units — closing");

        useEntity.UsingEntityID = 0;

        // Shuts the lid; see OpenInteractable for why this is the close signal.
        if (target.TryGetComponent<ClientInteractionComponent>(out var interaction))
            interaction.HasBeenOpened = true;
    }

    /// <summary>
    /// How far the player may stray before an open container closes itself. `/chest` places a
    /// chest <c>3 * 32</c> units ahead, so this is a few chest-lengths — deliberately generous,
    /// since the position units in this codebase are inconsistent (32 here, `VoxelUnits` = 64
    /// elsewhere) and closing the window while the player is still standing at the chest would
    /// be far worse than closing it a little late.
    /// </summary>
    private const int ContainerRange = 400;

    /// <summary>
    /// Resolve any entity's inventory by id — the player's own bag, or an open container's.
    /// </summary>
    /// <remarks>
    /// Inventory drags carry a source and a target entity id, which are equal for a move inside
    /// the rucksack and differ when moving to or from a chest. Both handlers used to bail out
    /// unless each id was the player, so container transfers did nothing; that guard was only
    /// ever reachable once chests could be opened at all.
    /// </remarks>
    public bool TryGetInventory(int entityId, [NotNullWhen(true)] out ClientInventoryComponent? inventory)
    {
        inventory = null;

        if (entityId == Player.Id)
            return Player.TryGetComponent(out inventory);

        return Map.TryGetEntity(entityId, out var entity) && entity.TryGetComponent(out inventory);
    }

    /// <summary>
    /// Move a stack between two <em>different</em> inventories (chest to bag, or back), swapping
    /// when the destination square is occupied. Same-inventory drags keep using the split/merge
    /// path in <see cref="Packets.InventoryItemTransferToSlot"/>.
    /// </summary>
    public bool TryTransferBetweenInventories(
        ClientInventoryComponent source, int sourceSlot,
        ClientInventoryComponent target, int targetSlot, int count = 0)
    {
        var sourceSlots = source.InventoryEntityList;
        var targetSlots = target.InventoryEntityList;

        if (sourceSlot < 0 || sourceSlot >= sourceSlots.Count ||
            targetSlot < 0 || targetSlot >= targetSlots.Count)
        {
            Console.WriteLine($"[inventory] cross-container slot out of range "
                + $"(source has {sourceSlots.Count}, target {targetSlots.Count})");

            return false;
        }

        if (sourceSlots[sourceSlot] == 0)
            return false;

        // Dropping a partial stack onto an empty square is a split, and dropping onto a
        // matching stack is a merge — exactly as inside one inventory. Without these two the
        // transfer moved the whole item entity regardless of the count the client asked for,
        // so dragging 5 of 10 into a chest silently moved all 10.
        if (TrySplitStack(sourceSlot, targetSlot, count, source, target))
            return true;

        if (TryMergeStack(sourceSlot, targetSlot, count, source, target))
            return true;

        (sourceSlots[sourceSlot], targetSlots[targetSlot]) = (targetSlots[targetSlot], sourceSlots[sourceSlot]);

        // Reassign both: mutating the list in place does not run the setter that marks the
        // parameter dirty, so neither entity would sync.
        source.InventoryEntityList = sourceSlots;
        target.InventoryEntityList = targetSlots;

        return true;
    }

    /// <summary>
    /// Move every stack from one inventory into the first free squares of another — the loot
    /// window's "Take All" button.
    /// </summary>
    /// <remarks>
    /// The client sends <c>InventoryItemTransferAll</c> (52) carrying just the two entity ids;
    /// it applies nothing locally and waits for the inventory sync, so an unhandled packet
    /// leaves the button looking dead.
    /// </remarks>
    public int TransferAll(ClientInventoryComponent source, ClientInventoryComponent target)
    {
        var moved = 0;

        for (var slot = 0; slot < source.InventoryEntityList.Count; slot++)
        {
            if (source.InventoryEntityList[slot] == 0)
                continue;

            // Prefer topping up a matching stack, then fall back to the first empty square.
            var merged = false;

            for (var candidate = 0; candidate < target.InventoryEntityList.Count && !merged; candidate++)
            {
                if (target.InventoryEntityList[candidate] == 0)
                    continue;

                merged = TryMergeStack(slot, candidate, 0, source, target);
            }

            if (merged)
            {
                moved++;

                continue;
            }

            var free = target.InventoryEntityList.IndexOf(0);

            // Into the rucksack proper, never the equipment or hotbar squares.
            if (ReferenceEquals(target, PlayerInventory) && free < FirstRucksackSlot)
                free = target.InventoryEntityList.FindIndex(FirstRucksackSlot, id => id == 0);

            if (free < 0)
                break;

            if (TryTransferBetweenInventories(source, slot, target, free))
                moved++;
        }

        return moved;
    }

    /// <summary>The player's own inventory, or null if they somehow have none.</summary>
    private ClientInventoryComponent? PlayerInventory =>
        Player.TryGetComponent<ClientInventoryComponent>(out var inventory) ? inventory : null;

    /// <summary>
    /// Answer an <c>InteractAction</c> by opening the target container.
    /// </summary>
    /// <remarks>
    /// There is no "open the chest" packet. The client opens a loot window when the
    /// <em>player's</em> <c>usingentityid</c> becomes the target's entity id — see
    /// <see cref="UseEntityComponent"/> for the decompiled chain. Setting it marks the
    /// parameter dirty and <c>Server.ProcessMaps</c> fans the <c>EntitySync</c> out.
    ///
    /// <c>hasbeenopened</c> must stay <b>false</b>. It is the close signal, not the open one:
    /// the client's open path <c>FUN_00778ac0</c> fires event <c>0x4E</c> only when
    /// <c>islootchest &amp;&amp; !hasbeenopened</c>, while <c>FUN_00778af0</c> fires the close event
    /// <c>0x4F</c> when it flips true. An earlier version of this method set it true on every
    /// interact, which was both the wrong signal and a permanent poison of the open path.
    /// </remarks>
    public void OpenInteractable(int entityId)
    {
        if (!Map.TryGetEntity(entityId, out var entity))
        {
            Console.WriteLine($"[interact] entity {entityId} is not on the map");

            return;
        }

        if (!entity.TryGetComponent<ClientInteractionComponent>(out var interaction))
        {
            Console.WriteLine($"[interact] '{entity.Name}' ({entityId}) has no ClientInteractionComponent");

            return;
        }

        if (!Player.TryGetComponent<ClientUseEntityComponent>(out var useEntity))
        {
            Console.WriteLine("[interact] the player has no ClientUseEntityComponent");

            return;
        }

        var slots = entity.TryGetComponent<ClientInventoryComponent>(out var inventory)
            ? $"{inventory.InventoryEntityList.Count(id => id != 0)}/{inventory.MaxInventorySlots} filled"
            : "no inventory component";

        // Toggle: pressing E on the chest we already have open closes it.
        var opening = useEntity.UsingEntityID != entityId;

        useEntity.UsingEntityID = opening ? entityId : 0;

        // The lid animation is driven by hasbeenopened, not by usingentityid. FUN_00778af0
        // fires the close event 0x4F only on the false -> true transition (while the client's
        // own "window open" latch is set), and the open path FUN_00778ac0 requires it to be
        // false. So it has to be raised to shut the lid and lowered again before the next open,
        // which is safe here because the two happen on separate key presses and therefore in
        // different sync ticks.
        interaction.HasBeenOpened = !opening;

        Console.WriteLine($"[interact] {(opening ? "opening" : "closing")} '{entity.Name}' ({entityId}): {slots}; "
            + $"enabled={interaction.Enabled} islootchest={interaction.IsLootChest} "
            + $"hasbeenopened={interaction.HasBeenOpened} -> player usingentityid={useEntity.UsingEntityID}");
    }

    /// <summary>
    /// Spawn a loot chest in front of the player (the /chest admin command). With no
    /// <paramref name="loot"/> the chest is empty. Also serves as the generic "spawn an
    /// interactable" path — pass <paramref name="entityName"/> to place an Anvil, a Mailbox or
    /// any other entity carrying <c>clientinteractioncomponent</c>. Must run on the game thread.
    /// </summary>
    public string SpawnChest(IReadOnlyList<(string Name, int Count)> loot, string entityName = "Chest")
    {
        if (!Map.TryCreateEntity(entityName, out var chest))
            return $"could not create the '{entityName}' entity";

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

        // The voxel link is what puts the chest *in* the world grid rather than floating in
        // front of it. Every one of the 50 entities that declares clientinteractioncomponent
        // also declares this, and until now we had no class for it, so `voxels` was never sent.
        // Entities.json gives the shape per entity: Chest is [[[0,0,0], 39]] - a single voxel at
        // the entity's own cell, index 39 being the voxel literally named `Entity`. Read it from
        // the entity data rather than hardcoding, so PVP_Post's 3-tall stack works too.
        // Pickup is the branch the HUD falls back to when the interact branch declines — which
        // for a Chest happens whenever the player is outside its 45-degree interaction cone,
        // i.e. standing behind it. Unsent, the client had nothing to describe the pickup with
        // and showed "Inventory Full". Values follow Entities.json: a populated loot chest is
        // not baggable, and only whoever placed it may take it.
        if (chest.TryGetComponent<ClientPickupComponent>(out var pickup))
        {
            pickup.InventoryItemEntity = 0;
            pickup.PlacedByUUID = Util.CharacterUuid();
            pickup.OnlyOwnerCanPickup = true;
            pickup.CanPickUpPopulatedInventories = false;
        }

        if (chest.TryGetComponent<ClientVoxelLinkComponent>(out var voxelLink))
        {
            voxelLink.Voxels = EntityManager.GetDefaultVoxelLinks(chest.Name);
            voxelLink.CanReplaceVoxelsOfEntityID = 0;

            Console.WriteLine($"[chest] voxel link: {string.Join(" ", voxelLink.Voxels.Select(v => $"({v.X},{v.Y},{v.Z})={v.VoxelIndex}"))}");
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
            // false = a proper two-way storage chest. TakeOnly is the authentic setting for a
            // one-shot loot chest, but it also stops the client letting you put anything back,
            // which makes the container useless for testing transfers in both directions.
            inventory.TakeOnly = false;

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

        var sync = chest.DescribeSync().ToList();

        var unsendable = sync.Where(x => !x.Supported).ToList();

        Console.WriteLine($"[chest] spawned '{chest.Name}' (id {chest.Id}) with {placed} item(s), "
            + $"{sync.Count} synced parameters");

        foreach (var (index, component, parameter, supported) in sync)
            Console.WriteLine($"[chest]   {index,2} {parameter,-32} {component,-32} {(supported ? "sent" : "NOT SENT - no server component")}");

        if (unsendable.Count > 0)
        {
            Console.WriteLine($"[chest] {unsendable.Count} parameter(s) cannot be sent: "
                + string.Join(", ", unsendable.Select(x => x.Parameter)));
        }

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
    /// <param name="inventory">
    /// Which inventory to operate on; null means the player's own bag. A chest passes its own,
    /// so rearranging and splitting stacks *inside* a container works the same way it does in
    /// the rucksack.
    /// </param>
    public bool TrySplitStack(int sourceSlot, int targetSlot, int count,
        ClientInventoryComponent? inventory = null, ClientInventoryComponent? targetInventory = null)
    {
        if (inventory is null && !Player.TryGetComponent(out inventory))
            return false;

        // Same inventory unless told otherwise, which makes a cross-container split — dragging
        // half a stack straight from the chest into the rucksack — the same operation.
        targetInventory ??= inventory;

        var slots = inventory.InventoryEntityList;
        var targetSlots = targetInventory.InventoryEntityList;

        if (sourceSlot < 0 || sourceSlot >= slots.Count || targetSlot < 0 || targetSlot >= targetSlots.Count)
            return false;

        // Only ever split into an empty square; merging into an occupied one is a different
        // operation and would need the stack limit checking.
        if (targetSlots[targetSlot] != 0 || slots[sourceSlot] == 0)
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

        targetSlots[targetSlot] = item.Id;

        // Reassign both; when the two are the same object this is simply done twice.
        targetInventory.InventoryEntityList = targetSlots;
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
    public string? Dig(int chunkX, int chunkY, int chunkZ, int voxelX, int voxelY, int voxelZ,
        int hitX = 0, int hitY = 0, int hitZ = 0)
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

        // Ore rolls a loot table and pops out as a floor pickup, the way seams do in the real
        // game. Everything else goes straight into the rucksack, matching what plain blocks do.
        if (LootTables.TableFor(material) is { } tableName)
        {
            var rolled = LootTables.Roll(tableName, _loot);

            if (rolled.Count == 0)
            {
                Console.WriteLine($"[dig] broke voxel ({world.X},{world.Y},{world.Z}) -> {tableName} rolled nothing");

                return null;
            }

            // Drop where the block was. The dig packet carries the exact point the tool
            // struck in entity units (1/64 of a voxel), so no conversion is needed — and it is
            // the only coordinate here that is guaranteed to agree with the client's own.
            var dropX = hitX;
            var dropY = hitY;
            var dropZ = hitZ;

            // Fall back to the block's centre if the packet had no hit point.
            if (dropX == 0 && dropY == 0 && dropZ == 0)
            {
                dropX = world.X * VoxelUnits + VoxelUnits / 2;
                dropY = world.Y * VoxelUnits + VoxelUnits / 2;
                dropZ = world.Z * VoxelUnits + VoxelUnits / 2;
            }

            foreach (var (name, count) in rolled)
            {
                Console.WriteLine($"[dig] broke voxel ({world.X},{world.Y},{world.Z}) -> {tableName} -> {name} x{count}");

                DropPickup(name, count, dropX, dropY, dropZ);
            }

            return rolled[0].Name;
        }

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

    /// <summary>
    /// Push a voxel change to this client. The admin editor records the edit on the world
    /// itself and calls this so the running game reflects it, exactly as digging and placing do.
    /// </summary>
    public void SendWorldVoxel(int worldX, int worldY, int worldZ, byte material)
    {
        const int chunkSize = TerrainGenerator.ChunkSize;

        // Floor division so negative coordinates land in the right chunk.
        var chunkX = (int)Math.Floor(worldX / (double)chunkSize);
        var chunkY = (int)Math.Floor(worldY / (double)chunkSize);
        var chunkZ = (int)Math.Floor(worldZ / (double)chunkSize);

        SendVoxelEdit(chunkX, chunkY, chunkZ,
            worldX - chunkX * chunkSize, worldY - chunkY * chunkSize, worldZ - chunkZ * chunkSize,
            material);
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

    /// <summary>
    /// Default stack size for items that do not override it. Only 14 resources carry an
    /// override (99 for several tools, 1 for flags and timed portals, 10 for the Mining Pick);
    /// everything else, blocks included, uses this. 64 matches the point at which
    /// <see cref="Packets.Common.InventorySlotData"/> switches to its wide count encoding.
    /// </summary>
    /// <summary>Rolls for loot tables. One per connection keeps digs reproducible-ish.</summary>
    private readonly Random _loot = new();

    /// <summary>
    /// Entity position units per voxel. Confirmed by the dig packet, whose position field
    /// is documented as <c>/ 64</c> — an earlier value of 32 put drops at half scale,
    /// underground and invisible.
    /// </summary>
    private const int VoxelUnits = 64;

    private const int DefaultStackLimit = 64;

    private static int StackLimit(ResourceData resource)
        => resource.IsOverridingStackLimit && resource.StackLimitOverride > 0
            ? resource.StackLimitOverride
            : DefaultStackLimit;

    /// <summary>
    /// Drop a stack onto another of the same item: move as much as the target can hold, up to
    /// the stack limit. Returns false when this is not a merge (different items, empty target,
    /// no room), leaving the caller to swap instead.
    /// </summary>
    /// <param name="inventory">
    /// Which inventory to operate on; null means the player's own bag. See
    /// <see cref="TrySplitStack"/>.
    /// </param>
    public bool TryMergeStack(int sourceSlot, int targetSlot, int count,
        ClientInventoryComponent? inventory = null, ClientInventoryComponent? targetInventory = null)
    {
        if (inventory is null && !Player.TryGetComponent(out inventory))
            return false;

        // Same inventory unless told otherwise; passing a second one merges a rucksack stack
        // onto a matching stack already in the chest, and vice versa.
        targetInventory ??= inventory;

        var slots = inventory.InventoryEntityList;
        var targetSlots = targetInventory.InventoryEntityList;

        // A slot can only collide with itself within one inventory.
        if ((sourceSlot == targetSlot && ReferenceEquals(inventory, targetInventory)) ||
            sourceSlot < 0 || sourceSlot >= slots.Count ||
            targetSlot < 0 || targetSlot >= targetSlots.Count ||
            slots[sourceSlot] == 0 || targetSlots[targetSlot] == 0)
            return false;

        if (!Map.TryGetEntity(slots[sourceSlot], out var source) ||
            !source.TryGetComponent<InventoryItemComponent>(out var sourceItem) ||
            !Map.TryGetEntity(targetSlots[targetSlot], out var target) ||
            !target.TryGetComponent<InventoryItemComponent>(out var targetItem))
            return false;

        var sourceData = sourceItem.InventorySlotData;
        var targetData = targetItem.InventorySlotData;

        // Only stacks of the same item merge.
        if (sourceData.Name is not { } nameHash || targetData.Name != nameHash)
            return false;

        if (!GeoDataManager.TryGetResource(nameHash, out var resource))
            return false;

        var space = StackLimit(resource) - targetData.Count;

        if (space <= 0)
            return false;

        // A drag of part of a stack asks for that many; a whole-stack drag asks for all of it.
        var wanted = count > 0 ? Math.Min(count, sourceData.Count) : sourceData.Count;

        var moved = Math.Min(wanted, space);

        if (moved <= 0)
            return false;

        targetData.Count += moved;

        // Reassign so the setters raise the change and both stacks sync.
        targetItem.InventorySlotData = targetData;

        sourceData.Count -= moved;

        if (sourceData.Count > 0)
        {
            sourceItem.InventorySlotData = sourceData;
        }
        else
        {
            var entityId = slots[sourceSlot];

            slots[sourceSlot] = 0;

            inventory.InventoryEntityList = slots;

            Map.RemoveEntity(source);

            Send(new EntityRemoved { Id = entityId });

            ItemNames.Remove(entityId);
        }

        Console.WriteLine($"[inventory] merged {moved} {resource.Name} from slot {sourceSlot} "
            + $"into slot {targetSlot} (now {targetData.Count}, {sourceData.Count} left behind)");

        return true;
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
    #region Mail

    /// <summary>
    /// Answer <c>MailCheck</c>: push the inbox and then tell the panel it is loaded.
    /// </summary>
    /// <remarks>
    /// Both halves are required and the order is load-bearing. Marking <c>mailitemlist</c>
    /// changed makes the next tick's <c>EntitySync</c> carry it; <c>RemoteMailSynced</c> is what
    /// flips the panel out of its <c>mailLoading</c> state — without it the inbox spins forever
    /// no matter how correct the list is. The channel is RELIABLE_ORDERED, so sending the
    /// notification after marking the parameter dirty is enough to guarantee the client sees the
    /// sync first.
    /// </remarks>
    public void SyncMailbox()
    {
        if (!Player.TryGetComponent<ClientMailBoxComponent>(out var mailbox))
        {
            Console.WriteLine("[mail] the player has no ClientMailBoxComponent");

            return;
        }

        // Deliberately NOT re-announcing the attachment containers here.
        //
        // A repeat EntityAdd for an id the client already holds makes it destroy the entity and
        // build a fresh one, which leaves every slot list that still names the old object
        // holding a dangling pointer. That is precisely the pointer the contents recompute
        // dereferences (FUN_007eaff0 / FUN_0088b690: `*(entity + 0xb0)` into the component-table
        // binary search FUN_008661a0), and a stale pointer is the ONLY shape that faults there -
        // a slot whose entity is simply absent reads back null and is skipped.
        //
        // Announce once, at compose time, and let later changes ride EntitySync, which is the
        // path /give already proves. The channel is RELIABLE_ORDERED, so the compose-time
        // EntityAdd is always in front of the list that references it.
        mailbox.MarkChanged();

        Send(new RemoteMailSynced());

        Console.WriteLine($"[mail] synced {mailbox.MailItemList.Count} message(s): "
            + string.Join("; ", mailbox.MailItemList.Select(mail =>
            {
                var attachments = Map.TryGetEntity(mail.AttachmentEntity, out var container)
                    && container.TryGetComponent<ClientInventoryComponent>(out var inventory)
                        ? string.Join(",", inventory.InventoryEntityList)
                        : "entity NOT on the map";

                return $"'{mail.Subject}' flags={mail.Flags} attachmentEntity={mail.AttachmentEntity} [{attachments}]";
            })));
    }

    /// <summary>
    /// Put a message in the player's own inbox — the /mail admin command. Attachments become
    /// real item entities inside a container entity, exactly as a chest holds loot.
    /// Must run on the game thread.
    /// </summary>
    /// <remarks>
    /// <paramref name="containerEntity"/> is a diagnostic lever, not a feature: <c>MailItem</c>
    /// is the client's own attachment container (5 slots, take-only) and is the right answer,
    /// but it is also the one entity in this flow the client has never been proven to
    /// instantiate. Passing a known-good container (<c>Chest</c>) is how we tell "the client
    /// rejects MailItem" apart from "the ids never reached the client".
    /// </remarks>
    public string ComposeMail(string subject, string body, IReadOnlyList<(string Name, int Count)> attachments,
        string containerEntity = "MailItem")
    {
        if (!Player.TryGetComponent<ClientMailBoxComponent>(out var mailbox))
            return "the player has no mailbox component";

        var mail = new MailItem
        {
            Subject = subject,
            Body = body,
            MessageUuid = Util.NewGuid(),
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var placed = 0;

        // "@self" is the control experiment, not a feature: it points the mail at the PLAYER's
        // entity, which the client demonstrably has (it renders the rucksack from it) and can
        // resolve by id. If the attachment slots then show rucksack items, the mail attachment
        // path is sound and the fault is in how we announce the container entity; if they stay
        // empty, the fault is in the attachment path itself.
        if (containerEntity.Equals("self", StringComparison.OrdinalIgnoreCase))
        {
            mail.AttachmentEntity = Player.Id;

            Console.WriteLine($"[mail] control: pointing '{subject}' at the player entity {Player.Id}");
        }
        else if (attachments.Count > 0 && TryCreateAttachmentContainer(containerEntity, attachments, out var container, out placed))
        {
            mail.AttachmentEntity = container.Id;
        }

        mailbox.MailItemList.Add(mail);

        // The doorbell. The client answers it with MailCheck, which is what actually pulls the
        // list — see SyncMailbox. Sending the sync here as well would be harmless but pointless.
        Send(new NewMailRecieved { MessageUuid = mail.MessageUuid });

        Console.WriteLine($"[mail] composed '{subject}' ({mail.MessageUuid}) with {placed} attachment(s)");

        return $"sent '{subject}' with {placed} attachment(s)";
    }

    /// <summary>
    /// Build the <c>MailItem</c> entity that holds a message's attachments.
    /// </summary>
    /// <remarks>
    /// <c>MailItem</c> is the client's own container for this: a bare
    /// <c>clientinventorycomponent</c> with <c>maxinventoryslots = 5</c> and
    /// <c>takeonly = true</c>, and nothing else. Every item entity must reach the client before
    /// the container's inventory references its id, and the container before
    /// <c>mailitemlist</c> references <em>its</em> id — the same ordering rule as every other
    /// entity-referencing parameter.
    /// </remarks>
    private bool TryCreateAttachmentContainer(string containerEntity,
        IReadOnlyList<(string Name, int Count)> attachments,
        [NotNullWhen(true)] out Entity? container, out int placed)
    {
        placed = 0;

        if (!Map.TryCreateEntity(containerEntity, out container))
        {
            Console.WriteLine($"[mail] could not create the '{containerEntity}' entity");

            return false;
        }

        // Put the container where the player is, if it has a transform at all.
        //
        // Left at the default it sits at (0,0,0) - outside every loaded chunk - and the client
        // destroys it while `mailitemlist` still names its id. The slot walk in FUN_007eaff0 /
        // FUN_0088b690 then reads `entity + 0xb0` through the dead pointer and faults; a slot
        // whose entity is merely *absent* is skipped safely, so a destroyed-but-referenced
        // entity is the only shape that crashes. That is the access violation in
        // logs/client-internal-crash*.log.
        if (Player.TryGetComponent<SmoothedTransformComponent>(out var playerTransform) &&
            TryGetTransform(container, out var containerTransform))
        {
            containerTransform.Position = playerTransform.Position;
            containerTransform.Size = Vector3.One;
        }

        if (!container.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return true;

        // MailItem declares five, and the client draws presentSlot_00..04 to match. A
        // substituted container keeps its own count so the A/B stays honest.
        var slots = containerEntity == "MailItem" ? 5 : 25;

        // The list is nine entries LONGER than the slot count, and the attachments start at
        // index nine - the same equipment prefix the player's rucksack has (FirstRucksackSlot).
        //
        // The mail panel is explicit about this. FUN_00794950, which fills presentSlot_%02d,
        // walks the container's InventoryComponent from `*(inv + 0x38) + 0x120` - 0x120 / 0x20 =
        // nine slots in - to `*(inv + 0x3c)`. FUN_00794470 then derives the attachment count as
        // `maxinventoryslots - <empty slots from index nine on>` (FUN_00889fc0 counts those), so
        // the arithmetic only yields the real number of attachments when the array runs nine
        // entries past `maxinventoryslots`.
        //
        // Filling slots 0..4 of a five-entry list, as this used to, is invisible twice over: the
        // UI skips every slot it wrote, and `begin + 0x120` lands past `end`, so that walk runs
        // off the end of the array instead of terminating.
        var contents = new List<int>(new int[FirstRucksackSlot + slots]);

        inventory.MaxInventorySlots = (byte)slots;
        inventory.TakeOnly = true;

        foreach (var (name, count) in attachments)
        {
            if (placed >= slots)
            {
                Console.WriteLine($"[mail] '{name}' dropped: a MailItem holds only {slots} attachments");

                continue;
            }

            if (!GeoDataManager.TryGetResource(name, out var resource) || !resource.IsInventoryItem)
            {
                Console.WriteLine($"[mail] unknown item '{name}'; skipped");

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

            Send(new EntityAdd
            {
                Id = item.Id,
                NameHash = Util.ComputeCrc32(item.Name),
                SyncData = item.GetSyncData(newEntity: true)
            });

            contents[FirstRucksackSlot + placed++] = item.Id;

            ItemNames[item.Id] = resource.Name;
        }

        inventory.InventoryEntityList = contents;

        Send(new EntityAdd
        {
            Id = container.Id,
            NameHash = Util.ComputeCrc32(container.Name),
            SyncData = container.GetSyncData(newEntity: true)
        });

        // NOT re-assigning the list here to force a follow-up EntitySync.
        //
        // That was tried, on the theory that the EntityAdd's `inventoryentitylist` is received
        // and not applied. It puts an `EntitySync (31 B)` for the container on the wire right
        // after its EntityAdd - a packet the one run in which an attachment ever rendered
        // (logs/game-mailrun1.log) never sent. SyncMailbox re-announces the container in front
        // of every list that names it, so the contents travel again there, on the EntityAdd
        // path, without a mid-construction parameter change landing on the entity.

        // The attachment slots came up empty on the first live test even though the message
        // itself rendered, so say exactly what was sent: the container id the mail points at,
        // what is in it, and any parameter of it we cannot serialise.
        var unsendable = container.DescribeSync().Where(x => !x.Supported).ToList();

        Console.WriteLine($"[mail] attachment container {container.Name} (id {container.Id}) "
            + $"slots {string.Join(",", contents)} "
            + $"maxslots={inventory.MaxInventorySlots} takeonly={inventory.TakeOnly}"
            + (unsendable.Count > 0
                ? $" — cannot send: {string.Join(", ", unsendable.Select(x => x.Parameter))}"
                : string.Empty));

        return true;
    }

    /// <summary>
    /// The player opened a message. The client has already set its own read bit, so this only
    /// has to make the server agree — a re-sync with the bit clear would mark it unread again.
    /// </summary>
    public void MarkMailRead(string messageUuid)
    {
        if (!TryGetMail(messageUuid, out var mailbox, out var mail))
            return;

        mail.IsRead = true;

        mailbox.MarkChanged();

        Console.WriteLine($"[mail] read '{mail.Subject}' ({messageUuid})");
    }

    /// <summary>
    /// The player picked a gift. Which one is not on the wire (see
    /// <see cref="Packets.MailGiftSelected"/>) — all this records is that the choice happened,
    /// which is what stops the client offering the buttons again.
    /// </summary>
    public void MarkMailGiftChosen(string messageUuid)
    {
        if (!TryGetMail(messageUuid, out var mailbox, out var mail))
            return;

        mail.GiftChosen = true;

        mailbox.MarkChanged();

        Console.WriteLine($"[mail] gift chosen on '{mail.Subject}' ({messageUuid})");
    }

    /// <summary>
    /// Move one attachment from a message into the rucksack.
    /// </summary>
    /// <remarks>
    /// The client identifies the item by its uuid, not by slot, and has already blanked its own
    /// copy of the slot — so if the server does nothing the item is simply gone from view until
    /// the next re-bind restores it. Both inventories and the mail list have to be re-synced.
    /// </remarks>
    public void ClaimMailAttachment(string messageUuid, string itemUuid)
    {
        if (!TryGetMail(messageUuid, out var mailbox, out var mail))
            return;

        if (!Map.TryGetEntity(mail.AttachmentEntity, out var container) ||
            !container.TryGetComponent<ClientInventoryComponent>(out var attachments))
        {
            Console.WriteLine($"[mail] '{mail.Subject}' has no attachment container");

            return;
        }

        if (!Player.TryGetComponent<ClientInventoryComponent>(out var inventory))
            return;

        var attachmentSlots = attachments.InventoryEntityList;

        var sourceSlot = -1;

        for (var slot = 0; slot < attachmentSlots.Count; slot++)
        {
            if (attachmentSlots[slot] != 0 &&
                Map.TryGetEntity(attachmentSlots[slot], out var candidate) &&
                candidate.TryGetComponent<InventoryItemComponent>(out var item) &&
                item.InventorySlotData.ItemUUID == itemUuid)
            {
                sourceSlot = slot;
                break;
            }
        }

        if (sourceSlot < 0)
        {
            Console.WriteLine($"[mail] item {itemUuid} is not attached to '{mail.Subject}'");

            return;
        }

        var slots = inventory.InventoryEntityList;

        var targetSlot = -1;

        for (var slot = FirstRucksackSlot; slot < slots.Count; slot++)
        {
            if (slots[slot] == 0)
            {
                targetSlot = slot;
                break;
            }
        }

        if (targetSlot < 0)
        {
            Console.WriteLine("[mail] rucksack is full; attachment left in the message");

            // Re-sync anyway: the client blanked its slot optimistically and needs it back.
            mailbox.MarkChanged();

            return;
        }

        // The same move a chest transfer makes, so stacking, counts and both syncs behave
        // identically to dragging the item out of a container by hand.
        TryTransferBetweenInventories(attachments, sourceSlot, inventory, targetSlot);

        mailbox.MarkChanged();

        Console.WriteLine($"[mail] claimed attachment {itemUuid} from '{mail.Subject}' -> rucksack slot {targetSlot}");
    }

    /// <summary>
    /// Discard a message and everything still attached to it. The client does not remove the row
    /// itself — it disappears when the list re-syncs without it.
    /// </summary>
    public void DeleteMailMessage(string messageUuid)
    {
        if (!TryGetMail(messageUuid, out var mailbox, out var mail))
            return;

        if (Map.TryGetEntity(mail.AttachmentEntity, out var container))
        {
            if (container.TryGetComponent<ClientInventoryComponent>(out var attachments))
            {
                foreach (var entityId in attachments.InventoryEntityList)
                {
                    if (entityId == 0 || !Map.TryGetEntity(entityId, out var item))
                        continue;

                    Map.RemoveEntity(item);

                    Send(new EntityRemoved { Id = entityId });

                    ItemNames.Remove(entityId);
                }
            }

            Map.RemoveEntity(container);

            Send(new EntityRemoved { Id = container.Id });
        }

        mailbox.MailItemList.Remove(mail);

        mailbox.MarkChanged();

        Console.WriteLine($"[mail] deleted '{mail.Subject}' ({messageUuid})");
    }

    private bool TryGetMail(string messageUuid,
        [NotNullWhen(true)] out ClientMailBoxComponent? mailbox, [NotNullWhen(true)] out MailItem? mail)
    {
        mail = null;

        if (!Player.TryGetComponent(out mailbox))
        {
            Console.WriteLine("[mail] the player has no ClientMailBoxComponent");

            return false;
        }

        mail = mailbox.MailItemList.Find(x => x.MessageUuid == messageUuid);

        if (mail is null)
            Console.WriteLine($"[mail] no message {messageUuid}");

        return mail is not null;
    }

    #endregion

}