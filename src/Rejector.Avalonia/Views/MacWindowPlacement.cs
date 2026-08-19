using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Rejector.Avalonia.Views;

internal static class MacWindowPlacement
{
    public static bool TryGetFrame(Window window, out Rect frame)
    {
        frame = default;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return false;
        }

        var nativeFrame = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? GetFrameX64(handle.Handle)
            : GetFrame(handle.Handle, SelFrame);

        frame = new Rect(
            nativeFrame.Origin.X,
            nativeFrame.Origin.Y,
            nativeFrame.Size.Width,
            nativeFrame.Size.Height);
        return frame.Width > 0 && frame.Height > 0;
    }

    public static bool TrySetFrame(Window window, Rect frame)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero || frame.Width <= 0 || frame.Height <= 0)
        {
            return false;
        }

        SetFrame(
            handle.Handle,
            SelSetFrameDisplay,
            new NativeRect(
                new NativePoint(frame.X, frame.Y),
                new NativeSize(frame.Width, frame.Height)),
            true);
        return true;
    }

    private static NativeRect GetFrameX64(IntPtr receiver)
    {
        GetFrameStret(out var frame, receiver, SelFrame);
        return frame;
    }

    private static readonly IntPtr SelFrame = RegisterSelector("frame");
    private static readonly IntPtr SelSetFrameDisplay = RegisterSelector("setFrame:display:");

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NativeRect GetFrame(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend_stret")]
    private static extern void GetFrameStret(out NativeRect frame, IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetFrame(IntPtr receiver, IntPtr selector, NativeRect frame, bool display);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(NativePoint Origin, NativeSize Size);
}
