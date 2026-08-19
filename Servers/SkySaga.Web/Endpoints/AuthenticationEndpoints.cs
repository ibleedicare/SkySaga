using Microsoft.AspNetCore.Builder;

namespace SkySaga.Web.Endpoints;

public static class AuthenticationEndpoints
{
    public record ApplicationLogin(string Name, string Password);

    public record SmilegateAuthLogin(string Token);

    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/authentication/applications/names/login", (ApplicationLogin login) =>
        {
            // The name the player signed in with; everything else echoes it back.
            Session.AccountName = login.Name;

            return new
            {
            result = new
            {
                    tokenId = "tokenId",
                    refreshingTokenId = "refreshingTokenId",
                    timeout = 999999
                }
            };
        });

        // Used when the client is started with the `auth` variable set (SGLogin frontend),
        // i.e. driven by a launcher rather than the in-client login screen. The token is
        // whatever the launcher passed; treat "name" or "name:anything" as the account.
        app.MapPost("/api/authentication/sgauth/_login", (SmilegateAuthLogin login) =>
        {
            Session.AccountName = (login.Token ?? string.Empty).Split(':', 2)[0];

            return new
            {
            result = new
            {
                sgUser = "",
                memberId = "1",
                username = Session.AccountName,
                token = new
                {
                    tokenId = "tokenId",
                    refreshingTokenId = "refreshingTokenId",
                    timeout = 999999
                }
            }
            };
        });

        app.MapPost("/api/authentication/credentials/usernames/autologin", () => new
        {
            result = new
            {
                tokenId = "tokenId",
                refreshingTokenId = "refreshingTokenId",
                timeout = 999999
            }
        });
    }
}