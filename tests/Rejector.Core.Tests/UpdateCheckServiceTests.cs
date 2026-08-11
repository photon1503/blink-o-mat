using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task GetLatestReleaseInfoAsync_ReturnsInstallerUrlAndReleaseNotes()
    {
        var service = new UpdateCheckService(new StubHttpClient(new
        {
            tag_name = "v1.2.3",
            body = "# Changelog",
            assets = new[]
            {
                new { name = "Rejector-1.2.3.exe", browser_download_url = "https://example.com/Rejector.exe" },
            },
        }));

        var info = await service.GetLatestReleaseInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("1.2.3", info!.Version);
        Assert.Equal("# Changelog", info.ReleaseNotesMarkdown);
        Assert.Equal("https://example.com/Rejector.exe", info.InstallerUrl);
    }

    private sealed class StubHttpClient(object payload) : HttpClient(new StubHandler(payload))
    {
    }

    private sealed class StubHandler(object payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(response);
        }
    }
}
