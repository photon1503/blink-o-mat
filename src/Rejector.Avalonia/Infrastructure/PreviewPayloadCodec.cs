using Avalonia.Media.Imaging;
using Rejector.Core.Services;

namespace Rejector.Avalonia.Infrastructure;

internal static class PreviewPayloadCodec
{
    private const string Prefix = "rgb24:";

    public static string Encode(RustafitsService.RenderedImage image)
    {
        return $"{Prefix}{image.Width}:{image.Height}:{Convert.ToBase64String(image.Rgb24Data)}";
    }

    public static Bitmap? DecodeToBitmap(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = payload.Split(':', 4, StringSplitOptions.None);
        if (parts.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var width) || !int.TryParse(parts[2], out var height))
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(parts[3]);
            var image = new RustafitsService.RenderedImage(width, height, data, width * 3);
            return image.ToBitmap();
        }
        catch
        {
            return null;
        }
    }
}