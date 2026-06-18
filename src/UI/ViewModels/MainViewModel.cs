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
using MaNGOS.Extractor.Formats.Wdt;
using MaNGOS.Extractor.MapExtractor;
using MaNGOS.Extractor.MmapExtractor;
using MaNGOS.Extractor.RoadExtractor;
using MaNGOS.Extractor.DbcExtractor;
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
    private HashSet<int>? _savedMapIds;   // restored from config on startup
    private bool _suppressMapSelectionChanged;
    private bool _isLoadingConfig;
    private CancellationTokenSource? _metricsCts;
    private Task? _metricsTask;
    private DateTime _totalStartUtc;
    private DateTime _mapStartUtc;
    private int _currentMapId = -1;
    private int _currentMapProcessedTiles;
    private int _currentMapTotalTiles;
    private Dictionary<int, int> _mapTileCountByMapId = new();

    // Window geometry (persisted)
    public double WindowLeft   { get; set; } = double.NaN;
    public double WindowTop    { get; set; } = double.NaN;
    public double WindowWidth  { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;

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

    public int ThreadCount
    {
        get => _threadCount;
        set { _threadCount = Math.Clamp(value, 1, MaxThreadCount); OnPropertyChanged(); SaveConfig(); }
    }
    private int _threadCount = 1;
    public int MaxThreadCount { get; } = 20;
    public bool SingleTileEnabled { get; set; }
    public int SingleTileX { get; set; }
    public int SingleTileY { get; set; }

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

    private string _etaTotal = "--:--:--";
    public string EtaTotal
    {
        get => _etaTotal;
        private set { _etaTotal = value; OnPropertyChanged(); }
    }

    private string _etaCurrentMap = "--:--:--";
    public string EtaCurrentMap
    {
        get => _etaCurrentMap;
        private set { _etaCurrentMap = value; OnPropertyChanged(); }
    }

    private string _generatedSizeText = "0.00 MB";
    public string GeneratedSizeText
    {
        get => _generatedSizeText;
        private set { _generatedSizeText = value; OnPropertyChanged(); }
    }

    public string[] LogFilters => new[] { "All", "Info", "Warning", "Error" };

    private string _selectedLogFilter = "All";
    public string SelectedLogFilter
    {
        get => _selectedLogFilter;
        set
        {
            if (_selectedLogFilter == value)
                return;
            _selectedLogFilter = value;
            OnPropertyChanged();
        }
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
            _suppressMapSelectionChanged = true;
            foreach (var m in SelectedMaps)
                m.IsSelected = value;
            _suppressMapSelectionChanged = false;
        }
    }

    private void RefreshMapList()
    {
        // Recompute the SelectAllMaps checkbox — do NOT force-select all.
        _selectAllMaps = SelectedMaps.Count > 0 && SelectedMaps.All(m => m.IsSelected);
        OnPropertyChanged(nameof(SelectAllMaps));
        OnPropertyChanged(nameof(HasMaps));
    }

    private void OnMapSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressMapSelectionChanged) return;
        if (e.PropertyName == nameof(MapSelectionItem.IsSelected))
        {
            _selectAllMaps = SelectedMaps.Count > 0 && SelectedMaps.All(m => m.IsSelected);
            OnPropertyChanged(nameof(SelectAllMaps));
        }
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

    private Action<string, LogLevel>? _logCallback;
    private DateTime _lastUiLog = DateTime.MinValue;

    public MainViewModel()
    {
        _logCallback = (msg, lvl) =>
        {
            // Always write to file, but throttle UI updates to avoid freeze
            FileLog.Write(msg, lvl);
            if (lvl >= LogLevel.Error || (DateTime.UtcNow - _lastUiLog).TotalMilliseconds >= 200)
            {
                _lastUiLog = DateTime.UtcNow;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => AddLog(msg, lvl));
            }
        };
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(new FileLogLoggerProvider(_logCallback)));
        _tileGrid = new TileGridViewModel();
        _configPath = Path.Combine(AppContext.BaseDirectory, "ExtractorConfig.json");

        StartCommand = new RelayCommand(async _ => await StartExtractionAsync(), _ => CanStart);
        StopCommand  = new RelayCommand(_ => StopExtraction(), _ => CanStop);
        BrowseWowCommand    = new RelayCommand(_ => BrowsePath(true));
        BrowseOutputCommand = new RelayCommand(_ => BrowsePath(false));
        BrowseGoSpawnsCommand = new RelayCommand(_ => BrowseGoSpawns());
        BrowseOffMeshCommand  = new RelayCommand(_ => BrowseOffMesh());

        // Auto-save config when any phase checkbox is toggled.
        foreach (var phase in Phases)
            phase.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhaseItem.IsEnabled))
                    SaveConfig();
            };

        LoadConfig();
    }

    // Keep for designer/XAML preview
    // public MainViewModel() : this(...) is the real constructor above

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

        // Preserve current in-memory selections; fall back to saved config IDs on first load.
        var sel = SelectedMaps.Count > 0
            ? SelectedMaps.Where(m => m.IsSelected).Select(m => m.MapId).ToHashSet()
            : _savedMapIds ?? new HashSet<int>();

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

        // Unsubscribe old items, clear, then add new ones and subscribe.
        foreach (var item in SelectedMaps)
            item.PropertyChanged -= OnMapSelectionChanged;
        SelectedMaps.Clear();
        foreach (var item in loaded)
        {
            SelectedMaps.Add(item);
            item.PropertyChanged += OnMapSelectionChanged;
        }

        AddLog($"{loaded.Count} cartes chargées depuis Map.dbc");
        RefreshMapList();
    }

    public void LoadConfig()
    {
        _isLoadingConfig = true;
        try
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
            SingleTileEnabled = config.SingleTileEnabled;
            SingleTileX = config.SingleTileX;
            SingleTileY = config.SingleTileY;

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

        // Restore enabled phases.
        if (config.EnabledPhases is { Length: > 0 } savedPhases)
        {
            var enabledSet = savedPhases.ToHashSet();
            foreach (var phase in Phases)
                phase.IsEnabled = enabledSet.Contains(phase.Name);
        }

        // Restore window geometry.
        WindowLeft   = config.WindowLeft;
        WindowTop    = config.WindowTop;
        WindowWidth  = config.WindowWidth  > 200 ? config.WindowWidth  : 1000;
        WindowHeight = config.WindowHeight > 200 ? config.WindowHeight : 700;

        // Store saved map-ID set so TryReloadMapListAsync can restore selections.
        if (config.Maps is { Length: > 0 } savedMaps)
            _savedMapIds = savedMaps.ToHashSet();
        // TryReloadMapList is called by the WowClientPath setter above.
        } // end try
        finally
        {
            _isLoadingConfig = false;
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
            string roadDir = Path.Combine(OutputPath, "roadmaps");
            string mmapDir = Path.Combine(OutputPath, "mmaps");

            Directory.CreateDirectory(mapDir);
            Directory.CreateDirectory(roadDir);
            Directory.CreateDirectory(mmapDir);

            _archives = await Task.Run(() =>
                MpqArchiveCollection.FromWoWDirectory(WowClientPath, SelectedLocale, _loggerFactory));

            // Load area flags for correct Recast area types
            AdtFile.LoadAreaTable(_archives);
            AdtFile.LoadLiquidTypeTable(_archives);

            AddLog($"Starting extraction: {maps.Count} maps, {enabledPhases.Count} phases, {ThreadCount} threads");
            int? onlyTileX = SingleTileEnabled ? SingleTileX : null;
            int? onlyTileY = SingleTileEnabled ? SingleTileY : null;

            _totalStartUtc = DateTime.UtcNow;
            _mapStartUtc = _totalStartUtc;

            _mapTileCountByMapId = await ComputeTileCountsByMapAsync(maps, enabledPhases.Count, onlyTileX, onlyTileY, _cts.Token);
            TotalTiles = _mapTileCountByMapId.Values.Sum();
            ProcessedTiles = 0;
            Progress = 0;
            EtaTotal = "--:--:--";
            EtaCurrentMap = "--:--:--";

            _metricsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _metricsTask = RunOutputMetricsLoopAsync(_metricsCts.Token);

            var completedTiles = new HashSet<long>();
            var progress = new Progress<TileProgressEvent>(e =>
            {
                _tileGrid.OnTileProgress(e);

                if (e.Status is not (TileStatus.Done or TileStatus.Failed))
                    return;

                if (!completedTiles.Add(CreateTileKey(e.MapId, e.TileX, e.TileY, e.Phase)))
                    return;

                ProcessedTiles = completedTiles.Count;
                if (e.MapId == _currentMapId)
                    _currentMapProcessedTiles++;

                Progress = TotalTiles > 0
                    ? Math.Min(100.0, ProcessedTiles * 100.0 / TotalTiles)
                    : 0;
                UpdateEtaValues();
            });

            int successfulTiles = 0;

            // Single vmap service for the whole phase — the dedup HashSet of
            // .vmo/.vmd files (already written during per-map MDDF/MODF
            // processing) is shared with the global GameObjectDisplayInfo pass.
            MangosVmapExtractorService? vmapSvc = null;
            if (enabledPhases.Contains("Vmap"))
            {
                // The vmap-extractor manages Buildings/ and vmaps/ internally as
                // siblings under the output root — matching the C++ original layout
                // where szWorkDirWmo="./Buildings" and outDir=output_path+"/vmaps".
                // Pass the root OutputPath, NOT a "vmaps" subdirectory.
                vmapSvc = new MangosVmapExtractorService(_archives, _loggerFactory, OutputPath);
            }

            // C++ map-extractor (System.cpp:79): CONF_extract = EXTRACT_MAP | EXTRACT_DBC.
            // The same console extracts DBC files before maps. We mirror this:
            // whenever the Map phase is enabled, extract all DBC/DB2 files once
            // before the per-map loop. This matches the original 3.3.5a behavior
            // where running map-extractor.exe produces both dbc/ and maps/.
            if (enabledPhases.Contains("Map"))
            {
                string dbcDir = Path.Combine(OutputPath, "dbc");
                AddLog("[Dbc] Extracting DBC/DB2 files...");
                var dbcSvc = new DbcExtractorService(_archives, _loggerFactory, dbcDir);
                int dbcCount = await dbcSvc.ExtractAsync(SelectedLocale, _cts.Token);
                AddLog($"[Dbc] {dbcCount} DBC/DB2 files extracted.");
            }

            foreach (var map in maps)
            {
                _cts.Token.ThrowIfCancellationRequested();
                string mapName = map.Name;
                _currentMapId = map.MapId;
                _currentMapProcessedTiles = 0;
                _currentMapTotalTiles = _mapTileCountByMapId.TryGetValue(map.MapId, out var mapTotal) ? mapTotal : 0;
                _mapStartUtc = DateTime.UtcNow;
                UpdateEtaValues();

                // Update the tile grid to show this map's progress.
                _tileGrid.SetCurrentMap(map.MapId, mapName);

                if (enabledPhases.Contains("Map"))
                {
                    AddLog($"[Map] Extracting {mapName}...");
                    int tiles = await new MapExtractorService(_archives, _loggerFactory, mapDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token, onlyTileX, onlyTileY);
                    successfulTiles += tiles;
                }
                if (enabledPhases.Contains("Vmap") && vmapSvc != null)
                {
                    AddLog($"[Vmap] Extracting {mapName}...");
                    int tiles = await vmapSvc.ExtractMapAsync((uint)map.MapId, mapName, _cts.Token, onlyTileX, onlyTileY);
                    successfulTiles += tiles;
                }
                if (enabledPhases.Contains("Road"))
                {
                    AddLog($"[Road] Extracting {mapName}...");
                    int tiles = await new RoadExtractorService(_archives, _loggerFactory, roadDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token, onlyTileX, onlyTileY);
                    successfulTiles += tiles;
                }
                if (enabledPhases.Contains("Mmap"))
                {
                    AddLog($"[Mmap] Extracting {mapName}...");
                    var recastConfig = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle, WalkableHeight, WalkableRadius, WalkableClimb);
                    // Mangos-faithful pipeline: terrain is read from .map files
                    // (Map phase output), buildings from .vmtile + .vmo/.vmd (Vmap
                    // phase output), gameobject collision from .vmo/.vmd files
                    // built from the DBC + gameobject_spawns.bin entries.
                    int tiles = await new MmapExtractorService(_archives, _loggerFactory, mmapDir, recastConfig, ThreadCount, GoSpawnsPath, OffMeshPath, roadDir, OutputPath, mapDir)
                        .ExtractMapAsync((uint)map.MapId, mapName, progress, _cts.Token, onlyTileX, onlyTileY);
                    successfulTiles += tiles;
                }
            }

            // After all per-map passes are done, extract the GameObject model
            // .vmo/.vmd collision meshes (mirrors C++ ExtractGameobjectModels
            // from the vmap-export tool, minus the temp_gameobject_models
            // index file). The dedup set ensures models that were already
            // built during the per-map MDDF/MODF pass are not rebuilt.
            // gameobject_spawns.bin itself is user-provided.
            if (vmapSvc != null)
            {
                AddLog("[Vmap] Extracting GameObject model .vmo/.vmd collision meshes...");
                int goBuilt = await vmapSvc.ExtractGameObjectModelsAsync(_cts.Token);
                AddLog($"[Vmap] {goBuilt} GameObject .vmo/.vmd built.");
            }

            if (TotalTiles > 0 && ProcessedTiles >= TotalTiles)
                Progress = 100;

            EtaTotal = "00:00:00";
            EtaCurrentMap = "00:00:00";

            AddLog($"Extraction complete. {successfulTiles} tiles successful, {ProcessedTiles}/{TotalTiles} tiles finalized.");
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

            if (_metricsCts != null)
            {
                _metricsCts.Cancel();
                _metricsCts.Dispose();
                _metricsCts = null;
            }

            if (_metricsTask != null)
            {
                try { await _metricsTask; }
                catch (OperationCanceledException) { }
                _metricsTask = null;
            }

            _archives?.Dispose();
            _archives = null;
        }
    }


    public void SaveConfig()
    {
        if (_isLoadingConfig) return;
        var config = new ExtractorConfig
        {
            WowClientPath = WowClientPath,
            OutputPath    = OutputPath,
            GoSpawnsPath  = GoSpawnsPath,
            OffMeshPath   = OffMeshPath,
            Locale        = SelectedLocale,
            EnabledPhases = Phases.Where(p => p.IsEnabled).Select(p => p.Name).ToArray(),
            Maps          = SelectedMaps.Where(m => m.IsSelected).Select(m => m.MapId).ToArray(),
            Threads       = ThreadCount,
            BigBaseUnit   = BigBaseUnit,
            SingleTileEnabled = SingleTileEnabled,
            SingleTileX   = SingleTileX,
            SingleTileY   = SingleTileY,
            RecastConfig  = new RecastConfig(CellSize, CellHeight, WalkableSlopeAngle, WalkableHeight, WalkableRadius, WalkableClimb),
            WindowLeft    = double.IsNaN(WindowLeft)  ? 0.0  : WindowLeft,
            WindowTop     = double.IsNaN(WindowTop)   ? 0.0  : WindowTop,
            WindowWidth   = WindowWidth,
            WindowHeight  = WindowHeight
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

    private async Task<Dictionary<int, int>> ComputeTileCountsByMapAsync(
        List<MapSelectionItem> maps,
        int enabledPhaseCount,
        int? onlyTileX,
        int? onlyTileY,
        CancellationToken ct)
    {
        if (_archives == null || enabledPhaseCount <= 0 || maps.Count == 0)
            return new Dictionary<int, int>();

        var wdt = new WdtReader(_archives);
        var result = new Dictionary<int, int>(maps.Count);

        foreach (var map in maps)
        {
            ct.ThrowIfCancellationRequested();

            if (!await wdt.LoadAsync(map.Name, ct))
            {
                AddLog($"[Progress] WDT introuvable pour {map.Name}; total ignoré pour cette map.", LogLevel.Warning);
                result[map.MapId] = 0;
                continue;
            }

            var tiles = wdt.GetExistingTiles();
            int perPhaseCount;
            if (onlyTileX.HasValue && onlyTileY.HasValue)
                perPhaseCount = tiles.Count(t => t.X == onlyTileX.Value && t.Y == onlyTileY.Value);
            else
                perPhaseCount = tiles.Count;

            result[map.MapId] = perPhaseCount * enabledPhaseCount;
        }

        return result;
    }

    private async Task RunOutputMetricsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                long bytes = GetDirectorySizeSafe(OutputPath);
                GeneratedSizeText = $"{bytes / (1024d * 1024d):F2} MB";
            }
            catch
            {
                // Ignore transient IO errors while files are being written.
            }

            await Task.Delay(1000, ct);
        }
    }

    private static long GetDirectorySizeSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return 0;

        long total = 0;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(path, "*", opts))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // File can disappear between enumerate/read during extraction.
            }
        }

        return total;
    }

    private void UpdateEtaValues()
    {
        EtaTotal = ComputeEta(_totalStartUtc, ProcessedTiles, TotalTiles);
        EtaCurrentMap = ComputeEta(_mapStartUtc, _currentMapProcessedTiles, _currentMapTotalTiles);
    }

    private static string ComputeEta(DateTime startedUtc, int done, int total)
    {
        if (total <= 0)
            return "--:--:--";
        if (done >= total)
            return "00:00:00";

        double elapsedSeconds = Math.Max(0.0, (DateTime.UtcNow - startedUtc).TotalSeconds);
        if (done <= 0 || elapsedSeconds < 1.0)
            return "--:--:--";

        double rate = done / elapsedSeconds;
        if (rate <= 0)
            return "--:--:--";

        double remainingSeconds = (total - done) / rate;
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) || remainingSeconds < 0)
            return "--:--:--";

        return TimeSpan.FromSeconds(remainingSeconds).ToString(@"hh\:mm\:ss");
    }

    public bool ShouldDisplayLog(Mel.LogLevel level)
    {
        return SelectedLogFilter switch
        {
            "Info" => level == Mel.LogLevel.Information,
            "Warning" => level == Mel.LogLevel.Warning,
            "Error" => level == Mel.LogLevel.Error || level == Mel.LogLevel.Critical,
            _ => true
        };
    }

    private static long CreateTileKey(int mapId, int tileX, int tileY, ExtractionPhase phase)
    {
        // mapId(12 bits) | tileX(6 bits) | tileY(6 bits) | phase(4 bits)
        return ((long)mapId << 16) | ((long)tileX << 10) | ((long)tileY << 4) | (long)phase;
    }
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
