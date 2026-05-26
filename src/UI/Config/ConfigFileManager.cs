using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Models;

namespace MaNGOS.Extractor.UI.Config;

public static class ConfigFileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ExtractorConfig? Load(string path, ILogger? logger = null)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ExtractorConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load config from {Path}", path);
            return null;
        }
    }

    public static void Save(string path, ExtractorConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static ExtractorConfig CreateDefault() => new()
    {
        WowClientPath = @"C:\World of Warcraft",
        OutputPath = @"D:\wow-data\output",
        GoSpawnsPath = Path.Combine(AppContext.BaseDirectory, "gameobject_spawns.bin"),
        OffMeshPath = Path.Combine(AppContext.BaseDirectory, "offmesh.txt"),
        EnabledPhases = new[] { "Map", "Vmap", "Road", "Mmap" },
        Maps = new[] { 0, 1, 530, 571 },
        Threads = 4,
        BigBaseUnit = false,
        RecastConfig = new RecastConfig()
    };
}
