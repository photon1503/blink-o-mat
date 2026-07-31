namespace Rejector.Core.Abstractions;

public interface IAppDataPathProvider
{
    string GetApplicationDataDirectory(string applicationName);
}