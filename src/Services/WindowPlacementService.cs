using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace blink_o_mat.Services;

internal static class WindowPlacementService
{
    private const string MainWindowKey = "MainWindow";
    private const string PreviewWindowKey = "PreviewWindow";

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rejector");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "window-placement.json");

    public static void RestoreMainWindow(Window window)
    {
        RestoreWindow(window, MainWindowKey);
    }

    public static void SaveMainWindow(Window window)
    {
        SaveWindow(window, MainWindowKey);
    }

    public static void RestorePreviewWindow(Window window)
    {
        RestoreWindow(window, PreviewWindowKey);
    }

    public static void SavePreviewWindow(Window window)
    {
        SaveWindow(window, PreviewWindowKey);
    }

    private static void RestoreWindow(Window window, string key)
    {
        var settings = LoadSettings();
        if (!settings.TryGetValue(key, out var placement) || !IsValid(placement))
        {
            return;
        }

        var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!IsOnScreen(bounds))
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = placement.Width;
        window.Height = placement.Height;

        if (placement.WindowState == WindowState.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            window.WindowState = WindowState.Normal;
        }
    }

    private static void SaveWindow(Window window, string key)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (!IsValid(bounds))
        {
            return;
        }

        var settings = LoadSettings();
        settings[key] = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal,
        };

        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static Dictionary<string, WindowPlacement> LoadSettings()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(json)
                ?? new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);
        }
    }

    private static bool IsValid(WindowPlacement placement)
    {
        return placement.Width > 0
            && placement.Height > 0
            && !double.IsNaN(placement.Left)
            && !double.IsNaN(placement.Top)
            && !double.IsNaN(placement.Width)
            && !double.IsNaN(placement.Height)
            && !double.IsInfinity(placement.Left)
            && !double.IsInfinity(placement.Top)
            && !double.IsInfinity(placement.Width)
            && !double.IsInfinity(placement.Height);
    }

    private static bool IsValid(Rect bounds)
    {
        return bounds.Width > 0
            && bounds.Height > 0
            && !double.IsNaN(bounds.Left)
            && !double.IsNaN(bounds.Top)
            && !double.IsNaN(bounds.Width)
            && !double.IsNaN(bounds.Height)
            && !double.IsInfinity(bounds.Left)
            && !double.IsInfinity(bounds.Top)
            && !double.IsInfinity(bounds.Width)
            && !double.IsInfinity(bounds.Height);
    }

    private static bool IsOnScreen(Rect bounds)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return virtualScreen.IntersectsWith(bounds);
    }

    private sealed class WindowPlacement
    {
        public double Left { get; set; }

        public double Top { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public WindowState WindowState { get; set; }
    }
}
