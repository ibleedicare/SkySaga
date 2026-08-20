namespace SkySaga.Game.Components;

/// <summary>
/// The client-side half of <see cref="UseEntityComponent"/>; exists so EntityManager's name
/// lookup resolves the <c>clientuseentitycomponent</c> that Entities.json declares on Player.
/// </summary>
public class ClientUseEntityComponent : UseEntityComponent
{
}
