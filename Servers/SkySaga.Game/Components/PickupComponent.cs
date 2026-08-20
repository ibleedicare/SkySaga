using System;

using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Components;

/// <summary>
/// Whether an entity can be picked up and put in a bag, and which inventory item it becomes
/// when it is. Carried by 210 entities — every placeable — including <c>Chest</c>.
/// </summary>
/// <remarks>
/// Wire format read off the client's dispatchers <c>FUN_0087f400</c> (deserialise) and
/// <c>FUN_0087f350</c> (serialise); parameter map <c>FUN_00807cc0</c> gives the component-local
/// indices <c>InventoryItemEntity</c> 0, <c>PlacedByUUID</c> 1, <c>OnlyOwnerCanPickup</c> 2,
/// <c>CanPickUpPopulatedInventories</c> 3:
/// <code>
///   inventoryitementity           : 32 bits, raw entity id
///   placedbyuuid                  : standard string (as ClientOwnerComponent.Owner)
///   onlyownercanpickup            : 1 bit
///   canpickuppopulatedinventories : 1 bit
/// </code>
/// Why this matters beyond pickup itself: the HUD reticule (<c>FUN_007fe280</c>) falls through
/// to the pickup branch whenever the interact branch declines — most visibly when the player is
/// outside a chest's <c>interactionanglesradians</c> cone, i.e. standing behind it. With these
/// parameters unsent the client had nothing to describe the pickup with and displayed
/// "Inventory Full".
///
/// The client's own constructor (<c>FUN_0087ed70</c>) zeroes both bools, so an unsent
/// <c>onlyownercanpickup</c> lands as false regardless of the Entities.json default of true.
/// </remarks>
public class PickupComponent : Component
{
    /// <summary>The inventory-item entity this becomes when picked up; 0 for none.</summary>
    public int InventoryItemEntity { get; set { field = value; OnParameterChanged(); } }

    /// <summary>
    /// Character UUID of whoever placed it, matched against the picker when
    /// <see cref="OnlyOwnerCanPickup"/> is set.
    /// </summary>
    public string? PlacedByUUID { get; set { field = value; OnParameterChanged(); } }

    public bool OnlyOwnerCanPickup { get; set { field = value; OnParameterChanged(); } }

    /// <summary>
    /// Entities.json defaults this to false for a Chest: a chest with items in it cannot be
    /// bagged. That is the authentic behaviour, so leave it false for loot chests.
    /// </summary>
    public bool CanPickUpPopulatedInventories { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(InventoryItemEntity), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(InventoryItemEntity);

            return true;
        }
        else if (parameterName.Equals(nameof(PlacedByUUID), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.WriteString(PlacedByUUID);

            return true;
        }
        else if (parameterName.Equals(nameof(OnlyOwnerCanPickup), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(OnlyOwnerCanPickup);

            return true;
        }
        else if (parameterName.Equals(nameof(CanPickUpPopulatedInventories), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(CanPickUpPopulatedInventories);

            return true;
        }

        return false;
    }
}
