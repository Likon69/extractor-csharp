using System.Text.Json.Serialization;

namespace MaNGOS.Extractor.Core.Models;

public sealed class ExtractorConfig
{
    [JsonPropertyName("WowClientPath")]
    public string? WowClientPath { get; init; }

    [JsonPropertyName("OutputPath")]
    public string? OutputPath { get; init; }

    [JsonPropertyName("OffMeshPath")]
    public string? OffMeshPath { get; init; }

    [JsonPropertyName("GoSpawnsPath")]
    public string GoSpawnsPath { get; init; } = "gameobject_spawns.bin";

    [JsonPropertyName("Locale")]
    public string Locale { get; init; } = "enUS";

    [JsonPropertyName("Phases")]
    public string[] EnabledPhases { get; init; } = Array.Empty<string>();

    [JsonPropertyName("Maps")]
    public int[] Maps { get; init; } = Array.Empty<int>();

    [JsonPropertyName("Threads")]
    public int Threads { get; init; } = 4;

    [JsonPropertyName("BigBaseUnit")]
    public bool BigBaseUnit { get; init; }

    [JsonPropertyName("SingleTileEnabled")]
    public bool SingleTileEnabled { get; init; }

    [JsonPropertyName("SingleTileX")]
    public int SingleTileX { get; init; }

    [JsonPropertyName("SingleTileY")]
    public int SingleTileY { get; init; }

    [JsonPropertyName("RecastConfig")]
    public RecastConfig? RecastConfig { get; init; }

    // Window geometry — NaN means "use default"
    [JsonPropertyName("WindowLeft")]
    public double WindowLeft { get; init; } = double.NaN;

    [JsonPropertyName("WindowTop")]
    public double WindowTop { get; init; } = double.NaN;

    [JsonPropertyName("WindowWidth")]
    public double WindowWidth { get; init; } = 1000;

    [JsonPropertyName("WindowHeight")]
    public double WindowHeight { get; init; } = 700;
}

public readonly struct RecastConfig
{
    [JsonPropertyName("CellSize")]
    public float CellSize { get; init; }

    [JsonPropertyName("CellHeight")]
    public float CellHeight { get; init; }

    [JsonPropertyName("WalkableSlopeAngle")]
    public float WalkableSlopeAngle { get; init; }

    [JsonPropertyName("WalkableHeight")]
    public int WalkableHeight { get; init; }

    [JsonPropertyName("WalkableRadius")]
    public int WalkableRadius { get; init; }

    [JsonPropertyName("WalkableClimb")]
    public int WalkableClimb { get; init; }

    public RecastConfig(
        float cellSize = 0.303030f,
        float cellHeight = 0.2f,
        float walkableSlopeAngle = 50.0f,
        int walkableHeight = 11,
        int walkableRadius = 2,
        int walkableClimb = 5)
    {
        CellSize = cellSize;
        CellHeight = cellHeight;
        WalkableSlopeAngle = walkableSlopeAngle;
        WalkableHeight = walkableHeight;
        WalkableRadius = walkableRadius;
        WalkableClimb = walkableClimb;
    }
}
