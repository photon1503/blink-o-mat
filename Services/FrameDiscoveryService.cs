using System.IO;

namespace blink_o_mat.Services;

public sealed class FrameDiscoveryService
{
    private static readonly string[] Extensions = [".fit", ".fits", ".xisf"];

    public IReadOnlyList<string> Discover(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
