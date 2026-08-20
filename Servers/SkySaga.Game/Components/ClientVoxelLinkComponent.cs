namespace SkySaga.Game.Components;

/// <summary>
/// The client-side half of <see cref="VoxelLinkComponent"/>. Empty in the client too — every
/// method lives on the base class; this exists so <c>EntityManager</c>'s name lookup resolves
/// the <c>clientvoxellinkcomponent</c> that Entities.json declares.
/// </summary>
public class ClientVoxelLinkComponent : VoxelLinkComponent
{
}
