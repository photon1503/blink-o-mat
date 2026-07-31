using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class SessionServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSessionData()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new SessionService();
            var path = Path.Combine(tempRoot, "session.json");
            var session = new SessionData
            {
                SavedAt = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
                InputFolder = "/input",
                RejectedFolder = "/rejected",
                IncludeSubfolders = true,
                Frames =
                [
                    new SessionFrameEntry
                    {
                        FilePath = "/input/frame1.fit",
                        FileName = "frame1.fit",
                        OverallScore = 4.2,
                        Fwhm = 3.1,
                        StarCount = 123,
                    },
                ],
            };

            service.Save(path, session);
            var loaded = service.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(session.InputFolder, loaded!.InputFolder);
            Assert.Equal(session.RejectedFolder, loaded.RejectedFolder);
            Assert.Single(loaded.Frames);
            Assert.Equal("frame1.fit", loaded.Frames[0].FileName);
            Assert.Equal(4.2, loaded.Frames[0].OverallScore);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void EncodeAndDecodePngBytes_RoundTripsPayload()
    {
        var bytes = new byte[] { 0, 1, 2, 3, 250, 251, 252 };

        var encoded = SessionService.EncodePngBytes(bytes);
        var decoded = SessionService.DecodePngBytes(encoded);

        Assert.NotNull(encoded);
        Assert.Equal(bytes, decoded);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Rejector.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}