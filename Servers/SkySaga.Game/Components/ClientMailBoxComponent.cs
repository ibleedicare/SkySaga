using RakNet;

namespace SkySaga.Game.Components;

/// <summary>
/// <c>clientmailboxcomponent</c> — the name <c>Player</c> binds <c>mailitemlist</c> to.
/// </summary>
/// <remarks>
/// Component classes are resolved by lowercased type name (<c>EntityManager</c>), so this thin
/// subclass is what makes the base component reachable from Entities.json at all — the same
/// pairing as <c>ClientInventoryComponent</c> / <c>InventoryComponent</c>.
/// </remarks>
public class ClientMailBoxComponent : MailBoxComponent
{
    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        return base.TrySync(parameterName, bitStream);
    }
}
