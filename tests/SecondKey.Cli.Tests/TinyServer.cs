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

    private TinyServer(WebApplication app, Uri address, Task received)
    {
        _app = app;
        Address = address;
        Received = received;
    }

    public Uri Address { get; }

    /// <summary>Completes when the first request arrives, before it is answered.</summary>
    public Task Received { get; }

    /// <param name="variant">Named in every answer, so a test can tell the sides apart.</param>
    /// <param name="delay">How long every answer takes: a slow legacy system.</param>
    public static async Task<TinyServer> StartAsync(string variant, TimeSpan delay = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapFallback(async (HttpContext context) =>
        {
            received.TrySetResult();
            await Task.Delay(delay);
            return Results.Json(new { variant, path = context.Request.Path.Value });
        });
        await app.StartAsync();
        return new TinyServer(app, new Uri(app.Urls.First()), received.Task);
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
