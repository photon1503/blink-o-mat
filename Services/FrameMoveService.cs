using blink_o_mat.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace blink_o_mat.Services;

public sealed class FrameMoveService
{
    /// <param name="filterKeys">
    /// When non-null, only rejected frames whose FilterName (or "(no filter)" for blanks) is
    /// in this set are moved. When null every rejected frame is moved.
    /// </param>
    /// <returns>The list of <see cref="FrameItem"/> objects that were successfully moved.</returns>
    public IReadOnlyList<FrameItem> MoveRejected(IEnumerable<FrameItem> frames, string destinationFolder,
        IReadOnlyCollection<string>? filterKeys = null)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return [];
        }

        var toMove = frames.Where(f => f.IsRejected);

        if (filterKeys != null)
        {
            toMove = toMove.Where(f =>
            {
                var key = string.IsNullOrWhiteSpace(f.FilterName) ? "(no filter)" : f.FilterName;
                return filterKeys.Contains(key);
            });
        }

        var moved = new List<FrameItem>();
        foreach (var frame in toMove.ToList())
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
            moved.Add(frame);
        }

        return moved;
    }
}
