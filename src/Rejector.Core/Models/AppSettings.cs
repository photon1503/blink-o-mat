namespace Rejector.Core.Models;

public sealed class AppSettings
{
    public string? InputFolder { get; set; }

    public string? RejectedFolder { get; set; }

    public bool IncludeSubfolders { get; set; }

    public bool WatchFolder { get; set; }

    public string DefaultProfileName { get; set; } = "Default";

    public List<SettingsProfile> Profiles { get; set; } =
    [
        new SettingsProfile
        {
            Name = "Default",
        },
    ];
}