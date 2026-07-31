using Rejector.Core.Abstractions;

namespace Rejector.Core.Services;

public sealed class DefaultAppDataPathProvider : IAppDataPathProvider
{
    public string GetApplicationDataDirectory(string applicationName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, applicationName);
    }
}