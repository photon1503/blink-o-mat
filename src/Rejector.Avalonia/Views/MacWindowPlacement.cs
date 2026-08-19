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

        var nsWindow = GetNativeWindowHandle(handle.Handle);
        if (nsWindow == IntPtr.Zero)
        {
            return false;
        }

        var nativeFrame = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? GetFrameX64(nsWindow)
            : GetFrame(nsWindow, SelFrame);

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

        var nsWindow = GetNativeWindowHandle(handle.Handle);
        if (nsWindow == IntPtr.Zero)
        {
            return false;
        }

        SetFrame(
            nsWindow,
            SelSetFrameDisplay,
            new NativeRect(
                new NativePoint(frame.X, frame.Y),
                new NativeSize(frame.Width, frame.Height)),
            true);
        return true;
    }

    private static IntPtr GetNativeWindowHandle(IntPtr handle)
    {
        if (IsKindOfClass(handle, ClassNsWindow))
        {
            return handle;
        }

        if (IsKindOfClass(handle, ClassNsView))
        {
            var window = GetWindow(handle, SelWindow);
            return window != IntPtr.Zero && IsKindOfClass(window, ClassNsWindow)
                ? window
                : IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static NativeRect GetFrameX64(IntPtr receiver)
    {
        GetFrameStret(out var frame, receiver, SelFrame);
        return frame;
    }

    private static readonly IntPtr SelFrame = RegisterSelector("frame");
    private static readonly IntPtr SelWindow = RegisterSelector("window");
    private static readonly IntPtr SelIsKindOfClass = RegisterSelector("isKindOfClass:");
    private static readonly IntPtr SelSetFrameDisplay = RegisterSelector("setFrame:display:");
    private static readonly IntPtr ClassNsWindow = GetClass("NSWindow");
    private static readonly IntPtr ClassNsView = GetClass("NSView");

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool IsKindOfClass(IntPtr receiver, IntPtr selector, IntPtr nativeClass);

    private static bool IsKindOfClass(IntPtr receiver, IntPtr nativeClass)
    {
        return receiver != IntPtr.Zero && nativeClass != IntPtr.Zero && IsKindOfClass(receiver, SelIsKindOfClass, nativeClass);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NativeRect GetFrame(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetWindow(IntPtr receiver, IntPtr selector);

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
