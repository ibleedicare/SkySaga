using System;

using RakNet;

namespace SkySaga.Game.Components;

/// <summary>
/// Marks an entity as interactable and, for chests, whether it's a loot chest and who may
/// open it. The client shows the interact prompt when <see cref="Enabled"/> and opens the
/// entity's inventory UI on interact; the contents come from its
/// <see cref="ClientInventoryComponent"/>.
/// </summary>
/// <remarks>
/// Matches the <c>clientinteractioncomponent</c> the Chest entity declares. Synced bindings
/// (Entities.json): enabled (3), islootchest (8), owneronly (12), hasbeenopened (4),
/// allowmultipleusers (0). interactionanglesradians (5) is left to the client default. Bools
/// are 1-bit writes, matching the working PlayerAspectsComponent.
/// </remarks>
public class ClientInteractionComponent : Component
{
    public bool Enabled { get; set { field = value; OnParameterChanged(); } } = true;
    public bool IsLootChest { get; set { field = value; OnParameterChanged(); } }
    public bool HasBeenOpened { get; set { field = value; OnParameterChanged(); } }
    public bool OwnerOnly { get; set { field = value; OnParameterChanged(); } }
    public bool AllowMultipleUsers { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(Enabled), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(Enabled);
            return true;
        }
        else if (parameterName.Equals(nameof(IsLootChest), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(IsLootChest);
            return true;
        }
        else if (parameterName.Equals(nameof(HasBeenOpened), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(HasBeenOpened);
            return true;
        }
        else if (parameterName.Equals(nameof(OwnerOnly), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(OwnerOnly);
            return true;
        }
        else if (parameterName.Equals(nameof(AllowMultipleUsers), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(AllowMultipleUsers);
            return true;
        }

        return false;
    }
}
