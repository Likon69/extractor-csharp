using System.Diagnostics;
using System.IO;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.DbcExtractor;
using MaNGOS.Extractor.Formats.Mpq;
using MaNGOS.Extractor.MapExtractor;
using MaNGOS.Extractor.MmapExtractor;
using MaNGOS.Extractor.RoadExtractor;
using MaNGOS.Extractor.UI;
using MaNGOS.Extractor.VmapExtractor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MaNGOS.Extractor.CLI;

public static class Program
{
    private static ILoggerFactory? _loggerFactory;

    [System.STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        return RunCliAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var options = ParseArgs(args);
        if (options == null) return 1;
        Banner(options);

        _loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = null; }));

        using var archives = MpqArchiveCollection.FromWoWDirectory(
            options.WoWPath, options.Locale, _loggerFactory);

        // Load DBC tables once at startup — mirrors MaNGOS C++ System.cpp
        // ExtractMapsFromMpq(): "ReadMapDBC(); ReadAreaTableDBC(); ReadLiquidTypeTableDBC();"
        // Without these, the .map file's AREA section is all 0xFFFF and the
        // LIQUID type flags fall back to wrong values.
        MaNGOS.Extractor.Formats.Adt.Models.AdtFile.LoadAreaTable(archives);
        MaNGOS.Extractor.Formats.Adt.Models.AdtFile.LoadLiquidTypeTable(archives);

        var progress = new ConsoleProgress();
        var ct = CancellationToken.None;
        int processed = 0;

        // Single vmap service for the whole phase — the dedup HashSet of
        // .vmo/.vmd files (already written during per-map MDDF/MODF
        // processing) is shared with the global GameObjectDisplayInfo pass.
        // Created only when either VmapExtract or VmapAssemble is requested
        // (mirrors the 2 sub-phases of MaNGOS vmap-extractor.exe).
        MaNGOS.Extractor.VmapExtractor.MangosVmapExtractorService? vmapSvc = null;
        bool needVmapExtract = options.Phases.Contains("vmapextract", StringComparer.OrdinalIgnoreCase);
        bool needVmapAssemble = options.Phases.Contains("vmapassemble", StringComparer.OrdinalIgnoreCase);
        bool needVmapLegacy = options.Phases.Contains("vmap", StringComparer.OrdinalIgnoreCase);
        if (needVmapExtract || needVmapAssemble || needVmapLegacy)
        {
            // Pass the output ROOT, not output/vmaps — the service itself
            // writes to Buildings/, vmaps/ and Buildings/dir_bin as siblings
            // (matches MaNGOS C++ output layout byte-for-byte).
            vmapSvc = new MaNGOS.Extractor.VmapExtractor.MangosVmapExtractorService(archives, _loggerFactory, options.OutputPath);
        }

        // Global DBC/DB2 extraction (independent of map ID). Mirrors MaNGOS C++
        // map-extractor/System.cpp::ExtractDBCFiles — writes all *.dbc/*.db2
        // found across the MPQ archives to <output>/dbc/ (basicLocale=true layout),
        // plus component.wow-<locale>.txt. Consumed by the worldserver at startup.
        if (options.Phases.Contains("dbc", StringComparer.OrdinalIgnoreCase))
        {
            string dbcDir = Path.Combine(options.OutputPath, "dbc");
            var dbcSvc = new DbcExtractorService(archives, _loggerFactory, dbcDir);
            int dbcCount = await dbcSvc.ExtractAsync(options.Locale, ct);
            Console.WriteLine($"  [Dbc] {dbcCount} files extracted.");
        }

        foreach (var mid in options.MapIds)
        {
            uint mapId = (uint)mid;
            string mapName = WowConstants.GetMapDirectory(mapId);
            Console.WriteLine();
            Console.WriteLine($"=== Processing map {mapId} ({mapName}) ===");

            if (options.Phases.Contains("map", StringComparer.OrdinalIgnoreCase))
            {
                string mapDir = Path.Combine(options.OutputPath, "maps");
                var svc = new MapExtractorService(archives, _loggerFactory, mapDir);
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct, options.TileX, options.TileY);
                Console.WriteLine($"  [Map] {tiles} tiles extracted.");
                processed += tiles;
            }

            // Phase VmapExtract: mirrors vmap-extractor.cpp::ExtractWmo + ParseMapFiles
            // + ExtractGameobjectModels. Writes raw .vmo/.vmd (VMAPt07) to Buildings/
            // and a dir_bin index. NO .vmtree/.vmtile yet.
            if (needVmapExtract && vmapSvc != null)
            {
                int tiles = await vmapSvc.ExtractMapAsync(mapId, mapName, ct, options.TileX, options.TileY, skipAssemble: true);
                Console.WriteLine($"  [VmapExtract] {tiles} tiles → Buildings/ (raw VMAPt07) + dir_bin.");
            }

            // Phase VmapAssemble: mirrors vmap-extractor.cpp::AssembleVMAP (TileAssembler::convertWorld2).
            // Reads Buildings/dir_bin, builds the BIH, writes .vmtree + .vmtile and
            // copies/rewrites the FINAL .vmo/.vmd (VMAP_4.0) to vmaps/.
            if (needVmapAssemble && vmapSvc != null)
            {
                bool ok = vmapSvc.AssembleMap(mapId, mapName);
                Console.WriteLine($"  [VmapAssemble] {(ok ? "OK" : "FAILED")} → vmaps/.");
            }

            // Legacy combined "Vmap" phase: does both extract + assemble in one pass.
            if (needVmapLegacy && vmapSvc != null)
            {
                int tiles = await vmapSvc.ExtractMapAsync(mapId, mapName, ct, options.TileX, options.TileY);
                Console.WriteLine($"  [Vmap] {tiles} tiles → vmtile + vmtree + .vmo/.vmd.");
            }

            if (options.Phases.Contains("road", StringComparer.OrdinalIgnoreCase))
            {
                string roadDir = Path.Combine(options.OutputPath, "roadmaps");
                var svc = new RoadExtractorService(archives, _loggerFactory, roadDir);
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct, options.TileX, options.TileY);
                Console.WriteLine($"  [Road] {tiles} tiles extracted.");
            }

            if (options.Phases.Contains("mmap", StringComparer.OrdinalIgnoreCase))
            {
                string mmapDir = Path.Combine(options.OutputPath, "mmaps");
                var recast = new RecastConfig(
                    cellSize: 0.303030f,
                    cellHeight: 0.2f,
                    walkableSlopeAngle: 50.0f,
                    walkableHeight: 11,
                    walkableRadius: 2,
                    walkableClimb: 5);
                string roadDir = Path.Combine(options.OutputPath, "roadmaps");
                // vmapDir is the output root — the MmapExtractorService/MangosVmapGeometryLoader
                // appends "vmaps/" internally when looking for .vmtile/.vmo/.vmd files.
                string vmapDir = options.OutputPath;
                // Read terrain from the extracted .map files written by the
                // map phase — same as C++ MapBuilder::buildTile → TerrainBuilder::loadMap.
                string mapsDir = Path.Combine(options.OutputPath, "maps");
                var svc = new MmapExtractorService(archives, _loggerFactory, mmapDir,
                    recast, options.Threads, options.GoSpawnsPath, offMeshPath: options.OffMeshPath,
                    roadMapsDir: roadDir, vmapDir: vmapDir, mapsDir: mapsDir);
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct, options.TileX, options.TileY);
                Console.WriteLine($"  [Mmap] {tiles} tiles extracted.");
                processed += tiles;
            }
        }

        // After all per-map passes are done, run the global GameObject models
        // extraction (mirrors C++ ExtractGameobjectModels from the vmap-export
        // tool, minus the temp_gameobject_models index file). The .vmo/.vmd
        // collision meshes are deduped against the per-map MDDF/MODF set.
        // gameobject_spawns.bin itself is user-provided and never created here.
        // Skipped when only running VmapAssemble (extract already done).
        if (vmapSvc != null && (needVmapExtract || needVmapLegacy))
        {
            Console.WriteLine();
            Console.WriteLine("=== Extracting GameObject model .vmo/.vmd (DBC) ===");
            int goBuilt = await vmapSvc.ExtractGameObjectModelsAsync(ct);
            Console.WriteLine($"  [Vmap] {goBuilt} GameObject .vmo/.vmd collision meshes built.");
        }

        Console.WriteLine();
        Console.WriteLine($"Done. Total tiles processed: {processed}.");
        _loggerFactory?.Dispose();
        return 0;
    }

    private static void Banner(CliOptions opts)
    {
        Console.WriteLine($"MaNGOS Extractor v1.0 — WotLK 3.3.5a (build {WowConstants.TargetBuild})");
        Console.WriteLine($"Format: maps=v1.5 | vmaps=VMAPt07 | mmap=t06 | roadmaps=raw");
        Console.WriteLine($"Config: wow={opts.WoWPath} output={opts.OutputPath} threads={opts.Threads} phases={string.Join(",", opts.Phases)} maps={string.Join(",", opts.MapIds)} locale={opts.Locale}");
        Console.WriteLine(new string('-', 72));
    }

    private static CliOptions? ParseArgs(string[] args)
    {
        var opts = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--wow" or "-w":
                    if (++i >= args.Length) return BadArg("--wow <path>");
                    opts.WoWPath = args[i];
                    break;
                case "--out" or "-o":
                    if (++i >= args.Length) return BadArg("--out <path>");
                    opts.OutputPath = args[i];
                    break;
                case "--phases":
                    if (++i >= args.Length) return BadArg("--phases <csv>");
                    opts.Phases = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "--maps":
                    if (++i >= args.Length) return BadArg("--maps <csv>");
                    opts.MapIds = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse).ToArray();
                    break;
                case "--tile":
                    if (++i >= args.Length) return BadArg("--tile <x,y>");
                    var tileParts = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (tileParts.Length != 2) return BadArg("--tile <x,y>");
                    opts.TileX = int.Parse(tileParts[0]);
                    opts.TileY = int.Parse(tileParts[1]);
                    break;
                case "--threads":
                    if (++i >= args.Length) return BadArg("--threads <n>");
                    opts.Threads = int.Parse(args[i]);
                    break;
                case "--locale":
                    if (++i >= args.Length) return BadArg("--locale <code>");
                    opts.Locale = args[i];
                    break;
                case "--gospawns":
                    if (++i >= args.Length) return BadArg("--gospawns <path>");
                    opts.GoSpawnsPath = args[i];
                    break;
                case "--help" or "-h":
                    Help();
                    return null;
                case "--offmesh":
                    if (i + 1 >= args.Length) return BadArg("--offmesh requires a value");
                    opts.OffMeshPath = args[++i];
                    break;
                default:
                    return BadArg(args[i]);
            }
        }

        if (string.IsNullOrEmpty(opts.WoWPath) || string.IsNullOrEmpty(opts.OutputPath))
            return BadArg("--wow and --out are required");

        if (opts.MapIds.Length == 0)
            opts.MapIds = new[] { 0, 1, 530, 571 };

        if (opts.Phases.Length == 0)
            opts.Phases = new[] { "Dbc", "Map", "VmapExtract", "VmapAssemble", "Road", "Mmap" };

        return opts;
    }

    private static CliOptions? BadArg(string msg)
    {
        Console.Error.WriteLine($"Error: {msg}");
        Help();
        return null;
    }

    private static void Help()
    {
        Console.WriteLine("Usage: MaNGOS.Extractor.CLI [options]");
        Console.WriteLine("  --wow <path>        WoW client path (required)");
        Console.WriteLine("  --out <path>        Output directory (required)");
        Console.WriteLine("  --phases <csv>      Comma-separated phases (default: Dbc,Map,VmapExtract,VmapAssemble,Road,Mmap)");
        Console.WriteLine("  --maps <csv>        Comma-separated map IDs (default: 0,1,530,571)");
        Console.WriteLine("  --tile <x,y>        Extract only one ADT tile, e.g. --tile 35,20");
        Console.WriteLine("  --threads <n>       Max threads (default: 4)");
        Console.WriteLine("  --locale <code>     Locale code (default: enUS)");
        Console.WriteLine("  --gospawns <path>   gameobject_spawns.bin path (default: gameobject_spawns.bin)");
        Console.WriteLine("  --offmesh <path>    OffMesh connections file (offmesh.txt format)");
    }

    private sealed class CliOptions
    {
        public string WoWPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string[] Phases { get; set; } = Array.Empty<string>();
        public int[] MapIds { get; set; } = Array.Empty<int>();
        public int? TileX { get; set; }
        public int? TileY { get; set; }
        public int Threads { get; set; } = 4;
        public string Locale { get; set; } = "enUS";
        public string GoSpawnsPath { get; set; } = "gameobject_spawns.bin";
        public string? OffMeshPath { get; set; }
    }

    private sealed class ConsoleProgress : IProgress<TileProgressEvent>
    {
        public void Report(TileProgressEvent value)
        {
            Console.Write($"\r  [{value.TileX},{value.TileY}] {value.Status}  ");
        }
    }
}
