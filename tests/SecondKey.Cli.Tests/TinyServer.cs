using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SecondKey.Cli.Tests;

/// <summary>A minimal HTTP system to point sk at: every path answers JSON naming the variant.</summary>
internal sealed class TinyServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TinyServer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    public static async Task<TinyServer> StartAsync(string variant)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapFallback((HttpContext context) => Results.Json(new { variant, path = context.Request.Path.Value }));
        await app.StartAsync();
        return new TinyServer(app, new Uri(app.Urls.First()));
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
