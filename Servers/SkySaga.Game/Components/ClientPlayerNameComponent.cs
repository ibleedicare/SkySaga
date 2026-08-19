using System;

using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Components;

/// <summary>
/// The player's display name, synced to the client so UI that shows it (name plate,
/// photo album header, etc.) has a value instead of the "PLAYERNAME" placeholder.
/// </summary>
/// <remarks>
/// The class name matches the <c>clientplayernamecomponent</c> the Player entity declares
/// in entities.json, so EntityManager attaches it by reflection. Its one synced binding
/// is <c>playername</c> (syncindex 65), a string.
/// </remarks>
public class ClientPlayerNameComponent : Component
{
    public string PlayerName { get; set { field = value; OnParameterChanged(); } } = string.Empty;

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(PlayerName), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.WriteString(PlayerName);

            return true;
        }

        return false;
    }
}
