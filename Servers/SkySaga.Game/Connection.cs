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
            PacketId.RequestEquipInventoryItem => RequestEquipInventoryItem.Handle(this, bitStream),
            PacketId.NotifyPhotoCaptured => NotifyPhotoCaptured.Handle(this, bitStream),
            PacketId.ExecuteEntityAction => ExecuteEntityAction.Handle(this, bitStream),
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

                if (data is not null)
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
    public string SpawnEntity(string name)
    {
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

            placed = $"at ({transform.Position[0]}, {transform.Position[1]}, {transform.Position[2]}) yaw {FacingYawDegrees:0}";
        }

        Send(new EntityAdd
        {
            Id = entity.Id,
            NameHash = Util.ComputeCrc32(entity.Name),
            SyncData = entity.GetSyncData(newEntity: true)
        });

        Console.WriteLine($"[spawn] {entity.Name} (id {entity.Id}) {placed}");

        return $"spawned {entity.Name} (id {entity.Id}) {placed}";
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