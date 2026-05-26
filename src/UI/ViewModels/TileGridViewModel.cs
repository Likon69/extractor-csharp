using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;

namespace MaNGOS.Extractor.UI.ViewModels;

/// <summary>
/// Manages the 64×64 tile grid display and tile state updates.
/// Thread-safe — dispatches all UI updates to the UI thread.
/// </summary>
public sealed class TileGridViewModel
{
    private readonly WriteableBitmap _bitmap;
    private readonly Dispatcher _dispatcher;
    private readonly bool[] _tileExists;
    private readonly TileStatus[] _tileStatus;
    private readonly object _lock = new();

    public const int GridSize = WowConstants.GridSize; // 64

    // Couleurs ARGB
    private const uint ColorNone = 0xFF2D2D2D;    // Tile n'existe pas
    private const uint ColorPending = 0xFF4A4A6A; // En attente
    private const uint ColorProcessing = 0xFFF0A500; // En cours
    private const uint ColorDone = 0xFF2ECC71;     // Terminé
    private const uint ColorFailed = 0xFFE74C3C;   // Erreur

    /// <summary>Bitmap for rendering — lock before writing.</summary>
    public WriteableBitmap Bitmap => _bitmap;

    /// <summary>Collection of map IDs to display.</summary>
    public ObservableCollection<MapListItem> Maps { get; } = new();

    /// <summary>Currently selected map ID.</summary>
    public int SelectedMapId { get; set; }

    /// <summary>Creates a new tile grid view model.</summary>
    public TileGridViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tileExists = new bool[GridSize * GridSize];
        _tileStatus = new TileStatus[GridSize * GridSize];
        _bitmap = new WriteableBitmap(GridSize, GridSize, 96, 96, PixelFormats.Bgra32, null);

        // Initialize all tiles as non-existent
        Array.Fill(_tileStatus, TileStatus.Pending);
        ClearGrid();
    }

    /// <summary>
    /// Initializes tiles from WDT data.
    /// Call this when loading a map.
    /// </summary>
    public void InitializeTiles(IEnumerable<(int X, int Y)> existingTiles)
    {
        lock (_lock)
        {
            // Reset all to non-existent
            Array.Clear(_tileExists, 0, _tileExists.Length);

            // Mark existing tiles
            foreach (var (x, y) in existingTiles)
            {
                int idx = y * GridSize + x;
                if (idx >= 0 && idx < _tileExists.Length)
                {
                    _tileExists[idx] = true;
                    _tileStatus[idx] = TileStatus.Pending;
                }
            }

            // Re-render
            RenderGrid();
        }
    }

    /// <summary>
    /// Handles a tile progress event from extraction services.
    /// Thread-safe — dispatches to UI thread.
    /// </summary>
    public void OnTileProgress(TileProgressEvent e)
    {
        if (e.MapId != SelectedMapId)
            return;

        _dispatcher.InvokeAsync(() =>
        {
            lock (_lock)
            {
                int idx = e.TileY * GridSize + e.TileX;
                if (idx < 0 || idx >= _tileStatus.Length)
                    return;

                uint color = e.Status switch
                {
                    TileStatus.Pending => _tileExists[idx] ? ColorPending : ColorNone,
                    TileStatus.Processing => ColorProcessing,
                    TileStatus.Done => ColorDone,
                    TileStatus.Failed => ColorFailed,
                    _ => ColorPending
                };

                _tileStatus[idx] = e.Status;
                WritePixel(e.TileX, e.TileY, color);
            }
        });
    }

    /// <summary>
    /// Clears all tiles and resets to pending state.
    /// </summary>
    public void ClearGrid()
    {
        lock (_lock)
        {
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    uint color = _tileExists[y * GridSize + x] ? ColorPending : ColorNone;
                    WritePixel(x, y, color);
                }
            }
        }
    }

    /// <summary>
    /// Resets all tiles to pending state (clears done/failed).
    /// </summary>
    public void ResetAllTiles()
    {
        lock (_lock)
        {
            for (int i = 0; i < _tileStatus.Length; i++)
            {
                if (_tileExists[i])
                    _tileStatus[i] = TileStatus.Pending;
            }
            RenderGrid();
        }
    }

    private void WritePixel(int x, int y, uint color)
    {
        _bitmap.WritePixels(new System.Windows.Int32Rect(x, y, 1, 1), new[] { (int)color }, 4, 0);
    }

    private void RenderGrid()
    {
        var pixels = new int[GridSize * GridSize];

        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                int idx = y * GridSize + x;
                uint color = _tileExists[idx]
                    ? _tileStatus[idx] switch
                    {
                        TileStatus.Pending => ColorPending,
                        TileStatus.Processing => ColorProcessing,
                        TileStatus.Done => ColorDone,
                        TileStatus.Failed => ColorFailed,
                        _ => ColorPending
                    }
                    : ColorNone;

                pixels[idx] = (int)color;
            }
        }

        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, GridSize, GridSize), pixels, GridSize * 4, 0);
    }
}

/// <summary>
/// Represents a map entry in the list.
/// </summary>
public sealed class MapListItem
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsSelected { get; set; }

    public override string ToString() => $"[{Id}] {Name}";
}