using System;

using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Components;

/// <summary>
/// Carries the character UUID that owns an entity. Declared for the Player in
/// Entities.json as <c>clientownercomponent</c> with a single <c>owner</c> binding.
/// </summary>
/// <remarks>
/// The client resolves this component on its own player entity (FUN_006ff460, which looks
/// up "OwnerComponent") and uses the value as the id in its social-graph requests:
/// <c>/api/social-graph/character/%s/friends/info</c>, <c>/api/social-graph/friendrequest/%s</c>.
/// Without it the component is absent, the id is empty, and those URLs go out with an empty
/// path segment — which is why the friends list stays empty.
/// </remarks>
public class ClientOwnerComponent : Component
{
    public string? Owner { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(Owner), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.WriteString(Owner);

            return true;
        }

        return false;
    }
}
