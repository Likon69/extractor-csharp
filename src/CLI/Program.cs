using System.Diagnostics;
using System.IO;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Mpq;
using MaNGOS.Extractor.MapExtractor;
using MaNGOS.Extractor.MmapExtractor;
using MaNGOS.Extractor.RoadExtractor;
using MaNGOS.Extractor.UI;
using MaNGOS.Extractor.VmapExtractor;
using Microsoft.Extensions.Logging;

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

        _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information));

        using var archives = MpqArchiveCollection.FromWoWDirectory(
            options.WoWPath, options.Locale, _loggerFactory);

        var progress = new ConsoleProgress();
        var ct = CancellationToken.None;
        int processed = 0;

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
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct);
                Console.WriteLine($"  [Map] {tiles} tiles extracted.");
                processed += tiles;
            }

            if (options.Phases.Contains("vmap", StringComparer.OrdinalIgnoreCase))
            {
                string vmapDir = Path.Combine(options.OutputPath, "vmaps");
                var svc = new VmapExtractorService(archives, _loggerFactory, vmapDir);
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct);
                Console.WriteLine($"  [Vmap] {tiles} tiles extracted.");
            }

            if (options.Phases.Contains("road", StringComparer.OrdinalIgnoreCase))
            {
                string roadDir = Path.Combine(options.OutputPath, "roadmaps");
                var svc = new RoadExtractorService(archives, _loggerFactory, roadDir);
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct);
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
                var svc = new MmapExtractorService(archives, _loggerFactory, mmapDir,
                    recast, options.Threads, options.GoSpawnsPath, offMeshPath: null);
                int tiles = await svc.ExtractMapAsync(mapId, mapName, progress, ct);
                Console.WriteLine($"  [Mmap] {tiles} tiles extracted.");
            }
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
                default:
                    return BadArg(args[i]);
            }
        }

        if (string.IsNullOrEmpty(opts.WoWPath) || string.IsNullOrEmpty(opts.OutputPath))
            return BadArg("--wow and --out are required");

        if (opts.MapIds.Length == 0)
            opts.MapIds = new[] { 0, 1, 530, 571 };

        if (opts.Phases.Length == 0)
            opts.Phases = new[] { "Map", "Vmap", "Road", "Mmap" };

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
        Console.WriteLine("  --phases <csv>      Comma-separated phases (default: Map,Vmap,Road,Mmap)");
        Console.WriteLine("  --maps <csv>        Comma-separated map IDs (default: 0,1,530,571)");
        Console.WriteLine("  --threads <n>       Max threads (default: 4)");
        Console.WriteLine("  --locale <code>     Locale code (default: enUS)");
        Console.WriteLine("  --gospawns <path>   gameobject_spawns.bin path (default: gameobject_spawns.bin)");
    }

    private sealed class CliOptions
    {
        public string WoWPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string[] Phases { get; set; } = Array.Empty<string>();
        public int[] MapIds { get; set; } = Array.Empty<int>();
        public int Threads { get; set; } = 4;
        public string Locale { get; set; } = "enUS";
        public string GoSpawnsPath { get; set; } = "gameobject_spawns.bin";
    }

    private sealed class ConsoleProgress : IProgress<TileProgressEvent>
    {
        public void Report(TileProgressEvent value)
        {
            Console.Write($"\r  [{value.TileX},{value.TileY}] {value.Status}  ");
        }
    }
}
