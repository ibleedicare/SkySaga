using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Hosting;

using SkySaga.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// The 2017 builds (Alpha V10 b36731, and presumably the beta) request the web API over
// *https*. The client formats its request URLs with one of two templates picked per RPC by a
// bool parameter — `https://%s:%u%s` or `http://%s:%u%s` — and the login RPC
// (/api/authentication/applications/names/login) is registered as secure. Retail 10414 uses
// plain http, which is why the emulator has always been http-only.
//
// So HTTPS is opt-in: set SKYSAGA_WEB_HTTPS=1 for the 2017 clients, leave it unset for 10414.
// Note that a single port cannot serve both schemes — with HTTPS on, plain-http requests to
// this port (the client's `http://%s:%u/ping` connectivity test) will fail.
//
// SKYSAGA_WEB_CERT / SKYSAGA_WEB_CERT_PASSWORD point at a PKCS#12 file; without them a
// self-signed certificate for 127.0.0.1 is generated on first run and cached next to the
// binary so the client sees a stable certificate across restarts.
var useHttps = Environment.GetEnvironmentVariable("SKYSAGA_WEB_HTTPS") == "1";

if (useHttps)
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("SKYSAGA_WEB_PORT"), out var p) ? p : 5164;
    var cert = LoadOrCreateCertificate();

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Any, port, listen => listen.UseHttps(cert));
    });
}
else
{
    // Bind all interfaces by default so the client can live in a VM/container.
    builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("SKYSAGA_WEB_URLS") ?? "http://0.0.0.0:5164");
}

static X509Certificate2 LoadOrCreateCertificate()
{
    var path = Environment.GetEnvironmentVariable("SKYSAGA_WEB_CERT");
    var password = Environment.GetEnvironmentVariable("SKYSAGA_WEB_CERT_PASSWORD") ?? "";

    if (!string.IsNullOrEmpty(path))
    {
        Console.WriteLine($"web: using certificate {path}");
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    var cached = Path.Combine(AppContext.BaseDirectory, "skysaga-dev-cert.pfx");
    if (File.Exists(cached))
    {
        Console.WriteLine($"web: using cached self-signed certificate {cached}");
        return X509CertificateLoader.LoadPkcs12FromFile(cached, "");
    }

    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddIpAddress(IPAddress.Loopback);
    sanBuilder.AddDnsName("localhost");
    request.CertificateExtensions.Add(sanBuilder.Build());
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

    var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    File.WriteAllBytes(cached, generated.Export(X509ContentType.Pfx));
    Console.WriteLine($"web: generated self-signed certificate -> {cached}");

    return X509CertificateLoader.LoadPkcs12FromFile(cached, "");
}

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