using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Rejector.Core.Services;

namespace Rejector.Avalonia.Infrastructure;

internal static class RenderedImageExtensions
{
    public static Bitmap ToBitmap(this RustafitsService.RenderedImage image)
    {
        var bgraData = new byte[image.Width * image.Height * 4];
        for (int sourceIndex = 0, targetIndex = 0; sourceIndex < image.Rgb24Data.Length; sourceIndex += 3, targetIndex += 4)
        {
            bgraData[targetIndex] = image.Rgb24Data[sourceIndex + 2];
            bgraData[targetIndex + 1] = image.Rgb24Data[sourceIndex + 1];
            bgraData[targetIndex + 2] = image.Rgb24Data[sourceIndex];
            bgraData[targetIndex + 3] = 0xFF;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);

        using var locked = bitmap.Lock();
        Marshal.Copy(bgraData, 0, locked.Address, bgraData.Length);
        return bitmap;
    }
}