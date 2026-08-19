using System;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Hosting;

using SkySaga.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Bind all interfaces by default so the client can live in a VM/container.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("SKYSAGA_WEB_URLS") ?? "http://0.0.0.0:5164");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
});

// Emit property names exactly as declared. The default camelCase policy rewrites keys the
// client may read case-sensitively — e.g. RESERVED_NAME was going out as "reserveD_NAME".
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

app.UseHttpLogging();

app.MapGet("/ping", () => Results.Ok());

app.MapAccountEndpoints();
app.MapMatchMakingEndpoints();
app.MapBinaryStorageEndpoint();
app.MapGameConductorEndpoints();
app.MapAuthenticationEndpoints();
app.MapPersistentRecordEndpoints();
app.MapSocialGraphEndpoints();

app.Run();