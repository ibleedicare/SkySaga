using System;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace SkySaga.Web.Endpoints;

public static class PersistentRecordEndpoints
{
    private static Guid _characterUUID = Guid.Empty;

    public static void MapPersistentRecordEndpoints(this WebApplication app)
    {
        app.MapGet("/GetGUID", () => new
        {
            result = new
            {
                GUID = _characterUUID
            }
        });

        app.MapGet("/api/persistent-record/characters/list", () =>
        {
            if (_characterUUID == Guid.Empty)
            {
                return Results.Ok(new
                {
                    Error = new
                    {
                        code = 11001,
                        message = "",
                        detail = ""
                    }
                });
            }

            return Results.Ok(new
            {
                result = new
                {
                    characters = new[]
                    {
                        new
                        {
                            uuid = _characterUUID,
                            name = Session.DisplayName,
                            homeBiome = "Desert", // (string?)null, // null > character creation
                            positionInList = 0
                        }
                    }
                }
            });
        });

        // The 2017 builds (Alpha V10 b36731) ask for the *active* character instead of
        // listing characters: RPC `HTTPRPCDownloadActiveCharacter`. The response field name
        // is `character` (singular) — recovered from the client's own string table, where
        // each RPC's field names sit next to its name and path. Build 10414 does not use
        // this route, so adding it does not affect the retail flow.
        //
        // The empty case is NOT the 11001 error /characters/list returns: 36731 renders that
        // as "No character available unused 11001" — it understood "no character" but the
        // code is not one it handles. So "no active character" is signalled as a *success*
        // with a null character, which is what should move the frontend to its CREATE_CHAR
        // state (one of LOGIN / SGLOGIN / CREATE_CHAR / DOWNLOADING_CHARS / SELECTING_CHAR /
        // CONNECT_TEST / WAIT_WORLD / REQUEST_WORLD, read out of the client's string table).
        app.MapGet("/api/persistent-record/characters/_active", () =>
        {
            if (_characterUUID == Guid.Empty)
            {
                return Results.Ok(new
                {
                    result = new
                    {
                        character = (object?)null
                    }
                });
            }

            return Results.Ok(new
            {
                result = new
                {
                    character = new
                    {
                        uuid = _characterUUID,
                        name = Session.DisplayName,
                        homeBiome = "Desert",
                        positionInList = 0
                    }
                }
            });
        });

        app.MapPost("/api/persistent-record/characters/_create", () =>
        {
            _characterUUID = Guid.NewGuid();

            return new
            {
                result = new
                {
                    characterUUID = _characterUUID
                }
            };
        });
    }
}