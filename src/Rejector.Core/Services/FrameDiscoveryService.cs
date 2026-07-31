using System.IO;

namespace Rejector.Core.Services;

public sealed class FrameDiscoveryService
{
    private static readonly string[] Extensions = [".fit", ".fits", ".xisf"];

    public IReadOnlyList<string> Discover(string folder, bool recursive = false)
    {
        var folders = folder.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Discover(folders, recursive);
    }

    public IReadOnlyList<string> Discover(IEnumerable<string> folders, bool recursive = false)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", searchOption))
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}