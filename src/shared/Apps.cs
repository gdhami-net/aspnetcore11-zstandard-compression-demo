using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CompressionDemo;

// The small apps the probe starts. Each one serves the same JSON at /orders.
public static partial class Apps
{
    // The app the post is about: response compression with every default left alone.
    public static WebApplication Defaults(Action<KestrelServerOptions> listen)
    {
        var builder = NewBuilder(listen);

        builder.Services.AddResponseCompression();

        var app = builder.Build();
        app.UseResponseCompression();
        app.MapGet("/orders", () => Results.Bytes(Payload.Json, "application/json"));
        MapEcho(app);
        return app;
    }

    // Same app, with compression switched on for HTTPS requests too.
    public static WebApplication HttpsOn(Action<KestrelServerOptions> listen)
    {
        var builder = NewBuilder(listen);
        builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

        var app = builder.Build();
        app.UseResponseCompression();
        app.MapGet("/orders", () => Results.Bytes(Payload.Json, "application/json"));
        MapEcho(app);
        return app;
    }

    // Plain HTTP from a proxy that ends TLS and says so in X-Forwarded-Proto.
    // UseForwardedHeaders runs first, so the request now counts as HTTPS.
    public static WebApplication BehindProxy(Action<KestrelServerOptions> listen)
    {
        var builder = NewBuilder(listen);
        builder.Services.AddResponseCompression();
        builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedProto);

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseResponseCompression();
        app.MapGet("/orders", () => Results.Bytes(Payload.Json, "application/json"));
        return app;
    }

    static WebApplicationBuilder NewBuilder(Action<KestrelServerOptions> listen)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(listen);
        return builder;
    }

    // Echoes the Accept-Encoding the client sent, and records the encoding the
    // middleware chose for it. Used by the Edge check.
    static void MapEcho(WebApplication app)
    {
        app.MapGet("/echo", (HttpContext context) =>
        {
            var sent = context.Request.Headers.AcceptEncoding.ToString();
            var agent = context.Request.Headers.UserAgent.ToString();
            var client = agent.Split(' ').LastOrDefault(t => t.StartsWith("Edg/")) ?? agent.Split(' ').FirstOrDefault() ?? "";
            context.Response.OnCompleted(() =>
            {
                var chosen = context.Response.Headers.ContentEncoding.ToString();
                var url = $"{context.Request.Scheme}://{context.Request.Host}";
                Console.WriteLine($"ECHO {client}  {url,-27} sent \"{sent}\"  got {(chosen.Length == 0 ? "none" : chosen)}");
                return Task.CompletedTask;
            });
            return Results.Text($"accept-encoding: {sent}\n", "text/plain");
        });
    }

    public static X509Certificate2 SelfSignedLocalhost()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }
}
