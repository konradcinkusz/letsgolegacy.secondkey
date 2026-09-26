using SecondKey.Artifacts.Capture;

namespace SecondKey.Capture.Tests;

public class ProxyLifecycleTests
{
    [Fact]
    public async Task The_proxy_stops_by_itself_after_the_requested_number_of_exchanges()
    {
        await using var shop = new SampleShopHost("candidate");
        var output = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.skcap");
        try
        {
            await using var proxy = await RecordingProxy.StartAsync(new CaptureOptions
            {
                Listen = new Uri("http://127.0.0.1:0"),
                Target = shop.Address,
                Output = output,
                ExitAfter = 2,
            });
            using var client = new HttpClient { BaseAddress = proxy.Address };

            await client.GetAsync("/health");
            await client.GetAsync("/health");

            var finished = await Task.WhenAny(proxy.Completion, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(proxy.Completion, finished);
            Assert.Equal(2, proxy.Recorded);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task A_legacy_system_that_does_not_answer_is_not_recorded_as_behaviour()
    {
        var output = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.skcap");
        try
        {
            await using (var proxy = await RecordingProxy.StartAsync(new CaptureOptions
            {
                Listen = new Uri("http://127.0.0.1:0"),
                Target = new Uri("http://127.0.0.1:9"),
                Output = output,
            }))
            {
                using var client = new HttpClient { BaseAddress = proxy.Address };
                var response = await client.GetAsync("/anything");

                Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
                Assert.Equal(1, proxy.Skipped);
                Assert.Equal(0, proxy.Recorded);
            }

            Assert.Empty(CaptureFile.Read(output).Exchanges);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Starting_without_options_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecordingProxy.StartAsync(null!));
    }
}
