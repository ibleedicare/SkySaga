using System;

using RakNet;

namespace SkySaga.Game.Components;

/// <summary>
/// The entity the player is currently "using" — a chest they have open, a campfire they are
/// cooking at. Carried by <c>Player</c> (syncindex 83), <c>TestPlayer</c> and
/// <c>ArtTestPlayer</c> (84); 0 means "using nothing".
/// </summary>
/// <remarks>
/// <b>This is how a container is opened.</b> There is no dedicated "open the chest" packet:
/// the server sets this parameter on the <em>player</em> entity to the target's id and lets the
/// ordinary <c>EntitySync</c> fan-out carry it, and sets it back to 0 to close.
///
/// Verified in the client: the parameter map <c>FUN_00809640</c> gives <c>UsingEntityID</c> as
/// the component's only parameter (local index 0), encoded as a plain 32-bit int
/// (<c>FUN_0090e9a0</c> / <c>FUN_0090e9f0</c>). Its <c>OnSyncedParametersChanged</c>,
/// <c>FUN_0084c3d0</c>, branches on the new value: non-zero calls <c>FUN_0084c360</c>, which
/// resolves the target, appends the player to the target InteractionComponent's user list
/// (<c>FUN_0087a570</c>) and fires entity event <c>0x34</c> on the player and <c>0x0C</c> on the
/// target; zero calls <c>FUN_0084c2a0</c>, the mirror, firing <c>0x35</c> / <c>0x0D</c>.
///
/// Entering that state is also what makes the client send <c>InteractWithEntity</c> (msgId 154)
/// — <c>FUN_007f6e10</c> is the state's OnEnter and reaches the RPC writer <c>FUN_00778e10</c>.
/// So that packet is a <em>consequence</em> of the container opening, not its cause, which is
/// why our handler for it had never once fired.
/// </remarks>
public class UseEntityComponent : Component
{
    public int UsingEntityID { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(UsingEntityID), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.Write(UsingEntityID);

            return true;
        }

        return false;
    }
}
