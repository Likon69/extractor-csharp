using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
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
    public ICommand BrowseGoSpawnsCommand { get; }
    public ICommand BrowseOffMeshCommand { get; }

    public string SelectedLocale { get; set; } = "enUS";
    public string[] Locales => new[] { "enUS", "enGB", "deDE", "frFR", "esES", "ruRU" };

    private string _wowClientPath = @"C:\World of Warcraft";
    public string WowClientPath
    {
        get => _wowClientPath;
        set { _wowClientPath = value; OnPropertyChanged(); _ = TryReloadMapListAsync(); }
    }

    private string _outputPath = Path.Combine(AppContext.BaseDirectory, "output");
    public string OutputPath { get => _outputPath; set { _outputPath = value; OnPropertyChanged(); } }

    private string _goSpawnsPath = Path.Combine(AppContext.BaseDirectory, "gameobject_spawns.bin");
    public string GoSpawnsPath { get => _goSpawnsPath; set { _goSpawnsPath = value; OnPropertyChanged(); } }

    private string _offMeshPath = Path.Combine(AppContext.BaseDirectory, "offmesh.txt");
    public string OffMeshPath { get => _offMeshPath; set { _offMeshPath = value; OnPropertyChanged(); } }

    public int ThreadCount { get; set; } = 4;

    private bool _isExtracting;
    public bool IsExtracting { get => _isExtracting; private set { _isExtracting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop)); } }
    public bool CanStart => !IsExtracting;
    public bool CanStop => IsExtracting;

    private int _processedTiles;
    public int ProcessedTiles
    {
        get => _processedTiles;
        private set { _processedTiles = value; OnPropertyChanged(); }
    }

    private int _totalTiles;
    public int TotalTiles
    {
        get => _totalTiles;
        private set { _totalTiles = value; OnPropertyChanged(); }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set { _progress = value; OnPropertyChanged(); }
    }
    public string StatusMessage { get; private set; } = "Ready";

    public ObservableCollection<PhaseItem> Phases { get; } = new()
    {
        new PhaseItem { Name = "Map", IsEnabled = true },
        new PhaseItem { Name = "Vmap", IsEnabled = true },
        new PhaseItem { Name = "Road", IsEnabled = true },
        new PhaseItem { Name = "Mmap", IsEnabled = true }
    };

    public ObservableCollection<MapSelectionItem> SelectedMaps { get; } = new();

    // F2: map list selection
    public bool HasMaps => SelectedMaps.Count > 0;

    private bool _selectAllMaps = true;
    public bool SelectAllMaps
    {
        get => _selectAllMaps;
        set
        {
            _selectAllMaps = value;
            OnPropertyChanged();
            foreach (var m in SelectedMaps)
                m.IsSelected = value;
        }
    }

    private void RefreshMapList()
    {
        // Select all by default after loading
        foreach (var m in SelectedMaps)
            m.IsSelected = true;
        _selectAllMaps = true;
        OnPropertyChanged(nameof(SelectAllMaps));
        OnPropertyChanged(nameof(HasMaps));
    }

    public float CellSize { get; set; } = 0.303030f;
    public float CellHeight { get; set; } = 0.2f;
    public float WalkableSlopeAngle { get; set; } = 50.0f;
    public int WalkableHeight { get; set; } = 11;
    public int WalkableRadius { get; set; } = 2;
    public int WalkableClimb { get; set; } = 5;

    private bool _bigBaseUnit;
    public bool BigBaseUnit { get => _bigBaseUnit; set { _bigBaseUnit = value; OnPropertyChanged(); } }

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
        BrowseGoSpawnsCommand = new RelayCommand(_ => BrowseGoSpawns());
        BrowseOffMeshCommand  = new RelayCommand(_ => BrowseOffMesh());
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

    private void BrowseGoSpawns()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select gameobject_spawns.bin",
            Filter = "BIN files (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = string.IsNullOrEmpty(GoSpawnsPath) ? "" : GoSpawnsPath
        };
        if (dialog.ShowDialog() == true)
            GoSpawnsPath = dialog.FileName;
    }

    private void BrowseOffMesh()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select OffMesh File",
            Filter = "TXT files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = string.IsNullOrEmpty(OffMeshPath) ? "" : OffMeshPath
        };
        if (dialog.ShowDialog() == true)
            OffMeshPath = dialog.FileName;
    }

    private async Task TryReloadMapListAsync()
    {
        if (string.IsNullOrEmpty(_wowClientPath) || !Directory.Exists(_wowClientPath))
        {
            AddLog($"Chemin WoW invalide ou introuvable: {_wowClientPath}", LogLevel.Error);
            return;
        }

        var path  = _wowClientPath;
        var locale = SelectedLocale;
        var dataDir = Path.Combine(path, "Data");
        var localeDir = Path.Combine(dataDir, locale);
        AddLog($"Chargement des cartes depuis {path}... (locale={locale})");

        if (!Directory.Exists(dataDir))
        {
            AddLog($"Dossier Data introuvable: {dataDir}", LogLevel.Error);
            return;
        }

        if (!Directory.Exists(localeDir))
        {
            AddLog($"Dossier locale introuvable: {localeDir}. Locale={locale}", LogLevel.Warning);
        }

        var sel = SelectedMaps.Where(m => m.IsSelected).Select(m => m.MapId).ToHashSet();

        List<MapSelectionItem> loaded;
        List<string> diag = new();
        try
        {
            loaded = await Task.Run(() =>
            {
                using var archives = MpqArchiveCollection.FromWoWDirectory(path, locale, _loggerFactory);
                diag.Add($"{archives.ArchiveCount} archives MPQ ouvertes.");

                ReadOnlyMemory<byte> data = default;
                var mapDbcCandidates = new[]
                {
                    "DBFilesClient\\Map.dbc",
                    "DBFilesClient\\map.dbc",
                    "dbfilesclient\\map.dbc",
                    "DBFilesClient/Map.dbc",
                    "Map.dbc"
                };

                string? selectedCandidate = null;
                foreach (var candidate in mapDbcCandidates)
                {
                    if (!archives.TryReadFile(candidate, out data))
                        continue;

                    selectedCandidate = candidate;
                    break;
                }

                if (selectedCandidate == null)
                {
                    diag.Add($"Map.dbc introuvable dans les archives MPQ.");
                    foreach (var candidate in mapDbcCandidates)
                        diag.Add($"  test {candidate}: exists={archives.FileExists(candidate)}");
                    return new List<MapSelectionItem>();
                }

                diag.Add($"Map.dbc trouvé via: {selectedCandidate}");

                var reader = DbcReader<MapDbcRow>.Parse(data.Span);
                diag.Add($"Map.dbc: {reader.RowCount} entrées.");

                var result = new List<MapSelectionItem>();
                foreach (var row in reader.Rows)
                {
                    string dir = reader.GetString(row, 1);
                    if (!string.IsNullOrEmpty(dir))
                        result.Add(new MapSelectionItem
                        {
                            MapId     = (int)row.Id,
                            Name      = dir,
                            MapType   = reader.GetInt32(row, 2),
                            IsSelected = sel.Count == 0 || sel.Contains((int)row.Id)
                        });
                }
                return result;
            });
        }
        catch (Exception ex)
        {
            AddLog($"Erreur [{ex.GetType().Name}]: {ex.Message}", LogLevel.Error);
            if (ex.InnerException != null)
                AddLog($"  Cause: {ex.InnerException.Message}", LogLevel.Error);
            return;
        }

        foreach (var d in diag)
            AddLog(d);

        SelectedMaps.Clear();
        foreach (var item in loaded)
            SelectedMaps.Add(item);

        AddLog($"{loaded.Count} cartes chargées depuis Map.dbc");
        RefreshMapList();
    }

    public void LoadConfig()
    {
        var config = ConfigFileManager.Load(_configPath);

        if (config == null)
        {
            WowClientPath = @"C:\World of Warcraft";
            OutputPath = Path.Combine(AppContext.BaseDirectory, "output");
            GoSpawnsPath = Path.Combine(AppContext.BaseDirectory, "gameobject_spawns.bin");
            ThreadCount = 4;
            return;
        }

        WowClientPath = config.WowClientPath ?? string.Empty;
        OutputPath    = config.OutputPath    ?? string.Empty;
        GoSpawnsPath  = config.GoSpawnsPath  ?? Path.Combine(AppContext.BaseDirectory, "gameobject_spawns.bin");
        OffMeshPath   = config.OffMeshPath   ?? Path.Combine(AppContext.BaseDirectory, "offmesh.txt");
        ThreadCount   = config.Threads > 0   ? config.Threads : 4;
        BigBaseUnit   = config.BigBaseUnit;

        if (config.RecastConfig is { } r)
        {
            CellSize = r.CellSize;
            CellHeight = r.CellHeight;
            WalkableSlopeAngle = r.WalkableSlopeAngle;
            WalkableHeight = r.WalkableHeight;
            WalkableRadius = r.WalkableRadius;
            WalkableClimb = r.WalkableClimb;
        }

        SelectedLocale = config.Locale ?? "enUS";
        // TryReloadMapList is called by the WowClientPath setter above.
        // Always start with all maps selected (dropdown index 0).
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
            string roadDir = Path.Combine(OutputPath, "roadmaps");
            string mmapDir = Path.Combine(OutputPath, "mmaps");

            Directory.CreateDirectory(mapDir);
            Directory.CreateDirectory(vmapDir);
            Directory.CreateDirectory(roadDir);
            Directory.CreateDirectory(mmapDir);

            _archives = await Task.Run(() =>
                MpqArchiveCollection.FromWoWDirectory(WowClientPath, SelectedLocale, _loggerFactory));

            // Load area flags for correct Recast area types
            AdtFile.LoadAreaTable(_archives);
            AdtFile.LoadLiquidTypeTable(_archives);

            var progress = new Progress<TileProgressEvent>(e => _tileGrid.OnTileProgress(e));
            AddLog($"Starting extraction: {maps.Count} maps, {enabledPhases.Count} phases, {ThreadCount} threads");

            int processed = 0;

            // Estimate total tiles for progress (rough: each map has at most 4096 tiles)
            TotalTiles = maps.Sum(m => Math.Max(1, 4096 / maps.Count)) * enabledPhases.Count;
            foreach (var map in maps)
            {
                _cts.Token.ThrowIfCancellationRequested();
                string mapName = map.Name;

                if (enabledPhases.Contains("Map"))
                {
                    AddLog($"[Map] Extracting {mapName}...");
                    int tiles = await new MapExtractorService(_archives, _loggerFactory, mapDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    ProcessedTiles = processed;
                    Progress = TotalTiles > 0 ? processed * 100.0 / TotalTiles : 0;
                }
                if (enabledPhases.Contains("Vmap"))
                {
                    AddLog($"[Vmap] Extracting {mapName}...");
                    int tiles = await new VmapExtractorService(_archives, _loggerFactory, vmapDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    ProcessedTiles = processed;
                    Progress = TotalTiles > 0 ? processed * 100.0 / TotalTiles : 0;
                }
                if (enabledPhases.Contains("Road"))
                {
                    AddLog($"[Road] Extracting {mapName}...");
                    int tiles = await new RoadExtractorService(_archives, _loggerFactory, roadDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    ProcessedTiles = processed;
                    Progress = TotalTiles > 0 ? processed * 100.0 / TotalTiles : 0;
                }
                if (enabledPhases.Contains("Mmap"))
                {
                    AddLog($"[Mmap] Extracting {mapName}...");
                    var recastConfig = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle, WalkableHeight, WalkableRadius, WalkableClimb);
                    int tiles = await new MmapExtractorService(_archives, _loggerFactory, mmapDir, recastConfig, ThreadCount, GoSpawnsPath, OffMeshPath)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token);
                    processed += tiles;
                    ProcessedTiles = processed;
                    Progress = TotalTiles > 0 ? processed * 100.0 / TotalTiles : 0;
                }
            }
            Progress = 100;

            AddLog($"Extraction complete. {processed} tiles processed.");
        }
        catch (OperationCanceledException)
        {
            AddLog("Extraction cancelled.");
        }
        catch (Exception ex)
        {
            AddLog($"Error [{ex.GetType().Name}]: {ex.Message}", LogLevel.Error);
            AddLog(ex.StackTrace ?? "(no stack trace)", LogLevel.Error);
        }
        finally
        {
            IsExtracting = false;
            _archives?.Dispose();
            _archives = null;
        }
    }


    public void SaveConfig()
    {
        var config = new ExtractorConfig
        {
            WowClientPath = WowClientPath,
            OutputPath = OutputPath,
            GoSpawnsPath = GoSpawnsPath,
            OffMeshPath = OffMeshPath,
            Locale = SelectedLocale,
            EnabledPhases = Phases.Where(p => p.IsEnabled).Select(p => p.Name).ToArray(),
            Maps = SelectedMaps.Where(m => m.IsSelected).Select(m => m.MapId).ToArray(),
            Threads = ThreadCount,
            BigBaseUnit = BigBaseUnit,
            RecastConfig = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle, WalkableHeight, WalkableRadius, WalkableClimb)
        };
        ConfigFileManager.Save(_configPath, config);
    }
    public void StopExtraction()
    {
        _cts?.Cancel();
        _tileGrid?.ResetAllTiles();
        StatusMessage = "Stopping...";
    }

    public void AddLog(string message, Mel.LogLevel level = Mel.LogLevel.Information)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        LogMessages.Add(new LogMessage($"[{ts}] {message}", level));
        FileLog.Write(message, level);
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
    public int MapType { get; init; }
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