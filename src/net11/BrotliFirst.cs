using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CompressionDemo;

public static partial class Apps
{
    // .NET 11 with Brotli pinned first. Adding any provider replaces the whole
    // default list, so all three are listed, in the order to prefer them.
    public static WebApplication BrotliFirst(Action<KestrelServerOptions> listen)
    {
        var builder = NewBuilder(listen);

        builder.Services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<ZstandardCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });

        var app = builder.Build();
        app.UseResponseCompression();
        app.MapGet("/orders", () => Results.Bytes(Payload.Json, "application/json"));
        return app;
    }
}
