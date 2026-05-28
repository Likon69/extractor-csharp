using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;

namespace MaNGOS.Extractor.UI.ViewModels;

/// <summary>
/// Manages the 64×64 tile grid display and tile state updates.
/// Bitmap is 640×640 (10 px per tile, with 1-px separator border).
/// Thread-safe — dispatches all UI updates to the UI thread.
/// </summary>
public sealed class TileGridViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly WriteableBitmap _bitmap;
    private readonly Dispatcher      _dispatcher;
    private readonly bool[]          _tileExists; // tiles known from WDT
    private readonly bool[]          _tileDone;   // deduplicate done-count across phases
    private readonly uint[]          _tileColor;  // current render color per tile
    private readonly object          _lock = new();

    private int _doneTiles;
    private int _pendingTiles;

    public const int GridSize    = WowConstants.GridSize; // 64
    private const int CellSize   = 10;                    // px per tile cell
    private const int BitmapSize = GridSize * CellSize;   // 640

    // ── Palette ─────────────────────────────────────────────────────────────
    private const uint ColorEmpty      = 0xFF0A0E14; // void / no tile
    private const uint ColorBorder     = 0xFF060A0F; // 1-px cell separator
    private const uint ColorPending    = 0xFF1B2A3B; // fog of war (known, not yet built)
    private const uint ColorProcessing = 0xFFCC8800; // amber — building
    private const uint ColorFailed     = 0xFFD62F2F; // red   — error

    // Done colours, one per phase
    private const uint ColorDoneMap    = 0xFF1768AC; // blue
    private const uint ColorDoneVmap   = 0xFF6A1FB5; // purple
    private const uint ColorDoneRoad   = 0xFFB8960C; // gold
    private const uint ColorDoneMmap   = 0xFF1A7F37; // green  ← mmap = most visible

    public WriteableBitmap Bitmap      => _bitmap;
    public int             SelectedMapId { get; set; }

    public ObservableCollection<MapListItem> Maps { get; } = new();

    private string _currentMapLabel = "— No map loaded —";
    public string CurrentMapLabel
    {
        get => _currentMapLabel;
        private set { _currentMapLabel = value; OnPropertyChanged(); }
    }

    private string _statsLabel = "";
    public string StatsLabel
    {
        get => _statsLabel;
        private set { _statsLabel = value; OnPropertyChanged(); }
    }

    public TileGridViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tileExists = new bool[GridSize * GridSize];
        _tileDone   = new bool[GridSize * GridSize];
        _tileColor  = new uint[GridSize * GridSize];
        _bitmap     = new WriteableBitmap(BitmapSize, BitmapSize, 96, 96, PixelFormats.Bgra32, null);

        Array.Fill(_tileColor, ColorEmpty);
        RenderFullGrid();
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Switch the grid to a new map, clearing all state.</summary>
    public void SetCurrentMap(int mapId, string name)
    {
        lock (_lock)
        {
            SelectedMapId = mapId;
            Array.Clear(_tileExists, 0, _tileExists.Length);
            Array.Clear(_tileDone,   0, _tileDone.Length);
            Array.Fill (_tileColor,  ColorEmpty);
            _doneTiles    = 0;
            _pendingTiles = 0;
        }
        _dispatcher.InvokeAsync(() =>
        {
            CurrentMapLabel = $"[{mapId:D3}]  {name}";
            StatsLabel      = "";
            RenderFullGrid();
        });
    }

    /// <summary>Pre-populate pending tiles from WDT data.</summary>
    public void InitializeTiles(IEnumerable<(int X, int Y)> existingTiles)
    {
        lock (_lock)
        {
            Array.Clear(_tileExists, 0, _tileExists.Length);
            _pendingTiles = 0;
            foreach (var (x, y) in existingTiles)
            {
                int idx = y * GridSize + x;
                if ((uint)idx < (uint)_tileExists.Length)
                {
                    _tileExists[idx] = true;
                    _tileColor[idx]  = ColorPending;
                    _pendingTiles++;
                }
            }
        }
        _dispatcher.InvokeAsync(() => RenderFullGrid());
    }

    /// <summary>Handle a progress event from an extraction service.</summary>
    public void OnTileProgress(TileProgressEvent e)
    {
        if (e.MapId != SelectedMapId) return;

        _dispatcher.InvokeAsync(() =>
        {
            int idx = e.TileY * GridSize + e.TileX;
            if ((uint)idx >= (uint)_tileColor.Length) return;

            uint newColor;
            lock (_lock)
            {
                newColor = e.Status switch
                {
                    TileStatus.Pending    => _tileExists[idx] ? ColorPending : ColorEmpty,
                    TileStatus.Processing => ColorProcessing,
                    TileStatus.Done       => PhaseColor(e.Phase),
                    TileStatus.Failed     => ColorFailed,
                    _                     => ColorEmpty
                };

                if (e.Status == TileStatus.Done && !_tileDone[idx])
                {
                    _tileDone[idx] = true;
                    _doneTiles++;
                }
                _tileColor[idx] = newColor;
            }

            WriteTilePixels(e.TileX, e.TileY, newColor);
            UpdateStats();
        });
    }

    /// <summary>Reset all tiles back to pending/empty (called on Stop).</summary>
    public void ResetAllTiles()
    {
        lock (_lock)
        {
            for (int i = 0; i < _tileColor.Length; i++)
                _tileColor[i] = _tileExists[i] ? ColorPending : ColorEmpty;
            Array.Clear(_tileDone, 0, _tileDone.Length);
            _doneTiles = 0;
        }
        _dispatcher.InvokeAsync(() =>
        {
            RenderFullGrid();
            StatsLabel = "";
        });
    }

    // ── Private ─────────────────────────────────────────────────────────────

    private static uint PhaseColor(ExtractionPhase phase) => phase switch
    {
        ExtractionPhase.Map  => ColorDoneMap,
        ExtractionPhase.Vmap => ColorDoneVmap,
        ExtractionPhase.Road => ColorDoneRoad,
        ExtractionPhase.Mmap => ColorDoneMmap,
        _                    => ColorDoneMmap
    };

    private void RenderFullGrid()
    {
        var buf = new int[BitmapSize * BitmapSize];
        lock (_lock)
        {
            for (int ty = 0; ty < GridSize; ty++)
            {
                for (int tx = 0; tx < GridSize; tx++)
                {
                    uint fill = _tileColor[ty * GridSize + tx];
                    int  px   = tx * CellSize;
                    int  py   = ty * CellSize;

                    for (int dy = 0; dy < CellSize; dy++)
                    {
                        bool lastRow = dy == CellSize - 1;
                        for (int dx = 0; dx < CellSize; dx++)
                        {
                            bool border = lastRow || dx == CellSize - 1;
                            buf[(py + dy) * BitmapSize + (px + dx)] =
                                (int)(border ? ColorBorder : fill);
                        }
                    }
                }
            }
        }
        _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, BitmapSize, BitmapSize),
                            buf, BitmapSize * 4, 0);
    }

    private void WriteTilePixels(int tx, int ty, uint fill)
    {
        int px     = tx * CellSize;
        int py     = ty * CellSize;
        var pixels = new int[CellSize * CellSize];

        for (int dy = 0; dy < CellSize; dy++)
        {
            bool lastRow = dy == CellSize - 1;
            for (int dx = 0; dx < CellSize; dx++)
            {
                bool border = lastRow || dx == CellSize - 1;
                pixels[dy * CellSize + dx] = (int)(border ? ColorBorder : fill);
            }
        }
        _bitmap.WritePixels(new System.Windows.Int32Rect(px, py, CellSize, CellSize),
                            pixels, CellSize * 4, 0);
    }

    private void UpdateStats()
    {
        StatsLabel = _pendingTiles > 0
            ? $"{_doneTiles} / {_pendingTiles}"
            : _doneTiles > 0 ? $"{_doneTiles} tiles" : "";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MapListItem
{
    public int    Id         { get; init; }
    public string Name       { get; init; } = string.Empty;
    public bool   IsSelected { get; set; }
    public override string ToString() => $"[{Id}] {Name}";
}