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

        Directory.CreateDirectory(destinationFolder);

        var moved = 0;
        foreach (var frame in frames.Where(f => f.IsRejected))
        {
            var destination = Path.Combine(destinationFolder, frame.FileName);
            if (File.Exists(destination))
            {
                destination = Path.Combine(destinationFolder, $"{Path.GetFileNameWithoutExtension(frame.FileName)}_{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(frame.FileName)}");
            }

            File.Move(frame.FilePath, destination);
            moved++;
        }

        return moved;
    }
}
