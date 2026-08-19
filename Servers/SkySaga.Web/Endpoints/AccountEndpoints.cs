using System;
using System.IO;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace SkySaga.Web.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        // The client posts the set of account keys it wants; log them so the ones we do not
        // return yet are visible (the social UI has no id to query with, which suggests it
        // expects an identifier from here).
        app.MapPost("/api/account/get", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);

            var body = await reader.ReadToEndAsync();

            Console.WriteLine($"[account/get] requested keys: {body}");

            return Results.Ok(new
            {
                result = new
                {
                    keySubset = new
                    {
                        RESERVED_NAME = Session.AccountName
                    }
                }
            });
        });
    }
}