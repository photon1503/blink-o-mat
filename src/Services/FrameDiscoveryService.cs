using System.IO;

namespace blink_o_mat.Services;

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
            .Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f))
            .SelectMany(f => Directory.EnumerateFiles(f, "*.*", searchOption))
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
