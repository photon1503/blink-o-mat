using blink_o_mat.Models;
using System.IO;

namespace blink_o_mat.Services;

public sealed class FrameMoveService
{
    public int MoveRejected(IEnumerable<FrameItem> frames, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return 0;
        }

        var moved = 0;
        foreach (var frame in frames.Where(f => f.IsRejected))
        {
            string targetFolder;
            if (!string.IsNullOrWhiteSpace(frame.RelativePath))
            {
                targetFolder = Path.Combine(destinationFolder, frame.RelativePath);
            }
            else
            {
                targetFolder = destinationFolder;
            }

            Directory.CreateDirectory(targetFolder);

            var destination = Path.Combine(targetFolder, frame.FileName);
            if (File.Exists(destination))
            {
                destination = Path.Combine(targetFolder, $"{Path.GetFileNameWithoutExtension(frame.FileName)}_{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(frame.FileName)}");
            }

            File.Move(frame.FilePath, destination);
            moved++;
        }

        return moved;
    }
}
