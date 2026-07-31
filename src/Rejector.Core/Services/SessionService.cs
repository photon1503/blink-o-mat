using System.Text.Json;
using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed class SessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public void Save(string path, SessionData session)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(path, json);
    }

    public SessionData? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionData>(json, JsonOptions);
    }

    public static string? EncodePngBytes(byte[]? pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(pngBytes);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? DecodePngBytes(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch
        {
            return null;
        }
    }
}