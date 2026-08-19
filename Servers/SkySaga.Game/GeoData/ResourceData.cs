using System;
using System.Collections.Generic;

namespace SkySaga.Game.GeoData;

/// <summary>
/// One entry of <c>GeoData.json > Resources</c> — the client's item table.
/// </summary>
/// <remarks>
/// Items are not entities. <c>Entities.json</c> only describes the container
/// (<c>BasicInventoryItem</c> and its <c>inventoryitemcomponent</c>); what the item
/// actually *is* lives here, and the two are joined on the wire by
/// <see cref="NameHash"/> in <c>InventorySlotData.Name</c>.
///
/// Client 10414 ships 365 of these. Only the fields the server has a use for are
/// modelled; the rest of each entry (material categories, lifetimes, override flags)
/// stays in the JSON.
/// </remarks>
public class ResourceData
{
    /// <summary>Unique name, e.g. <c>ExplorerArmourHead</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>CRC32(Name)</c> — how the client refers to this item. Verified collision-free
    /// across all 365 resources for 10414.
    /// </summary>
    public required uint NameHash { get; init; }

    public bool IsInventoryItem { get; init; }
    public bool IsEquippableItem { get; init; }
    public bool IsStorable { get; init; }
    public bool IsDiscardable { get; init; }
    public bool IsDestroyable { get; init; }
    public bool IsTwoHanded { get; init; }
    public bool IsUsableByPlayer { get; init; }

    /// <summary>Equipment group: <c>Head</c>, <c>Torso</c>, <c>Arms</c>, <c>Legs</c>, <c>Edged Weapons</c>, … Empty for 205 of the 365.</summary>
    public string SubCategory { get; init; } = string.Empty;

    /// <summary><c>Common</c> … <c>Ultimate</c>.</summary>
    public string RarityLevel { get; init; } = string.Empty;

    /// <summary>Key into <c>GeoData.json > StatTemplates</c>, e.g. <c>Armour</c>.</summary>
    public string StatTemplateName { get; init; } = string.Empty;

    public string RequiredJob { get; init; } = string.Empty;
    public int RequiredJobRank { get; init; }

    public int ObjectType { get; init; }
    public int InGameCurrency { get; init; }

    /// <summary>Only meaningful when <see cref="IsOverridingStackLimit"/> is set.</summary>
    public int StackLimitOverride { get; init; }
    public bool IsOverridingStackLimit { get; init; }

    /// <summary>
    /// Armour protection, 0-4, present for the 38 resources that carry a
    /// <c>Clothing</c> block. Null for everything else.
    /// </summary>
    public int? ClothingProtection { get; init; }

    /// <summary>
    /// Names of the behaviour blocks this resource carries — <c>Attack</c>,
    /// <c>Clothing</c>, <c>Dig</c>, <c>Eat</c>, <c>CreateEntity</c>, … A resource has
    /// 0, 1 or 2 of them.
    /// </summary>
    public IReadOnlySet<string> Behaviours { get; init; } = new HashSet<string>();

    public bool IsArmour => ClothingProtection.HasValue;

    public override string ToString() => Name;
}
