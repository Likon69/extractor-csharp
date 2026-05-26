namespace MaNGOS.Extractor.Core.Models;

/// <summary>
/// Represents the progress of a single tile during extraction.
/// Used for real-time UI updates via IProgress&lt;TileProgressEvent&gt;.
/// </summary>
/// <param name="MapId">Map ID (0-999).</param>
/// <param name="TileX">Tile X index (0-63).</param>
/// <param name="TileY">Tile Y index (0-63).</param>
/// <param name="Status">Current processing status.</param>
/// <param name="Phase">Which extraction phase is active.</param>
/// <param name="Message">Optional diagnostic message.</param>
public readonly record struct TileProgressEvent(
    int MapId,
    int TileX,
    int TileY,
    TileStatus Status,
    ExtractionPhase Phase,
    string? Message = null);

/// <summary>
/// Processing status for a single tile.
/// </summary>
public enum TileStatus
{
    Pending,
    Processing,
    Done,
    Failed
}

/// <summary>
/// Extraction phase identifiers.
/// </summary>
public enum ExtractionPhase
{
    Map,
    Vmap,
    Road,
    Mmap
}