using System;

namespace SkySaga.Web;

/// <summary>
/// The account currently signed in, so responses can echo the name the player actually used
/// instead of a hardcoded one. Single-slot and in-memory: this emulator serves one player.
/// </summary>
public static class Session
{
    private static string _accountName = Environment.GetEnvironmentVariable("SKYSAGA_DEFAULT_ACCOUNT") ?? "Player";

    /// <summary>Account/login name, as sent by the launcher or the client's login screen.</summary>
    public static string AccountName
    {
        get => _accountName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _accountName)
                return;

            _accountName = value;

            Console.WriteLine($"[session] account name: {value}");
        }
    }

    /// <summary>Name of the created character; defaults to the account name.</summary>
    public static string? CharacterName { get; set; }

    public static string DisplayName => CharacterName ?? AccountName;
}
