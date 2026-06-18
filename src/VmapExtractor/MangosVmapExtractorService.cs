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
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<MangosVmapExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _wmoParser = new WmoParser(archive, loggerFactory.CreateLogger<WmoParser>());
        _m2Parser = new M2Parser(archive);
        _outputDir = outputDir;
        _logger.LogInformation("[MangosVmap] Output directory: {OutputDir}", outputDir);
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

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            var perTile = new List<MangosModelSpawn>();
            string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX}_{tileY}.adt";
            var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);
            if (!result.Success || result.Tile == null)
                continue;

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
        }

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
            //   - Buildings/{uniform_name} receives the RAW .vmo (VMAPt07 magic,
            //     root header + per-group GRP/INDX/VERT chunks) — what the C++
            //     TileAssembler picks up in phase 2 to rebuild the BIH.
            //   - vmaps/{path}.vmo receives the FINAL format (VMAP_4.0 magic,
            //     WMOD/GMOD/VERT/TRIM/MBIH/GBIH chunks) — what the mmap-extractor
            //     reads at runtime.
            string uniformName = MangosVmapBuildingWriter.GetUniformName(wmoName);

            // Build the raw .vmo for Buildings/ from the ORIGINAL unfiltered data
            // (MaNGOS wmo.cpp:152-165 + 284-..., preciseVectorData=true default).
            // This writes ALL triangles (not just collision) and the real MOBA
            // BSP tree, matching the C++ byte-for-byte.
            var rawGroups = new List<MangosRawVmoWriter.WmoGroupRawData>(rawGroupData.Count);
            foreach (var grp in rawGroupData)
            {
                // MOBA: each batch is 12 uint16s. The C++ writes MobaEx as int32,
                // one per batch, using MOBA[i] where i starts at 8 and steps by 12.
                // Since MOBA is uint16*, MOBA[i] reads uint16 at index i. The loop
                // `for (int i = 8; i < moba_size; i += 12)` with moba_size in BYTES
                // is a known C++ bug (compares uint16 index to byte count), but the
                // actual indices read are 8, 20, 32, ... in uint16 units. So in C#
                // terms: RawMoba[8 + k*12]. Clamp to array bounds to avoid
                // IndexOutOfRangeException for the out-of-bounds reads the C++
                // silently does (undefined behavior in C, garbage in the output).
                int mobaBatch = grp.RawMoba.Length / 12;
                int[] moba = new int[mobaBatch];
                for (int k = 0; k < mobaBatch; k++)
                {
                    int idx = 8 + k * 12;
                    moba[k] = idx < grp.RawMoba.Length ? grp.RawMoba[idx] : 0;
                }

                // MOVI: 3 uint16 per triangle. C++ writes the full MOVI array (all
                // triangles). C# WmoTriangle has I0/I1/I2 as ushort.
                var gIdx = new ushort[grp.Triangles.Length * 3];
                for (int i = 0; i < grp.Triangles.Length; i++)
                {
                    gIdx[i * 3 + 0] = grp.Triangles[i].I0;
                    gIdx[i * 3 + 1] = grp.Triangles[i].I1;
                    gIdx[i * 3 + 2] = grp.Triangles[i].I2;
                }

                // MOVT: 3 floats per vertex.
                var gVerts = new Vector3[grp.Vertices.Length];
                for (int i = 0; i < grp.Vertices.Length; i++)
                {
                    gVerts[i] = new Vector3(grp.Vertices[i].X, grp.Vertices[i].Y, grp.Vertices[i].Z);
                }

                rawGroups.Add(new MangosRawVmoWriter.WmoGroupRawData
                {
                    MogpFlags = grp.Header.Flags,
                    GroupWMOID = grp.Header.GroupWmoId,
                    BoundMin = new Vector3(grp.Header.BoundingBoxMin.X, grp.Header.BoundingBoxMin.Y, grp.Header.BoundingBoxMin.Z),
                    BoundMax = new Vector3(grp.Header.BoundingBoxMax.X, grp.Header.BoundingBoxMax.Y, grp.Header.BoundingBoxMax.Z),
                    LiquFlags = 0,
                    MobaNodes = moba,
                    Indices = gIdx,
                    Vertices = gVerts,
                });
            }
            byte[] rawVmoBytes = MangosRawVmoWriter.WriteWmo(root.Root.Header.WmoId, rawGroups);

            // Final format for vmaps/
            byte[] vmoBytes = MangosVmoReader.ToBytes(root.Root.Header.WmoId, groups);

            string vmoPath = Path.Combine(_outputDir, "vmaps", wmoName.Replace('\\', '/') + ".vmo");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmoPath))!);
            File.WriteAllBytes(vmoPath, vmoBytes);

            string buildingPath = Path.Combine(_outputDir, "Buildings", wmoUniformName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(buildingPath))!);
            File.WriteAllBytes(buildingPath, rawVmoBytes);
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
            //   - Buildings/{uniform_name} receives the RAW .vmd (VMAPt07 magic,
            //     simple vertex+index chunks) — what the C++ TileAssembler picks
            //     up in phase 2 to rebuild the BIH.
            //   - vmaps/{path}.vmd receives the FINAL format (VMAP_4.0 magic,
            //     WMOD/GMOD/VERT/TRIM/MBIH chunks) — what the mmap-extractor
            //     reads at runtime.
            string uniformName = MangosVmapBuildingWriter.GetUniformName(m2Name);

            // Raw format for Buildings/ (MaNGOS model.cpp:121-184)
            // nIndices is uint16, M2 bounding mesh rarely exceeds 65k.
            if (indices.Length > 0xFFFF)
            {
                _logger.LogWarning("[MangosVmap] M2 {M2} has {N} indices (>65535), truncating for raw .vmd", m2Name, indices.Length);
            }
            var rawIndices = new ushort[Math.Min(indices.Length, 0xFFFF)];
            for (int i = 0; i < rawIndices.Length; i++) rawIndices[i] = (ushort)indices[i];
            var rawM2 = new MangosRawVmoWriter.M2RawData
            {
                NVertices = (uint)v.Length,
                Indices = rawIndices,
                Vertices = v,
            };
            byte[] rawVmdBytes = MangosRawVmoWriter.WriteM2(rawM2);

            // Final format for vmaps/
            byte[] vmdBytes = MangosVmoReader.ToBytes(0, new[] { grp });

            string vmdPath = Path.Combine(_outputDir, "vmaps", m2Name.Replace('\\', '/') + ".vmd");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(vmdPath))!);
            File.WriteAllBytes(vmdPath, vmdBytes);

            string buildingPath = Path.Combine(_outputDir, "Buildings", uniformName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(buildingPath))!);
            File.WriteAllBytes(buildingPath, rawVmdBytes);
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
