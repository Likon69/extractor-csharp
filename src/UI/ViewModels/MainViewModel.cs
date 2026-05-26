using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Dbc;
using MaNGOS.Extractor.Formats.Mpq;
using MaNGOS.Extractor.MapExtractor;
using MaNGOS.Extractor.MmapExtractor;
using MaNGOS.Extractor.RoadExtractor;
using MaNGOS.Extractor.UI.Config;
using MaNGOS.Extractor.VmapExtractor;
using Mel = Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.UI.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly TileGridViewModel _tileGrid;
    private readonly string _configPath;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource? _cts;
    private MpqArchiveCollection? _archives;

    public event PropertyChangedEventHandler? PropertyChanged;
    public TileGridViewModel TileGrid => _tileGrid;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand BrowseWowCommand { get; }
    public ICommand BrowseOutputCommand { get; }

    public string[] PhaseNames => new[] { "Toutes", "Map", "Vmap", "Road", "Mmap" };
    public string[] Locales => new[] { "enUS", "enGB", "deDE", "frFR", "esES", "ruRU" };

    public string SelectedPhase { get; set; } = "Toutes";
    public string SelectedLocale { get; set; } = "enUS";

    private string _wowClientPath = @"C:\World of Warcraft";
    public string WowClientPath { get => _wowClientPath; set { _wowClientPath = value; OnPropertyChanged(); } }

    private string _outputPath = @"D:\wow-data\output";
    public string OutputPath { get => _outputPath; set { _outputPath = value; OnPropertyChanged(); } }

    private string _goSpawnsPath = "gameobject_spawns.bin";
    public string GoSpawnsPath { get => _goSpawnsPath; set { _goSpawnsPath = value; OnPropertyChanged(); } }

    public int ThreadCount { get; set; } = 4;

    private bool _isExtracting;
    public bool IsExtracting { get => _isExtracting; private set { _isExtracting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop)); } }
    public bool CanStart => !IsExtracting;
    public bool CanStop => IsExtracting;

    public int ProcessedTiles { get; private set; }
    public string StatusMessage { get; private set; } = "Ready";

    public ObservableCollection<PhaseItem> Phases { get; } = new()
    {
        new PhaseItem { Name = "Map", IsEnabled = true },
        new PhaseItem { Name = "Vmap", IsEnabled = true },
        new PhaseItem { Name = "Road", IsEnabled = false },
        new PhaseItem { Name = "Mmap", IsEnabled = true }
    };

    public ObservableCollection<MapSelectionItem> SelectedMaps { get; } = new();

    public float CellSize { get; set; } = 0.303030f;
    public float CellHeight { get; set; } = 0.2f;
    public float WalkableSlopeAngle { get; set; } = 50.0f;
    public int WalkableHeight { get; set; } = 11;
    public int WalkableRadius { get; set; } = 2;
    public int WalkableClimb { get; set; } = 5;

    public ObservableCollection<LogMessage> LogMessages { get; } = new();

    public MainViewModel(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _tileGrid = new TileGridViewModel();
        _configPath = Path.Combine(AppContext.BaseDirectory, "ExtractorConfig.json");

        StartCommand = new RelayCommand(async _ => await StartExtractionAsync(), _ => CanStart);
        StopCommand = new RelayCommand(_ => StopExtraction(), _ => CanStop);
        BrowseWowCommand = new RelayCommand(_ => BrowsePath(true));
        BrowseOutputCommand = new RelayCommand(_ => BrowsePath(false));

        LoadConfig();
    }

    public MainViewModel() : this(LoggerFactory.Create(b => { })) { }

    private void BrowsePath(bool isWowPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = isWowPath ? "Select WoW Client Directory" : "Select Output Directory",
            InitialDirectory = isWowPath ? WowClientPath : OutputPath
        };
        if (dialog.ShowDialog() == true)
        {
            if (isWowPath) WowClientPath = dialog.FolderName;
            else OutputPath = dialog.FolderName;
        }
    }

    public void LoadConfig()
    {
        var config = ConfigFileManager.Load(_configPath);

        if (config == null)
        {
            WowClientPath = @"C:\World of Warcraft";
            OutputPath = @"D:\wow-data\output";
            GoSpawnsPath = "gameobject_spawns.bin";
            ThreadCount = 4;
            SelectedMaps.Clear();
            SelectedMaps.Add(new MapSelectionItem { MapId = 0, Name = "Azeroth", IsSelected = true });
            SelectedMaps.Add(new MapSelectionItem { MapId = 571, Name = "Northrend", IsSelected = true });
            return;
        }

        WowClientPath = config?.WowClientPath ?? string.Empty;
        OutputPath = config?.OutputPath ?? string.Empty;
        GoSpawnsPath = config?.GoSpawnsPath ?? string.Empty;
        ThreadCount = config.Threads > 0 ? config.Threads : 4;

        if (config?.RecastConfig is { } r)
        {
            CellSize = r.CellSize;
            CellHeight = r.CellHeight;
            WalkableSlopeAngle = r.WalkableSlopeAngle;
            WalkableHeight = r.WalkableHeight;
            WalkableRadius = r.WalkableRadius;
            WalkableClimb = r.WalkableClimb;
        }

        SelectedLocale = config?.Locale ?? "enUS";
        LoadMapSelection(config);
    }

    private void LoadMapSelection(ExtractorConfig? config)
    {
        var selectedIds = config?.SelectedMapIds != null
            ? new HashSet<int>(config.SelectedMapIds)
            : new HashSet<int> { 0, 571 };

        if (_archives != null && _archives.TryReadFile(@"DBFilesClient\Map.dbc", out var mapDbcData))
        {
            try
            {
                var reader = DbcReader<MapDbcRow>.Parse(mapDbcData.Span);
                foreach (var row in reader.Rows)
                {
                    string dir = reader.GetString(row, 1);
                    if (!string.IsNullOrEmpty(dir))
                        SelectedMaps.Add(new MapSelectionItem
                        {
                            MapId = (int)row.Id,
                            Name = dir,
                            IsSelected = selectedIds.Contains((int)row.Id)
                        });
                }
            }
            catch { }
        }

        if (SelectedMaps.Count == 0)
        {
            SelectedMaps.Add(new MapSelectionItem { MapId = 0, Name = "Azeroth", IsSelected = selectedIds.Contains(0) });
            SelectedMaps.Add(new MapSelectionItem { MapId = 1, Name = "Kalimdor", IsSelected = selectedIds.Contains(1) });
            SelectedMaps.Add(new MapSelectionItem { MapId = 530, Name = "Outland", IsSelected = selectedIds.Contains(530) });
            SelectedMaps.Add(new MapSelectionItem { MapId = 571, Name = "Northrend", IsSelected = selectedIds.Contains(571) });
        }
    }

    public async Task StartExtractionAsync()
    {
        if (IsExtracting) return;
        IsExtracting = true;
        _cts = new CancellationTokenSource();

        try
        {
            var maps = SelectedMaps.Where(m => m.IsSelected).ToList();
            if (maps.Count == 0) { AddLog("No maps selected."); return; }

            var enabledPhases = Phases.Where(p => p.IsEnabled).Select(p => p.Name).ToList();
            string mapDir = Path.Combine(OutputPath, "maps");
            string vmapDir = Path.Combine(OutputPath, "vmaps");
            string roadDir = Path.Combine(OutputPath, "road");
            string mmapDir = Path.Combine(OutputPath, "mmaps");

            Directory.CreateDirectory(mapDir);
            Directory.CreateDirectory(vmapDir);
            Directory.CreateDirectory(roadDir);
            Directory.CreateDirectory(mmapDir);

            _archives = MpqArchiveCollection.FromWoWDirectory(WowClientPath, SelectedLocale, _loggerFactory);

            if (SelectedMaps.Count <= 4)
                LoadMapSelection(new ExtractorConfig { SelectedMapIds = maps.Select(m => m.MapId).ToArray() });

            var progress = new Progress<TileProgressEvent>(e => _tileGrid.OnTileProgress(e));
            AddLog($"Starting extraction: {maps.Count} maps, {enabledPhases.Count} phases, {ThreadCount} threads");

            int processed = 0;

            foreach (var map in maps)
            {
                _cts.Token.ThrowIfCancellationRequested();
                string mapName = WowConstants.GetMapDirectory((uint)map.MapId);

                if (enabledPhases.Contains("Map"))
                {
                    AddLog($"[Map] Extracting {mapName}...");
                    int tiles = await new MapExtractorService(_archives, _loggerFactory, mapDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                }
                if (enabledPhases.Contains("Vmap"))
                {
                    AddLog($"[Vmap] Extracting {mapName}...");
                    int tiles = await new VmapExtractorService(_archives, _loggerFactory, vmapDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                }
                if (enabledPhases.Contains("Road"))
                {
                    AddLog($"[Road] Extracting {mapName}...");
                    int tiles = await new RoadExtractorService(_archives, _loggerFactory, roadDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                }
                if (enabledPhases.Contains("Mmap"))
                {
                    AddLog($"[Mmap] Extracting {mapName}...");
                    var recastConfig = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle, WalkableHeight, WalkableRadius, WalkableClimb);
                    int tiles = await new MmapExtractorService(_archives, _loggerFactory, mmapDir, recastConfig, ThreadCount, GoSpawnsPath)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                }

                ProcessedTiles = processed;
            }

            AddLog($"Extraction complete. {processed} tiles processed.");
        }
        catch (OperationCanceledException)
        {
            AddLog("Extraction cancelled.");
        }
        catch (Exception ex)
        {
            AddLog($"Error: {ex.Message}");
        }
        finally
        {
            IsExtracting = false;
            _archives?.Dispose();
            _archives = null;
        }
    }

    public void StopExtraction() { _cts?.Cancel(); StatusMessage = "Stopping..."; }

    public void AddLog(string message, Mel.LogLevel level = Mel.LogLevel.Information)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        LogMessages.Add(new LogMessage($"[{ts}] {message}", level));
        while (LogMessages.Count > 1000) LogMessages.RemoveAt(0);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PhaseItem : INotifyPropertyChanged
{
    private bool _isEnabled = true;
    public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; OnPropertyChanged(); } }
    public string Name { get; init; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class MapSelectionItem : INotifyPropertyChanged
{
    public int MapId { get; init; }
    public string Name { get; init; } = "";
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class LogMessage
{
    public string Text { get; }
    public Mel.LogLevel Level { get; }
    public System.Windows.Media.Brush Color => Level switch
    {
        Mel.LogLevel.Warning => System.Windows.Media.Brushes.Orange,
        Mel.LogLevel.Error => System.Windows.Media.Brushes.Red,
        _ => System.Windows.Media.Brushes.White
    };
    public LogMessage(string text, Mel.LogLevel level) => (Text, Level) = (text, level);
}