namespace SkySaga.Game.Components;

/// <summary>
/// The client-side half of <see cref="PickupComponent"/>; exists so EntityManager's name lookup
/// resolves the <c>clientpickupcomponent</c> that Entities.json declares.
/// </summary>
public class ClientPickupComponent : PickupComponent
{
}
