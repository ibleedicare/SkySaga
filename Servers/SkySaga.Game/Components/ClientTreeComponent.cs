using System;

using RakNet;

namespace SkySaga.Game.Components;

/// <summary>
/// A harvestable resource node. Despite the name this is not only wood — the engine's "tree" is
/// the generic gatherable, and later builds use it for ore (<c>TreeMetalOre</c>,
/// <c>TreeKeystoneOre</c>), which is why it carries loot tables at all.
/// </summary>
/// <remarks>
/// Matches the <c>clienttreecomponent</c> the Tree entity declares in build 10414: bindings
/// decorationloottable (0), description (1), partsdestroyed (2), treetype (5) and
/// trunkloottable (6).
///
/// Only the loot tables are synced. <see cref="TreeType"/> picks the model and its valid values
/// are unknown for this build, so it stays unsynced unless explicitly set — a wrong enum value
/// is exactly the kind of thing that makes the client choke on a full sync (see
/// <see cref="TransformComponent"/>'s yawdegrees note). Loot tables go on the wire as the CRC-32
/// of their name, the same hashing every other name reference uses.
/// </remarks>
public class ClientTreeComponent : Component
{
    /// <summary>CRC-32 of the loot table the trunk yields — the main harvest.</summary>
    public uint? TrunkLootTable { get; set { field = value; OnParameterChanged(); } }

    /// <summary>CRC-32 of the loot table the leaves/decoration yield.</summary>
    public uint? DecorationLootTable { get; set { field = value; OnParameterChanged(); } }

    /// <summary>Selects the model. Left unsynced unless set, since the enum is unverified.</summary>
    public int? TreeType { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(TrunkLootTable), StringComparison.OrdinalIgnoreCase))
        {
            if (TrunkLootTable is not { } trunk)
                return false;

            bitStream.Write((int)trunk);

            return true;
        }
        else if (parameterName.Equals(nameof(DecorationLootTable), StringComparison.OrdinalIgnoreCase))
        {
            if (DecorationLootTable is not { } decoration)
                return false;

            bitStream.Write((int)decoration);

            return true;
        }
        else if (parameterName.Equals(nameof(TreeType), StringComparison.OrdinalIgnoreCase))
        {
            if (TreeType is not { } treeType)
                return false;

            bitStream.Write(treeType);

            return true;
        }

        return false;
    }
}
