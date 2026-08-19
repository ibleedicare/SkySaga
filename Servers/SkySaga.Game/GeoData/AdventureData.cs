namespace SkySaga.Game.GeoData;

/// <summary>
/// One entry from <c>GeoData.json &gt; Adventures</c>. Every world the client can be in —
/// a home island, a quest adventure, a PVP match, a sandbox — is an "Adventure", and its
/// <see cref="WorldType"/> is what switches the client into that mode. The server tells the
/// client which one via <c>ServerInfo.ServerAdventureCrc</c> (the CRC of <see cref="Name"/>);
/// the client resolves it in its own copy of this table.
/// </summary>
/// <remarks>
/// WorldType values observed in build 10414:
/// 0 = character creator, 1 = home island, 2 = adventure/quest, 3 = PVP, 5 = test/sandbox.
/// </remarks>
public sealed record AdventureData
{
    public required string Name { get; init; }
    public required uint NameHash { get; init; }

    /// <summary>The mode the client switches into. See the remarks for known values.</summary>
    public int WorldType { get; init; }

    /// <summary>Map size in chunks per side (0 for home islands).</summary>
    public int MapSize { get; init; }

    /// <summary>Root world-feature that drives terrain/structure generation, e.g. "Forest_Castle".</summary>
    public string RootFeatureName { get; init; } = string.Empty;

    public bool IsHomeIsland => WorldType == 1;
}
