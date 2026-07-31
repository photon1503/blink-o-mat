using System.IO;
using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed class FrameMoveService
{
    public IReadOnlyList<ProcessedFrame> MoveRejected(
        IEnumerable<ProcessedFrame> frames,
        string destinationFolder,
        IReadOnlyCollection<string>? filterKeys = null)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return [];
        }

        var toMove = frames.Where(frame => frame.IsRejected);
        if (filterKeys is not null)
        {
            toMove = toMove.Where(frame =>
            {
                var key = string.IsNullOrWhiteSpace(frame.FilterName) ? "(no filter)" : frame.FilterName;
                return filterKeys.Contains(key);
            });
        }

        var moved = new List<ProcessedFrame>();
        foreach (var frame in toMove.ToList())
        {
            var targetFolder = string.IsNullOrWhiteSpace(frame.RelativePath)
                ? destinationFolder
                : Path.Combine(destinationFolder, frame.RelativePath);

            Directory.CreateDirectory(targetFolder);

            var destination = Path.Combine(targetFolder, frame.FileName);
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    targetFolder,
                    $"{Path.GetFileNameWithoutExtension(frame.FileName)}_{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(frame.FileName)}");
            }

            File.Move(frame.FilePath, destination);
            moved.Add(frame);
        }

        return moved;
    }
}