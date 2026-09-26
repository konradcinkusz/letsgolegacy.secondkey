using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace SecondKey.Capture.Tests;

/// <summary>The sample shop on a real port, as either side of the demo.</summary>
public sealed class SampleShopHost : WebApplicationFactory<Program>
{
    private readonly string _variant;

    public SampleShopHost(string variant)
    {
        _variant = variant;

        // UseKestrel() without a port, and the port set by URL: UseKestrel(port) rewrites the
        // server's addresses after start-up and races with it under load.
        UseKestrel();
        StartServer();
    }

    public Uri Address =>
        new(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseUrls("http://127.0.0.1:0");
        builder.UseSetting("SAMPLESHOP_VARIANT", _variant);
        builder.UseSetting("SAMPLESHOP_ALLOW_RESET", "1");
    }
}
