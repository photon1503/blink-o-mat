using Rejector.Core.Abstractions;
using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_NormalizesProfilesAndDefaultProfile()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new AppSettingsService(new FixedPathProvider(tempRoot), "Rejector.Tests");
            var settings = new AppSettings
            {
                DefaultProfileName = "Missing",
                Profiles =
                [
                    new SettingsProfile { Name = "  " },
                    new SettingsProfile { Name = "default" },
                ],
            };

            service.Save(settings);

            var loaded = service.Load();

            Assert.Single(loaded.Profiles);
            Assert.Equal("Default", loaded.Profiles[0].Name);
            Assert.Equal("Default", loaded.DefaultProfileName);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void TryBackupPersistedSettings_MovesExistingSettingsFile()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var service = new AppSettingsService(new FixedPathProvider(tempRoot), "Rejector.Tests");
            service.Save(new AppSettings());

            var backedUp = service.TryBackupPersistedSettings();

            Assert.True(backedUp);
            Assert.False(File.Exists(service.SettingsFilePath));
            Assert.Single(Directory.GetFiles(service.SettingsDirectoryPath, "settings.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Rejector.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedPathProvider(string rootPath) : IAppDataPathProvider
    {
        public string GetApplicationDataDirectory(string applicationName)
        {
            return Path.Combine(rootPath, applicationName);
        }
    }
}