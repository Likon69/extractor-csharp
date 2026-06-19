using System.IO;
using System.Numerics;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.Dbc;
using MaNGOS.Extractor.Formats.M2;
using MaNGOS.Extractor.Formats.Vmap.Mangos;
using MaNGOS.Extractor.Formats.Wdt;
using MaNGOS.Extractor.Formats.Wmo.Models;
using MaNGOS.Extractor.Formats.Wmo.Parsing;

namespace MaNGOS.Extractor.VmapExtractor;

/// <summary>
/// Mangos-compatible vmap-extractor (replaces the Honorbuddy VMAPt07 output).
///
/// Mirrors MaNGOS vmapexport.cpp + TileAssembler.cpp:
///   1. Read WDT → list of ADT tiles
///   2. For each ADT, read MODF/MDDF (WMO/M2 placements)
///   3. For each unique WMO/M2, build the compiled collision file
///      (.vmo / .vmd) using the Mangos WorldModel::WriteFile format
///   4. Write the per-tile .vmtile (placement list) and per-map .vmtree
///      (BIH + global placements)
///
/// The mmap-extractor downstream reads the .vmtile and the .vmo/.vmd
/// files via VMapManager2 + TerrainBuilder::loadVMap. This is the
/// official Mangos pipeline — same as the C++ Movemap-Generator.
/// </summary>
public sealed class MangosVmapExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly AdtParser _adtParser;
    private readonly WmoParser _wmoParser;
    private readonly M2Parser _m2Parser;
    private readonly ILogger _logger;
    private readonly string _outputDir;
    private readonly int _globalThreadCount;
    private readonly HashSet<string> _writtenVmoFiles = new(StringComparer.OrdinalIgnoreCase);
    // Per-map doodad data keyed by uniform WMO name. Populated by
    // TryBuildWmoAsync (which already iterates every group) and consumed by
    // the per-tile loop after the WMO dir_bin record is written, to write
    // the matching M2 doodad records (MaNGOS vmapexport.cpp::ADT::init →
    // Doodad::ExtractSet).
    private readonly Dictionary<string, (MaNGOS.Extractor.Formats.Wmo.Models.WmoRootFile Root, ushort[] DoodadRefs)> _wmoDoodadData = new(StringComparer.OrdinalIgnoreCase);

    public MangosVmapExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir,
        int threadCount = 4)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<MangosVmapExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _wmoParser = new WmoParser(archive, loggerFactory.CreateLogger<WmoParser>());
        _m2Parser = new M2Parser(archive);
        _outputDir = outputDir;
        _globalThreadCount = threadCount;
        _logger.LogInformation("[MangosVmap] Output directory: {OutputDir} (threads={Threads})", outputDir, threadCount);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        CancellationToken ct = default,
        int? onlyTileX = null,
        int? onlyTileY = null,
        bool skipAssemble = false)
    {
        _logger.LogInformation("[MangosVmap] Starting Mangos vmap extraction for {MapName} (id={MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("[MangosVmap] Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        // Faithful port of MaNGOS vmapexport.cpp::main: the dir_bin is created
        // fresh per run (the C++ exits with "please delete it!" if it already
        // exists). For simplicity we just truncate at the start of each map.
        string dirBinPath = Path.Combine(_outputDir, "Buildings", "dir_bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dirBinPath)!);
        // Truncate at start of each map so the file contains only this map's
        // placements (MaNGOS writes everything in one run, but C# processes
        // maps one at a time). The C++ semantics of "delete Buildings/ before
        // running" are preserved at the per-map level.
        if (File.Exists(dirBinPath)) File.Delete(dirBinPath);

        // Reset per-map doodad cache (filled lazily by TryBuildWmoAsync as
        // WMOs are encountered in ADT placements). Also reset the global
        // unique-id counter to match MaNGOS uniqueObjectIds semantics.
        _wmoDoodadData.Clear();
        MangosDoodadExtractor.ResetUniqueIds();

        // Per-tile / per-build counters. Declared here so the WDT-worldspawn
        // loop below can bump builtVmo/failedBuilds inline.
        int totalWmo = 0, totalM2 = 0;
        int writtenTiles = 0;
        int builtVmo = 0, builtVmd = 0, failedBuilds = 0;

        // Faithful port of MaNGOS wdtfile.cpp::init: write the WDT-level
        // worldspawn WMO records to Buildings/dir_bin BEFORE any ADT records.
        // The C++ uses tileX=65, tileY=65 for these so the TileAssembler
        // groups them under a single global tile for instances/maps that
        // don't have terrain tiles.
        if (_wdtReader.WorldspawnWmoNames.Length > 0
            && _wdtReader.WorldspawnWmoPlacements.Length > 0
            && File.Exists(dirBinPath))
        {
            for (int wi = 0; wi < _wdtReader.WorldspawnWmoPlacements.Length; wi++)
            {
                var modf = _wdtReader.WorldspawnWmoPlacements[wi];
                if ((int)modf.NameId >= _wdtReader.WorldspawnWmoNames.Length) continue;
                string wmoName = _wdtReader.WorldspawnWmoNames[(int)modf.NameId];
                if (string.IsNullOrEmpty(wmoName)) continue;

                string uniformName = MangosVmapBuildingWriter.GetUniformName(wmoName);
                // Ensure the WMO .vmo exists so the mmap extractor can load it.
                string vmoRel = wmoName.Replace('\\', '/') + ".vmo";
                if (_writtenVmoFiles.Add(vmoRel))
                {
                    if (!await TryBuildWmoAsync(wmoName, ct)) { failedBuilds++; _writtenVmoFiles.Remove(vmoRel); }
                    else builtVmo++;
                }

                // Write the WMO worldspawn record with tileX=65, tileY=65
                MangosVmapBuildingWriter.AppendWmoRecord(
                    dirBinPath,
                    mapId: mapId, tileX: 65, tileY: 65,
                    adtId: modf.NameSet, uniqueId: modf.UniqueId,
                    posX: modf.PositionX, posY: modf.PositionY, posZ: modf.PositionZ,
                    rotX: modf.RotationX, rotY: modf.RotationY, rotZ: modf.RotationZ,
                    scale: 1.0f,
                    boundMinX: modf.LowerBoundsX, boundMinY: modf.LowerBoundsY, boundMinZ: modf.LowerBoundsZ,
                    boundMaxX: modf.UpperBoundsX, boundMaxY: modf.UpperBoundsY, boundMaxZ: modf.UpperBoundsZ,
                    wmoInstName: uniformName);

                // And its doodads (MaNGOS vmapexport.cpp::WDTFile::init →
                // Doodad::ExtractSet, with tileX=65, tileY=65 so the
                // doodad records also get MOD_WORLDSPAWN).
                if (_wmoDoodadData.TryGetValue(uniformName, out var doodadData)
                    && doodadData.DoodadRefs.Length > 0)
                {
                    MangosDoodadExtractor.ExtractSet(
                        dirBinPath,
                        mapId: mapId, tileX: 65, tileY: 65,
                        wmoUniformName: uniformName,
                        modf: modf,
                        root: doodadData.Root,
                        doodadReferences: doodadData.DoodadRefs,
                        tryExtractModel: TryBuildM2ForDoodad);
                }
            }
        }

        var tiles = _wdtReader.GetExistingTiles();
        if (onlyTileX.HasValue && onlyTileY.HasValue)
            tiles = tiles.Where(t => t.X == onlyTileX.Value && t.Y == onlyTileY.Value).ToList();
        _logger.LogInformation("[MangosVmap] Found {Count} ADT tiles for {MapName}", tiles.Count, mapName);

        // per-tile spawns are no longer accumulated here: the MangosTileAssembler
        // re-reads Buildings/dir_bin after the ADT pass and writes the .vmtile
        // files with the correct BIH-referenced indices.

        var adtLastLog = DateTime.UtcNow;
        int adtIdx = 0;
        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();
            adtIdx++;
            var perTile = new List<MangosModelSpawn>();
            string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX}_{tileY}.adt";
            var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);
            if (!result.Success || result.Tile == null)
            {
                if ((DateTime.UtcNow - adtLastLog).TotalSeconds >= 2)
                {
                    adtLastLog = DateTime.UtcNow;
                    _logger.LogInformation("[MangosVmap] ADT progress: {Done}/{Total} (last={Tx}_{Ty}, built vmo={Vmo} vmd={Vmd})",
                        adtIdx, tiles.Count, tileX, tileY, builtVmo, builtVmd);
                }
                continue;
            }

            // WMO placements (MODF)
            for (int i = 0; i < result.Tile.WmoPlacements.Length; i++)
            {
                var modf = result.Tile.WmoPlacements[i];
                if ((int)modf.NameId >= result.Tile.WmoNames.Length) continue;
                string wmoName = result.Tile.WmoNames[(int)modf.NameId];
                if (string.IsNullOrEmpty(wmoName)) continue;

                // Build the .vmo for this WMO (once per unique WMO file).
                string vmoRelPath = wmoName.Replace('\\', '/') + ".vmo";
                if (_writtenVmoFiles.Add(vmoRelPath))
                {
                    if (await TryBuildWmoAsync(wmoName, ct))
                        builtVmo++;
                    else
                        failedBuilds++;
                }

                var spawn = BuildWmoSpawn(mapId, tileX, tileY, (uint)i, wmoName, modf);
                perTile.Add(spawn);
                totalWmo++;

                // Faithful port of MaNGOS wmo.cpp::WMOInstance: append a WMO
                // placement record to Buildings/dir_bin in exactly the format
                // ModelInstance::ReadFromFile expects (80-byte header +
                // length-prefixed uniform name). The WMO's MODF.UniqueId is
                // used as the placement ID, and the local AABB from MODF
                // (lowerBounds / upperBounds) is written as fixCoords.
                {
                    string uniformName = MangosVmapBuildingWriter.GetUniformName(wmoName);
                    MangosVmapBuildingWriter.AppendWmoRecord(
                        dirBinPath,
                        mapId: mapId, tileX: (uint)tileX, tileY: (uint)tileY,
                        adtId: modf.NameSet, uniqueId: modf.UniqueId,
                        posX: modf.PositionX, posY: modf.PositionY, posZ: modf.PositionZ,
                        rotX: modf.RotationX, rotY: modf.RotationY, rotZ: modf.RotationZ,
                        scale: 1.0f,
                        boundMinX: modf.LowerBoundsX, boundMinY: modf.LowerBoundsY, boundMinZ: modf.LowerBoundsZ,
                        boundMaxX: modf.UpperBoundsX, boundMaxY: modf.UpperBoundsY, boundMaxZ: modf.UpperBoundsZ,
                        wmoInstName: uniformName);

                    // After recording the WMO itself, also write the doodads
                    // it contains as M2 ModelSpawns (MaNGOS vmapexport.cpp
                    // ADT::init → model.cpp::Doodad::ExtractSet). The doodad
                    // data was cached by TryBuildWmoAsync (Paths/Sets/Spawns
                    // from the root + merged References from every group).
                    if (_wmoDoodadData.TryGetValue(uniformName, out var doodadData)
                        && doodadData.DoodadRefs.Length > 0)
                    {
                        MangosDoodadExtractor.ExtractSet(
                            dirBinPath,
                            mapId: mapId, tileX: (uint)tileX, tileY: (uint)tileY,
                            wmoUniformName: uniformName,
                            modf: modf,
                            root: doodadData.Root,
                            doodadReferences: doodadData.DoodadRefs,
                            tryExtractModel: TryBuildM2ForDoodad);
                    }
                }
            }

            // M2 placements (MDDF)
            for (int i = 0; i < result.Tile.DoodadPlacements.Length; i++)
            {
                var mddf = result.Tile.DoodadPlacements[i];
                if ((int)mddf.NameId >= result.Tile.ModelNames.Length) continue;
                string m2Name = result.Tile.ModelNames[(int)mddf.NameId];
                if (string.IsNullOrEmpty(m2Name)) continue;

                // Build the .vmd for this M2 (once per unique M2 file).
                string vmdRelPath = m2Name.Replace('\\', '/') + ".vmd";
                if (_writtenVmoFiles.Add(vmdRelPath))
                {
                    if (await TryBuildM2Async(m2Name, ct))
                        builtVmd++;
                    else
                        failedBuilds++;
                }

                var spawn = BuildM2Spawn(mapId, tileX, tileY, (uint)i, m2Name, mddf);
                perTile.Add(spawn);
                totalM2++;

                // Faithful port of MaNGOS model.cpp::ModelInstance: append an
                // M2 placement record to Buildings/dir_bin. The MDDF
                // FileDataId is used as the placement ID (matches what the
                // C++ ModelInstance constructor reads after the NameId). Scale
                // is MDDF.Scale / 1024 (WotLK stores the scale as a uint16
                // divided by 1024.0f). adtId is 0 (unused for M2).
                {
                    string uniformName = MangosVmapBuildingWriter.GetUniformName(m2Name);
                    MangosVmapBuildingWriter.AppendM2Record(
                        dirBinPath,
                        mapId: mapId, tileX: (uint)tileX, tileY: (uint)tileY,
                        adtId: 0, uniqueId: mddf.FileDataId,
                        posX: mddf.PositionX, posY: mddf.PositionY, posZ: mddf.PositionZ,
                        rotX: mddf.RotationY, rotY: mddf.RotationX, rotZ: mddf.RotationZ,
                        scale: mddf.Scale / 1024.0f,
                        m2InstName: uniformName);
                }
            }

            // NOTE: the .vmtile files are written by the MangosTileAssembler
            // AFTER the BIH is built, so they can carry the correct
            // referencedVal (= modelNodeIdx[spawn.ID]). Writing them here with a
            // sequential index would diverge from the C++ reference output.
            writtenTiles++;
            if ((DateTime.UtcNow - adtLastLog).TotalSeconds >= 2)
            {
                adtLastLog = DateTime.UtcNow;
                _logger.LogInformation("[MangosVmap] ADT progress: {Done}/{Total} (last={Tx}_{Ty}, built vmo={Vmo} vmd={Vmd}, total placements wmo={Tw} m2={Tm})",
                    adtIdx, tiles.Count, tileX, tileY, builtVmo, builtVmd, totalWmo, totalM2);
            }
        }
        _logger.LogInformation("[MangosVmap] ADT pass complete: {Done}/{Total} tiles, built vmo={Vmo} vmd={Vmd}",
            adtIdx, tiles.Count, builtVmo, builtVmd);

        // ===========================================================================
        // FAITHFUL PORT of MaNGOS wmo.cpp::ExtractWmo — global WMO/M2 scan.
        //
        // The C++ vmap-extractor does NOT only extract WMOs/M2s referenced by
        // ADT/WDT placements. It iterates EVERY MPQ archive and extracts EVERY
        // .wmo / .m2 file found, regardless of whether any ADT uses it. This
        // ensures:
        //   1. Gameobject models (chairs, doors, lamps, transports...) referenced
        //      only by GameObjectDisplayInfo.dbc but not by any ADT are still
        //      built into Buildings/ + vmaps/.
        //   2. WMOs that are global map objects (continents / dungeon roots) but
        //      not placed in any tile are still extracted so the mmap-extractor
        //      can load them.
        //   3. The doodad M2s of those WMOs are reachable through MangosDoodadExtractor.
        //
        // Mirrors ExtractWmo() lines 789-819 (reverse MPQ priority order so the
        // highest-priority WMO overwrites lower). Per-archive inline processing
        // avoids the StormLib SFileFindFirstFile deadlock that a pre-collection
        // loop would trigger.
        // ===========================================================================
        await ExtractAllWmoM2GloballyAsync(Path.Combine(_outputDir, "Buildings"), ct);

        // Phase-2 of the Mangos extractor (TileAssembler::convertWorld2):
        // re-read Buildings/dir_bin, build the BIH tree, and write the
        // per-map .vmtree (BIH + global spawns) and per-tile .vmtile files
        // with BIH-referenced indices. This is the faithful port of the C++
        // assembler; it replaces the old placeholder writer that left the
        // BIH empty.
        // Skipped when running the VmapExtract phase alone (mirrors the C++
        // vmap-export which has 2 internal phases: extract to Buildings/,
        // then TileAssembler builds vmaps/).
        string vmtreePath = "";
        if (!skipAssemble)
        {
            AssembleMap(mapId, mapName, out vmtreePath);
        }

        _logger.LogInformation("[MangosVmap] Extraction complete for {MapName}: {Tiles} tiles, " +
            "{Wmos} WMO placements ({VmoFiles} .vmo files built), {M2s} M2 placements ({VmdFiles} .vmd files built), " +
            "{Fails} build failures → {Vmtree}",
            mapName, writtenTiles, totalWmo, builtVmo, totalM2, builtVmd, failedBuilds, vmtreePath);
        return writtenTiles;
    }

    /// <summary>
    /// Phase-2 of the Mangos vmap-extractor (TileAssembler::convertWorld2):
    /// reads Buildings/dir_bin, builds the BIH tree, writes per-map .vmtree
    /// + per-tile .vmtile + copies/rewrites the FINAL .vmo/.vmd to vmaps/.
    /// Can be called independently after VmapExtract to mirror the C++ flow.
    /// </summary>
    public bool AssembleMap(uint mapId, string mapName)
        => AssembleMap(mapId, mapName, out _);

    // ---------------------------------------------------------------------------
    // Global WMO scan — faithful port of MaNGOS wmo.cpp::ExtractWmo (789-819).
    //
    // The C++ vmap-extractor iterates EVERY MPQ archive and extracts EVERY
    // .wmo file found, regardless of whether any ADT uses it. This is the
    // ONE global MPQ scan the C++ does. M2s are NOT scanned globally by the
    // C++ — they only come from the per-ADT MDDF pass and the DBC-driven
    // ExtractGameobjectModels() pass. (Earlier the C# also did a global
    // .m2 scan, but that produced 2106 extra M2s vs the C++ reference:
    // vehicles, bosses, BG portals, mounts, spell-visuals — all the
    // models that live in MPQ but are never referenced by any ADT or DBC.)
    //
    // Done in REVERSE MPQ priority order so the highest-priority WMO (patch
    // MPQs) is the one on disk — lower-priority copies from world.MPQ get
    // overwritten by the patch-version that wins the client priority list.
    //
    // Two key behaviors matching the C++ ExtractWmo loop:
    //   1. **Per-archive iteration with inline processing**: SFileFindFirstFile
    //      returns a handle that must be drained in the same call. Pre-collecting
    //      all 50k+ files into a List<string> before processing keeps the find
    //      handle alive too long and triggers StormLib deadlocks. We iterate
    //      one archive at a time and process each file inline as it's yielded.
    //   2. **Disk-existence skip**: if Buildings/{uniformName} already exists,
    //      the file is considered already extracted and is skipped. This makes
    //      re-runs instant (matches C++ ExtractSingleWmo's FileExists check at
    //      wmo.cpp:675).
    // ---------------------------------------------------------------------------
    private async Task ExtractAllWmoM2GloballyAsync(string buildingsDir, CancellationToken ct)
    {
        // -- 1. Scan all MPQ archives for *.wmo ----------------------------------
        _logger.LogInformation("[MangosVmap] Global WMO scan: iterating MPQs in reverse priority order");
        int globalWmoBuilt = 0, globalWmoFailed = 0, globalWmoSkipped = 0, globalWmoExist = 0;
        var wmoLastLog = DateTime.UtcNow;
        int wmoIdx = 0;
        // Tracks which uniform names we've already PROCESSED (built or skipped).
        // The reverse-priority iteration means we may see the same uniform name
        // multiple times (once per archive it's in); we keep the LAST write, which
        // is from the highest-priority archive.
        var wmoProcessed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            _archive.ForEachFileReversePriority("*.wmo", wmoPath =>
            {
                ct.ThrowIfCancellationRequested();
                wmoIdx++;
                string uni = MangosVmapBuildingWriter.GetUniformName(wmoPath);
                // Dedup: if we already processed this uniform, skip. The highest-
                // priority archive was processed LAST in reverse order, so its
                // bytes are the ones on disk.
                if (!wmoProcessed.Add(uni)) { globalWmoSkipped++; return; }
                string buildingFile = Path.Combine(buildingsDir, uni);
                if (File.Exists(buildingFile)) { globalWmoExist++; return; }
                if (_writtenVmoFiles.Contains(uni)) { globalWmoSkipped++; return; }
                try
                {
                    bool ok = TryBuildWmoAsync(wmoPath, ct).GetAwaiter().GetResult();
                    if (ok) { globalWmoBuilt++; _writtenVmoFiles.Add(uni); }
                    else globalWmoFailed++;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[MangosVmap] Global WMO skip on {Path}", wmoPath); globalWmoFailed++; }
                if ((DateTime.UtcNow - wmoLastLog).TotalSeconds >= 1)
                {
                    wmoLastLog = DateTime.UtcNow;
                    _logger.LogInformation("[MangosVmap] Global WMO progress: seen={Seen} unique={Unique} (built={Built} exist={Exist} skipped={Skipped} failed={Failed})",
                        wmoIdx, wmoProcessed.Count, globalWmoBuilt, globalWmoExist, globalWmoSkipped, globalWmoFailed);
                }
            });
        }, ct);
        _logger.LogInformation("[MangosVmap] Global WMO pass done: seen={Seen} unique={Unique} built={Built} exist={Exist} skipped={Skipped} failed={Failed}",
            wmoIdx, wmoProcessed.Count, globalWmoBuilt, globalWmoExist, globalWmoSkipped, globalWmoFailed);

        // -- 2. Scan all MPQ archives for *.m2 -----------------------------------
        // [DISABLED 2026-06-19] The C++ Mangos does NOT do a global "*.m2" MPQ scan.
        // M2s come ONLY from the per-ADT MDDF pass and the DBC-driven
        // ExtractGameobjectModels() pass. Doing a global scan here extracts
        // 2106 extra M2s (vehicles, bosses, BG portals, mounts, spell-visuals)
        // that the C++ reference doesn't have, and the scan over 24k+ files
        // takes ~10 minutes with 14k+ parse failures. The C# output should
        // match the C++ reference (5593 M2s), not exceed it.
        _logger.LogInformation("[MangosVmap] Global M2 scan: DISABLED (C++ Mangos doesn't do this; M2s come from MDDF + GameObjectDisplayInfo.dbc only)");
        await Task.Yield();
    }

    private bool AssembleMap(uint mapId, string mapName, out string vmtreePath)
    {
        string buildingsDir = Path.Combine(_outputDir, "Buildings");
        string vmapsDir = Path.Combine(_outputDir, "vmaps");
        var assembler = new MangosTileAssembler(buildingsDir, vmapsDir, _m2Parser, _logger);
        bool ok = assembler.AssembleMap(mapId, mapName);
        vmtreePath = Path.Combine(vmapsDir, MangosVmapTree.GetFileName(mapId));
        return ok;
    }

    // -----------------------------------------------------------------------
    // Global GameObject models extraction — mirrors MaNGOS vmapexport.cpp
    // ExtractGameobjectModels() in model.cpp, but WITHOUT writing the
    // temp_gameobject_models / gameobject_spawns.bin index file.
    //
    // Why: the original C++ ExtractGameobjectModels does two things:
    //   1. Iterate GameObjectDisplayInfo.dbc, extract every referenced .m2
    //      / .wmo to Buildings/{uniformName} (the collision mesh that
    //      eventually lands as vmaps/<path>.vmo or .vmd).
    //   2. Write temp_gameobject_models (displayId → name) for the
    //      assembler to know which files to include.
    //
    // In this C# port we do NOT write the index file — gameobject_spawns.bin
    // is provided externally (placed next to the EXE or selected via the
    // UI). We DO still need to extract the .vmo/.vmd collision meshes so
    // the mmap-extractor can find them by path when it consumes
    // gameobject_spawns.bin. The dedup set (_writtenVmoFiles) makes this
    // a no-op for models that were already extracted during the per-map
    // MDDF/MODF pass.
    //
    // Call this ONCE after the per-map passes are done, before the
    // mmap-extractor runs.
    // -----------------------------------------------------------------------
    public async Task<int> ExtractGameObjectModelsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[MangosVmap] Extracting GameObject model collision meshes...");

        const string dbcPath = "DBFilesClient\\GameObjectDisplayInfo.dbc";
        if (!_archive.TryReadFile(dbcPath, out var dbcData))
        {
            _logger.LogWarning("[MangosVmap] GameObjectDisplayInfo.dbc not found — skipping GO model extraction");
            return 0;
        }

        DbcReader<GameObjectDisplayInfoRow> dbcReader;
        try
        {
            dbcReader = DbcReader<GameObjectDisplayInfoRow>.Parse(dbcData.Span);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MangosVmap] Failed to parse GameObjectDisplayInfo.dbc");
            return 0;
        }
        var rows = dbcReader.Rows.ToArray();
        _logger.LogInformation("[MangosVmap] GameObjectDisplayInfo.dbc: {Count} rows", rows.Length);

        // Faithful port of MaNGOS model.cpp::ExtractGameobjectModels: opens
        // Buildings/temp_gameobject_models in "wb" and writes one record per
        // gameobject model that successfully extracted (displayId +
        // length-prefixed uniform name). The TileAssembler later copies this
        // file to vmaps/temp_gameobject_models.
        string tempGameObjectModelsPath = Path.Combine(_outputDir, "Buildings", "temp_gameobject_models");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tempGameObjectModelsPath))!);

        int built = 0;
        int skippedAlready = 0;
        int failed = 0;

        using (var goWriter = new MangosVmapBuildingWriter.GameObjectModelsWriter(tempGameObjectModelsPath))
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                string modelPath = dbcReader.GetString(row, 1)?.Replace('/', '\\') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(modelPath) || modelPath.Length < 4)
                    continue;

                string ext = Path.GetExtension(modelPath).ToLowerInvariant();
                if (ext == ".mdx" || ext == ".mdl")
                {
                    modelPath = Path.ChangeExtension(modelPath, ".m2");
                    ext = ".m2";
                }

                uint displayId = (uint)row.Id;
                bool extracted = false;

                if (ext == ".wmo")
                {
                    string vmoRel = modelPath.Replace('\\', '/') + ".vmo";
                    if (!_writtenVmoFiles.Add(vmoRel))
                    {
                        skippedAlready++;
                        continue;
                    }
                    if (await TryBuildWmoAsync(modelPath, ct))
                    {
                        built++;
                        extracted = true;
                    }
                    else { failed++; _writtenVmoFiles.Remove(vmoRel); }
                }
                else if (ext == ".m2")
                {
                    string vmdRel = modelPath.Replace('\\', '/') + ".vmd";
                    if (!_writtenVmoFiles.Add(vmdRel))
                    {
                        skippedAlready++;
                        continue;
                    }
                    if (await TryBuildM2Async(modelPath, ct))
                    {
                        built++;
                        extracted = true;
                    }
                    else { failed++; _writtenVmoFiles.Remove(vmdRel); }
                }

                // MaNGOS writes the entry only if the file exists on disk
                // (it checks FileExists(buildings/path + name) before writing
                // the record). TryBuildWmo/BuildM2 just wrote it, so we
                // always append if extracted == true.
                if (extracted)
                {
                    string uniformName = MangosVmapBuildingWriter.GetUniformName(modelPath);
                    goWriter.Append(displayId, uniformName);
                }
            }
        }

        // TileAssembler copies Buildings/temp_gameobject_models to vmaps/.
        // We write the copy directly here since the C# vmap-extractor is a
        // single-phase pipeline (no TileAssembler step).
        string vmapsTempGoPath = Path.Combine(_outputDir, "vmaps", "temp_gameobject_models");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmapsTempGoPath))!);
        File.Copy(tempGameObjectModelsPath, vmapsTempGoPath, overwrite: true);

        _logger.LogInformation(
            "[MangosVmap] GameObject models: {Built} .vmo/.vmd built, {Skipped} already in MDDF/MODF, {Failed} build failures",
            built, skippedAlready, failed);
        return built;
    }

    // -----------------------------------------------------------------------
    // Build .vmo from a WMO file (root + group files).
    //
    // Mirrors MaNGOS TileAssembler::convertRawFile / WorldModel::WriteFile.
    // For each WMO group:
    //   1. Filter triangles by MOPY flags (NO_COLLISION=0x04, HINT=0x08, COLLIDE_HIT=0x20)
    //   2. If MOBR indices are present, only include the BSP-flagged triangles
    //   3. Remap the surviving vertices to a compact index space
    //   4. Write one GroupModel per group into a single .vmo
    // -----------------------------------------------------------------------
    private async Task<bool> TryBuildWmoAsync(string wmoName, CancellationToken ct)
    {
        try
        {
            var root = await _wmoParser.ParseRootAsync(wmoName, ct);
            if (!root.Success || root.Root == null) return false;

            var groups = new List<MangosVmoReader.GroupModel>();
            // Keep references to the ORIGINAL (unfiltered) WmoGroupFile data so
            // the raw .vmo writer can emit ALL groups (including those with no
            // collision triangles). MaNGOS preciseVectorData=true writes every
            // group's full MOVI/MOVT, regardless of MOPY flags. The filtered
            // GroupModel list above is only used for the final .vmo (vmaps/).
            var rawGroupData = new List<WmoGroupFile>();
            var doodadRefSet = new HashSet<ushort>();
            for (uint g = 0; g < root.Root.Header.GroupCount; g++)
            {
                ct.ThrowIfCancellationRequested();
                string groupPath = BuildWmoGroupPath(wmoName, (int)g);
                var grp = await _wmoParser.ParseGroupAsync(groupPath, (int)g, wmoName, ct);
                if (grp == null) continue;

                // Always keep the raw group data for the raw Buildings/ file
                // (mirrors MaNGOS wmo.cpp ConvertToVMAPGroupWmo which writes
                // EVERY group header + GRP/INDX/VERT chunks, not just collision).
                rawGroupData.Add(grp);

                // Only add to the filtered list for the FINAL vmaps/ output.
                var filtered = FilterWmoGroupCollision(grp);
                if (filtered == null) continue;

                groups.Add(filtered);

                // Faithful port of MaNGOS wmo.cpp:766-773: every loaded group's
                // MODR doodad indices get merged into a single set, which
                // Doodad::ExtractSet then iterates over.
                if (grp.DoodadReferences != null)
                {
                    foreach (var r in grp.DoodadReferences) doodadRefSet.Add(r);
                }
            }

            // Cache doodad data for the per-tile loop (the doodad extraction
            // happens per-WMO-placement, after the WMO's dir_bin record is
            // already written). Reset per map (see ExtractMapAsync).
            string wmoUniformName = MangosVmapBuildingWriter.GetUniformName(wmoName);
            _wmoDoodadData[wmoUniformName] = (root.Root, doodadRefSet.ToArray());

            // Even if the filtered list is empty (no collision-eligible triangles),
            // we still want to write the raw .vmo for Buildings/ with all groups
            // (mirrors MaNGOS preciseVectorData=true which writes every group).
            if (rawGroupData.Count == 0)
            {
                _logger.LogWarning("[MangosVmap] WMO {Wmo} produced no usable group geometry", wmoName);
                return false;
            }

            // Faithful port of MaNGOS ExtractSingleWmo:
            //   - Buildings/{uniform_name} receives the FINAL .vmo (VMAP_4.0 magic,
            //     WMOD/GMOD/VERT/TRIM/MBIH/GBIH chunks) — what the C++ TileAssembler
            //     reads in phase 2 to rebuild the per-tile spawn list, AND what the
            //     mmap-extractor reads at runtime (the C++ then copies this file to
            //     vmaps/ as part of TileAssembler::convertWorld2).
            //   - vmaps/{path}.vmo is a copy of the same bytes, with the extension
            //     rewritten from .wmo to .vmo so the mmap-extractor's lookup matches.
            //
            // The previous "raw" (VMAPt07) intermediate format was a Honorbuddy
            // artifact and is NOT how the C++ Mangos writes. The C++ writes the
            // same VMAP_4.0 file to Buildings/ and then copies it to vmaps/.
            string uniformName = MangosVmapBuildingWriter.GetUniformName(wmoName);

            // Build the final-format .vmo bytes (VMAP_4.0 + WMOD + GMOD + VERT + TRIM
            // + MBIH + GBIH) — exactly what MaNGOS Model::WriteFile / Wmo::WriteFile
            // produce. This is the SINGLE output format; there is no intermediate
            // "raw" representation in the C++ pipeline.
            byte[] vmoBytes = MangosVmoReader.ToBytes(root.Root.Header.WmoId, groups);

            // C++ Mangos uses .vmo for the compiled collision file in vmaps/ — replace
            // the extension so the mmap-extractor's spawn.Name + ".vmo" lookup matches.
            string vmoFlatName = wmoUniformName.Replace(".wmo", ".vmo", StringComparison.OrdinalIgnoreCase);
            string vmoPath = Path.Combine(_outputDir, "vmaps", vmoFlatName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmoPath))!);
            File.WriteAllBytes(vmoPath, vmoBytes);

            // Buildings/ also gets the same VMAP_4.0 bytes (the C++ writes here first
            // then copies to vmaps/ during TileAssembler). The dir_bin references
            // this file by spawn.Name = uniformName (with .wmo extension preserved);
            // MangosVmoReader doesn't care about the extension — it reads by magic.
            string buildingPath = Path.Combine(_outputDir, "Buildings", wmoUniformName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(buildingPath))!);
            File.WriteAllBytes(buildingPath, vmoBytes);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangosVmap] WMO build failed: {Wmo}", wmoName);
            return false;
        }
    }

    /// <summary>
    /// Filter a WMO group's triangles to keep only collision-eligible ones and
    /// remap its vertex array to a compact index space — same logic as the
    /// in-process AppendWmoGeometryAsync in MmapExtractorService, but here
    /// producing a self-contained GroupModel suitable for the .vmo file.
    /// </summary>
    private static MangosVmoReader.GroupModel? FilterWmoGroupCollision(WmoGroupFile grp)
    {
        // Determine which triangle indices survive the MOPY + BSP filter
        var validTris = new List<int>();
        if (grp.BspTriangleIndices != null && grp.BspTriangleIndices.Length > 0)
        {
            var seen = new HashSet<int>();
            foreach (var triIndex in grp.BspTriangleIndices)
            {
                if (triIndex >= grp.Materials.Length) continue;
                var flags = grp.Materials[triIndex].Flags;
                if ((flags & WmoMaterialFlag.NoCollision) != 0) continue;
                if (seen.Add(triIndex)) validTris.Add(triIndex);
            }
        }
        else
        {
            for (int t = 0; t < grp.Triangles.Length; t++)
            {
                var flags = grp.Materials[t].Flags;
                if ((flags & WmoMaterialFlag.NoCollision) != 0) continue;
                if ((flags & (WmoMaterialFlag.Hint | WmoMaterialFlag.CollideHit)) == 0) continue;
                validTris.Add(t);
            }
        }
        if (validTris.Count == 0) return null;

        // Collect and remap the referenced vertices
        var usedVertSet = new HashSet<ushort>(validTris.Count * 3);
        foreach (int t in validTris)
        {
            var tri = grp.Triangles[t];
            usedVertSet.Add(tri.I0);
            usedVertSet.Add(tri.I1);
            usedVertSet.Add(tri.I2);
        }

        var remap = new Dictionary<ushort, uint>(usedVertSet.Count);
        var newVerts = new List<Vector3>(usedVertSet.Count);
        foreach (ushort vi in usedVertSet)
        {
            if (vi >= grp.Vertices.Length) continue;
            remap[vi] = (uint)newVerts.Count;
            var v = grp.Vertices[vi];
            // Raw MOVT vertices — TerrainBuilder::transform() applies the
            // (rot, scale, pos) matrix later, so the .vmo must store
            // the original local coordinates unchanged.
            newVerts.Add(new Vector3(v.X, v.Y, v.Z));
        }

        var newTris = new List<MangosVmoReader.MeshTriangle>(validTris.Count);
        foreach (int t in validTris)
        {
            var tri = grp.Triangles[t];
            if (!remap.TryGetValue(tri.I0, out uint r0)
                || !remap.TryGetValue(tri.I1, out uint r1)
                || !remap.TryGetValue(tri.I2, out uint r2))
                continue;
            newTris.Add(new MangosVmoReader.MeshTriangle(r0, r1, r2));
        }

        return new MangosVmoReader.GroupModel
        {
            MogpFlags = grp.Header.Flags,
            GroupWMOID = (uint)grp.Header.GroupWmoId,
            BoundLo = new Vector3(grp.Header.BoundingBoxMin.X, grp.Header.BoundingBoxMin.Y, grp.Header.BoundingBoxMin.Z),
            BoundHi = new Vector3(grp.Header.BoundingBoxMax.X, grp.Header.BoundingBoxMax.Y, grp.Header.BoundingBoxMax.Z),
            Vertices = newVerts.ToArray(),
            Triangles = newTris.ToArray(),
        };
    }

    // -----------------------------------------------------------------------
    // Build .vmd from an M2 file.
    //
    // Mirrors MaNGOS M2::ConvertToVMapM2 / M2File::ConvertVmapModel: the
    // collision mesh is the M2 bounding mesh (nBoundingVertices /
    // nBoundingTriangles). The mmap-extractor later flips the winding
    // (isM2=true) so we don't apply any flip here.
    // -----------------------------------------------------------------------
    /// <summary>
    /// Resolve a doodad's raw model path (read from the WMO's MODN block via
    /// NameIndex offset) to a uniform file name and extract the M2 to both
    /// Buildings/ and vmaps/. Returns the uniform name on success, or null if
    /// the model is missing / extraction failed (MaNGOS C++ then skips the
    /// dir_bin record).
    /// </summary>
    private string? TryBuildM2ForDoodad(string modelPath)
    {
        // .mdl / .mdx → .m2 (MaNGOS model.cpp::ExtractSingleModel)
        string ext = Path.GetExtension(modelPath).ToLowerInvariant();
        if (ext == ".mdl" || ext == ".mdx")
            modelPath = Path.ChangeExtension(modelPath, ".m2");

        string vmdRelPath = modelPath.Replace('\\', '/') + ".vmd";
        // If already extracted during the per-tile or gameobject pass, just
        // return the uniform name (the C++ does the same dedup).
        string doodadUniform = MangosVmapBuildingWriter.GetUniformName(modelPath);
        if (_writtenVmoFiles.Contains(vmdRelPath))
            return doodadUniform;

        // TryBuildM2Async is async-only by signature (it goes through the
        // AdtParser's async IArchiveReader). Run it sync via .GetAwaiter()
        // for the doodad callback. The doodad path is bounded by ADT count
        // and rarely exceeds a few thousand entries per map.
        bool ok = TryBuildM2Async(modelPath, CancellationToken.None).GetAwaiter().GetResult();
        if (ok)
        {
            _writtenVmoFiles.Add(vmdRelPath);
            return doodadUniform;
        }
        return null;
    }

    private async Task<bool> TryBuildM2Async(string m2Name, CancellationToken ct)
    {
        try
        {
            if (!_m2Parser.TryParseBoundingMesh(m2Name, out var verts, out var indices))
                return false;
            if (verts.Length == 0 || indices.Length == 0)
                return false;

            var v = new Vector3[verts.Length / 3];
            for (int i = 0; i < v.Length; i++)
                v[i] = new Vector3(verts[i * 3], verts[i * 3 + 1], verts[i * 3 + 2]);

            var t = new MangosVmoReader.MeshTriangle[indices.Length / 3];
            for (int i = 0; i < t.Length; i++)
                t[i] = new MangosVmoReader.MeshTriangle(indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2]);

            // Compute the model-local AABB
            var lo = v[0];
            var hi = v[0];
            for (int i = 1; i < v.Length; i++)
            {
                lo = Vector3.Min(lo, v[i]);
                hi = Vector3.Max(hi, v[i]);
            }

            var grp = new MangosVmoReader.GroupModel
            {
                MogpFlags = 0,
                GroupWMOID = 0,
                BoundLo = lo,
                BoundHi = hi,
                Vertices = v,
                Triangles = t,
            };

            // Faithful port of MaNGOS ExtractSingleModel:
            //   - Buildings/{uniform_name} receives the FINAL .vmd (VMAP_4.0 magic,
            //     WMOD/GMOD/VERT/TRIM/MBIH/GBIH chunks) — what the C++ TileAssembler
            //     reads in phase 2 to rebuild the per-tile spawn list, AND what the
            //     mmap-extractor reads at runtime.
            //   - vmaps/{path}.vmd is a copy of the same bytes with the extension
            //     rewritten from .m2 to .vmd.
            //
            // The previous "raw" (VMAPt07) intermediate format was a Honorbuddy
            // artifact and is NOT how the C++ Mangos writes. There is no raw
            // intermediate in the C++ pipeline.
            string uniformName = MangosVmapBuildingWriter.GetUniformName(m2Name);

            // Build the final-format .vmd bytes (VMAP_4.0 + WMOD + GMOD + VERT + TRIM
            // + MBIH + GBIH) — exactly what MaNGOS M2::WriteFile / Model::WriteFile
            // produce. The M2's bounding-mesh indices (ushort per vertex) are written
            // into the GMOD group's TRIM chunk; no separate "raw" index truncation.
            byte[] vmdBytes = MangosVmoReader.ToBytes(0, new[] { grp });

            // C++ Mangos uses .vmd for the compiled collision file in vmaps/ — replace
            // the extension so the mmap-extractor's spawn.Name + ".vmd" lookup matches.
            string vmdFlatName = uniformName.Replace(".m2", ".vmd", StringComparison.OrdinalIgnoreCase);
            string vmdPath = Path.Combine(_outputDir, "vmaps", vmdFlatName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmdPath))!);
            File.WriteAllBytes(vmdPath, vmdBytes);

            // Buildings/ gets the same VMAP_4.0 bytes (the C++ writes here first
            // then copies to vmaps/ during TileAssembler). MangosVmoReader doesn't
            // care about the extension — it reads by magic.
            string buildingPath = Path.Combine(_outputDir, "Buildings", uniformName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(buildingPath))!);
            File.WriteAllBytes(buildingPath, vmdBytes);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangosVmap] M2 build failed: {M2}", m2Name);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // ModelSpawn construction
    // -----------------------------------------------------------------------

    // fixCoords — faithful port of MaNGOS vec3d.h::fixCoords: returns
    // (v.z, v.x, v.y) of the input. The C++ does NOT apply any -32G offset
    // to iPos when writing to .vmtree/.vmtile (TileAssembler.cpp:135-138
    // has the iPos += 32G line commented out as a TODO; only iBound gets
    // the +32G offset). Storing (-z-32G, -x-32G, y) here would diverge
    // byte-for-byte from the C++ reference output.
    private static (float X, float Y, float Z) FixCoords(float x, float y, float z)
        => (z, x, y);

    private static MangosModelSpawn BuildWmoSpawn(
        uint mapId, int tileX, int tileY, uint id, string wmoName, AdtModf modf)
    {
        // Mirrors C++ ModelInstance / ModelSpawn::WriteToFile exactly:
        //   pos  = fixCoords(Vec3D(x, y, z))           // = (Z, X, Y)
        //   iBound = fixCoords(bound) + (32G, 32G, 0)  // see TileAssembler.cpp:137
        // iPos is written as-is (no -32G), iBound gets the +32G offset.
        var (fx, fy, fz) = FixCoords(modf.PositionX, modf.PositionY, modf.PositionZ);
        var (bLoX, bLoY, bLoZ) = FixCoords(modf.LowerBoundsX, modf.LowerBoundsY, modf.LowerBoundsZ);
        var (bHiX, bHiY, bHiZ) = FixCoords(modf.UpperBoundsX, modf.UpperBoundsY, modf.UpperBoundsZ);
        const float HalfWorld = 32f * WowConstants.TileSize;
        return new MangosModelSpawn
        {
            Flags = 0u, // MOD_M2 bit is clear for WMO
            AdtId = (ushort)tileX,
            Id = id,
            Pos = new[] { fx, fy, fz },
            Rot = new[] { modf.RotationX, modf.RotationY, modf.RotationZ },
            Scale = modf.Scale,
            BoundLow  = new[] { bLoX + HalfWorld, bLoY + HalfWorld, bLoZ },
            BoundHigh = new[] { bHiX + HalfWorld, bHiY + HalfWorld, bHiZ },
            Name = wmoName.Replace('\\', '/')
        };
    }

    private static MangosModelSpawn BuildM2Spawn(
        uint mapId, int tileX, int tileY, uint id, string m2Name, AdtMddf mddf)
    {
        // M2 placements have no bbox in the .vmtile; iPos = fixCoords only.
        var (fx, fy, fz) = FixCoords(mddf.PositionX, mddf.PositionY, mddf.PositionZ);
        return new MangosModelSpawn
        {
            Flags = MangosModelSpawn.MOD_M2,
            AdtId = (ushort)tileX,
            Id = id,
            Pos = new[] { fx, fy, fz },
            Rot = new[] { mddf.RotationX, mddf.RotationY, mddf.RotationZ },
            Scale = mddf.Scale / 1024f,
            Name = m2Name.Replace('\\', '/')
        };
    }

    private static string BuildWmoGroupPath(string rootWmo, int groupIndex)
    {
        string dir = Path.GetDirectoryName(rootWmo) ?? string.Empty;
        string filename = Path.GetFileNameWithoutExtension(rootWmo);
        return string.IsNullOrEmpty(dir)
            ? $"{filename}_{groupIndex:D3}.wmo"
            : $"{dir}\\{filename}_{groupIndex:D3}.wmo";
    }
}
