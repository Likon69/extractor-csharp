using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Mpq;
using MaNGOS.Extractor.MapExtractor;
using MaNGOS.Extractor.MmapExtractor;
using MaNGOS.Extractor.RoadExtractor;
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

    private string _selectedPhase = "Toutes";
    public string SelectedPhase
    {
        get => _selectedPhase;
        set { _selectedPhase = value; OnPropertyChanged(); UpdatePhaseEnabled(); }
    }

    private string _selectedLocale = "enUS";
    public string SelectedLocale
    {
        get => _selectedLocale;
        set { _selectedLocale = value; OnPropertyChanged(); }
    }

    private string _wowClientPath = @"C:\World of Warcraft";
    public string WowClientPath
    {
        get => _wowClientPath;
        set { _wowClientPath = value; OnPropertyChanged(); }
    }

    private string _outputPath = @"D:\wow-data\output";
    public string OutputPath
    {
        get => _outputPath;
        set { _outputPath = value; OnPropertyChanged(); }
    }

    private string _goSpawnsPath = "gameobject_spawns.bin";
    public string GoSpawnsPath
    {
        get => _goSpawnsPath;
        set { _goSpawnsPath = value; OnPropertyChanged(); }
    }

    private int _threadCount = 4;
    public int ThreadCount
    {
        get => _threadCount;
        set { _threadCount = Math.Max(1, Math.Min(32, value)); OnPropertyChanged(); }
    }

    private bool _bigBaseUnit;
    public bool BigBaseUnit
    {
        get => _bigBaseUnit;
        set { _bigBaseUnit = value; OnPropertyChanged(); }
    }

    private bool _isExtracting;
    public bool IsExtracting
    {
        get => _isExtracting;
        private set { _isExtracting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop)); }
    }

    public bool CanStart => !IsExtracting;
    public bool CanStop => IsExtracting;

    private int _totalTiles;
    public int TotalTiles
    {
        get => _totalTiles;
        private set { _totalTiles = value; OnPropertyChanged(); }
    }

    private int _processedTiles;
    public int ProcessedTiles
    {
        get => _processedTiles;
        private set { _processedTiles = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress)); }
    }

    public double Progress => TotalTiles > 0 ? (double)ProcessedTiles / TotalTiles * 100 : 0;

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ObservableCollection<PhaseItem> Phases { get; } = new()
    {
        new PhaseItem { Name = "Map", IsEnabled = true },
        new PhaseItem { Name = "Vmap", IsEnabled = true },
        new PhaseItem { Name = "Road", IsEnabled = false },
        new PhaseItem { Name = "Mmap", IsEnabled = true }
    };

    public ObservableCollection<MapSelectionItem> SelectedMaps { get; } = new()
    {
        new MapSelectionItem { MapId = 0, Name = "Azeroth", IsSelected = true },
        new MapSelectionItem { MapId = 1, Name = "Kalimdor", IsSelected = false },
        new MapSelectionItem { MapId = 530, Name = "Outland", IsSelected = false },
        new MapSelectionItem { MapId = 571, Name = "Northrend", IsSelected = true }
    };

    private float _cellSize = 0.303030f;
    public float CellSize
    {
        get => _cellSize;
        set { _cellSize = value; OnPropertyChanged(); }
    }

    private float _cellHeight = 0.2f;
    public float CellHeight
    {
        get => _cellHeight;
        set { _cellHeight = value; OnPropertyChanged(); }
    }

    private float _walkableSlopeAngle = 50.0f;
    public float WalkableSlopeAngle
    {
        get => _walkableSlopeAngle;
        set { _walkableSlopeAngle = value; OnPropertyChanged(); }
    }

    private int _walkableHeight = 11;
    public int WalkableHeight
    {
        get => _walkableHeight;
        set { _walkableHeight = value; OnPropertyChanged(); }
    }

    private int _walkableRadius = 2;
    public int WalkableRadius
    {
        get => _walkableRadius;
        set { _walkableRadius = value; OnPropertyChanged(); }
    }

    private int _walkableClimb = 5;
    public int WalkableClimb
    {
        get => _walkableClimb;
        set { _walkableClimb = value; OnPropertyChanged(); }
    }

    public ObservableCollection<LogMessage> LogMessages { get; } = new();

    public MainViewModel() : this(ObservableLoggerProvider.Instance)
    {
    }

    public MainViewModel(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _tileGrid = new TileGridViewModel();
        _configPath = Path.Combine(AppContext.BaseDirectory, "ExtractorConfig.json");

        if (_loggerFactory is ObservableLoggerProvider obs)
        {
            obs.Entries.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (LogEntry entry in e.NewItems)
                        LogMessages.Add(new LogMessage(entry.Message, entry.Level));
            };
            foreach (var entry in obs.Entries)
                LogMessages.Add(new LogMessage(entry.Message, entry.Level));
        }

        StartCommand = new RelayCommand(async _ => await StartExtractionAsync(), _ => CanStart);
        StopCommand = new RelayCommand(_ => StopExtraction(), _ => CanStop);
        BrowseWowCommand = new RelayCommand(_ => BrowsePath(true));
        BrowseOutputCommand = new RelayCommand(_ => BrowsePath(false));

        LoadConfig();
    }

    private void BrowsePath(bool isWowPath)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = isWowPath ? "Select WoW Client Directory" : "Select Output Directory",
            InitialDirectory = isWowPath ? WowClientPath : OutputPath
        };

        if (dialog.ShowDialog() == true)
        {
            if (isWowPath)
                WowClientPath = dialog.FolderName;
            else
                OutputPath = dialog.FolderName;
        }
    }

    private void UpdatePhaseEnabled()
    {
        bool allEnabled = SelectedPhase == "Toutes";
        foreach (var phase in Phases)
            phase.IsEnabled = allEnabled || phase.Name == SelectedPhase;
    }

    public void LoadConfig()
    {
        if (!File.Exists(_configPath))
            return;

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<ExtractorConfig>(json);
            if (config == null) return;

            WowClientPath = config.WowClientPath ?? string.Empty;
            OutputPath = config.OutputPath ?? string.Empty;
            GoSpawnsPath = config.GoSpawnsPath ?? string.Empty;
            ThreadCount = config.Threads;
            BigBaseUnit = config.BigBaseUnit;

            if (config.RecastConfig.CellSize > 0)
            {
                CellSize = config.RecastConfig.CellSize;
                CellHeight = config.RecastConfig.CellHeight;
                WalkableSlopeAngle = config.RecastConfig.WalkableSlopeAngle;
                WalkableHeight = config.RecastConfig.WalkableHeight;
                WalkableRadius = config.RecastConfig.WalkableRadius;
                WalkableClimb = config.RecastConfig.WalkableClimb;
            }

            if (!string.IsNullOrEmpty(config.Locale))
                SelectedLocale = config.Locale;

            AddLog("Configuration loaded.");
        }
        catch (Exception ex)
        {
            AddLog($"Failed to load config: {ex.Message}");
        }
    }

    public void SaveConfig()
    {
        var config = new ExtractorConfig
        {
            WowClientPath = WowClientPath,
            OutputPath = OutputPath,
            GoSpawnsPath = GoSpawnsPath,
            EnabledPhases = Phases.Where(p => p.IsEnabled).Select(p => p.Name).ToArray(),
            SelectedMapIds = SelectedMaps.Where(m => m.IsSelected).Select(m => m.MapId).ToArray(),
            Threads = ThreadCount,
            BigBaseUnit = BigBaseUnit,
            Locale = SelectedLocale,
            RecastConfig = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle,
                WalkableHeight, WalkableRadius, WalkableClimb)
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);

        AddLog("Configuration saved.");
    }

    public async Task StartExtractionAsync()
    {
        if (IsExtracting)
            return;

        IsExtracting = true;
        _cts = new CancellationTokenSource();

        try
        {
            var maps = SelectedMaps.Where(m => m.IsSelected).ToList();
            if (maps.Count == 0)
            {
                AddLog("No maps selected.");
                return;
            }

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
                    var service = new MapExtractorService(_archives, _loggerFactory, mapDir);
                    int tiles = await service.ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    AddLog($"[Map] {tiles} tiles extracted for {mapName}");
                }

                if (enabledPhases.Contains("Vmap"))
                {
                    AddLog($"[Vmap] Extracting {mapName}...");
                    var service = new VmapExtractorService(_archives, _loggerFactory, vmapDir);
                    int tiles = await service.ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    AddLog($"[Vmap] {tiles} tiles extracted for {mapName}");
                }

                if (enabledPhases.Contains("Road"))
                {
                    AddLog($"[Road] Extracting {mapName}...");
                    var service = new RoadExtractorService(_archives, _loggerFactory, roadDir);
                    int tiles = await service.ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    AddLog($"[Road] {tiles} tiles extracted for {mapName}");
                }

                if (enabledPhases.Contains("Mmap"))
                {
                    AddLog($"[Mmap] Extracting {mapName}...");
                    var recastConfig = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle,
                        WalkableHeight, WalkableRadius, WalkableClimb);
                    var service = new MmapExtractorService(_archives, _loggerFactory, mmapDir, recastConfig, ThreadCount);
                    int tiles = await service.ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    AddLog($"[Mmap] {tiles} tiles extracted for {mapName}");
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

    public void StopExtraction()
    {
        _cts?.Cancel();
        StatusMessage = "Stopping...";
    }

    public void AddLog(string message, Mel.LogLevel level = Mel.LogLevel.Information)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogMessages.Add(new LogMessage($"[{timestamp}] {message}", level));

        while (LogMessages.Count > 1000)
            LogMessages.RemoveAt(0);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class PhaseItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled))); }
    }

    public string Name { get; init; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MapSelectionItem : INotifyPropertyChanged
{
    public int MapId { get; init; }
    public string Name { get; init; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LogMessage
{
    public string Text { get; }
    public Mel.LogLevel Level { get; }

    public System.Windows.Media.Brush Color => Level switch
    {
        Mel.LogLevel.Warning => System.Windows.Media.Brushes.Orange,
        Mel.LogLevel.Error => System.Windows.Media.Brushes.Red,
        Mel.LogLevel.Critical => System.Windows.Media.Brushes.DarkRed,
        _ => System.Windows.Media.Brushes.White
    };

    public LogMessage(string text, Mel.LogLevel level) => (Text, Level) = (text, level);
}