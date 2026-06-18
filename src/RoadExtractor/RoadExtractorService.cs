using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Wdt;

namespace MaNGOS.Extractor.RoadExtractor;

public sealed class RoadExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly ILogger _logger;
    private readonly string _outputDir;

    private static readonly string[] RoadPatterns =
    {
        "road", "cobblestone", "path_cobble", "path_stone",
        "dirtpath", "dirt_path", "bridgefloor", "bridge_stone"
    };

    public RoadExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<RoadExtractorService>();
        _wdtReader = new WdtReader(archive);
        _outputDir = outputDir;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
        _logger.LogInformation("[Road] Output directory: {OutputDir}", outputDir);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default,
        int? onlyTileX = null,
        int? onlyTileY = null)
    {
        _logger.LogInformation("[Road] Starting road extraction for {MapName} (id={MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("[Road] Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        if (onlyTileX.HasValue && onlyTileY.HasValue)
            tiles = tiles.Where(t => t.X == onlyTileX.Value && t.Y == onlyTileY.Value).ToList();
        
        _logger.LogInformation("[Road] Found {Count} ADT tiles for {MapName}", tiles.Count, mapName);

        int successCount = 0, failCount = 0;
        int totalRoadChunks = 0, totalChunks = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Road));

            var (success, roadChunks, chunks) = await ProcessTileAsync(mapId, mapName, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Road));

            if (success) successCount++; else failCount++;
            totalRoadChunks += roadChunks; totalChunks += chunks;
        }

        _logger.LogInformation("[Road] Extraction complete for {MapName}: {Success} OK, {Failed} failed. " +
            "Road chunks: {RoadChunks}/{TotalChunks}",
            mapName, successCount, failCount, totalRoadChunks, totalChunks);
        return successCount;
    }

    private async Task<(bool ok, int roadChunkCount, int totalChunkCount)> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        string fileName = $"{mapId:D3}{tileY:D2}{tileX:D2}.road";
        string filePath = Path.Combine(_outputDir, fileName);

        if (File.Exists(filePath))
        {
            _logger.LogDebug("[Road] Skipping ({TileX},{TileY}) — already exists", tileX, tileY);
            return (true, 0, 256);
        }

        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX}_{tileY}.adt";

        if (!_archive.TryReadFile(adtPath, out var fileData))
        {
            _logger.LogWarning("[Road] Failed to read ADT ({TileX},{TileY})", tileX, tileY);
            // In C++, if ADT fails to load, no .road file is created
            return (false, 0, 256);
        }

        byte[] roadFlags = new byte[256];
        bool hasRoad = ExtractRoadMask(fileData.Span, roadFlags);

        int roadChunkCount = roadFlags.Count(b => b == 1);

        if (roadChunkCount > 0)
            _logger.LogInformation("[Road] ADT ({TileX},{TileY}): {RoadChunks}/256 road chunks detected",
                tileX, tileY, roadChunkCount);

        try
        {
            await File.WriteAllBytesAsync(filePath, roadFlags, ct);
            return (true, roadChunkCount, 256);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Road] Failed to write road file: ({TileX},{TileY})", tileX, tileY);
            return (false, roadChunkCount, 256);
        }
    }

    private static bool ExtractRoadMask(ReadOnlySpan<byte> data, byte[] roadMask)
    {
        Array.Clear(roadMask, 0, 256);

        if (data.Length < 20)
            return false;

        int mtexOffset = -1;
        uint mtexSize = 0;

        List<uint> mcnkOffsets = new List<uint>();

        uint pos = 0;
        uint fileSize = (uint)data.Length;

        while (pos + 8 <= fileSize)
        {
            uint magic = BitConverter.ToUInt32(data.Slice((int)pos, 4));
            uint size = BitConverter.ToUInt32(data.Slice((int)pos + 4, 4));

            uint remaining = fileSize - pos - 8;
            if (size > remaining)
                break;

            if (magic == 0x4D544558) // 'MTEX' -> M(0x4D), T(0x54), E(0x45), X(0x58) -> Little-endian uint32: 0x5845544D.
            {
                // Wait! In C++, `magic == 0x4D544558`.
                // In C++, magic is *(uint32*). On x86, it reads the bytes as little-endian.
                // Memory: 'M' (0x4D), 'T' (0x54), 'E' (0x45), 'X' (0x58).
                // *(uint32*) = 0x5845544D. So `magic == 0x4D544558` in C++ means memory has 0x58, 0x45, 0x54, 0x4D which is "XETM".
                // Let's use the exact same comparison as C++. BitConverter.ToUInt32 will give the exact same result as *(uint32*).
            }

            // Actually, let's just check the exact same integer as the C++ code to be byte-for-byte compatible.
            if (magic == 0x4D544558)
            {
                mtexOffset = (int)(pos + 8);
                mtexSize = size;
            }

            if (magic == 0x4D434E4B) // 'MCNK' magic check in C++
            {
                mcnkOffsets.Add(pos);
            }

            pos += 8 + size;
        }

        if (mtexOffset == -1 || mtexSize == 0)
            return false;

        // Build texture name table
        List<string> texNames = new List<string>();
        int p = mtexOffset;
        int end = mtexOffset + (int)mtexSize;
        while (p < end)
        {
            int strEnd = p;
            while (strEnd < end && data[strEnd] != 0) strEnd++;
            if (strEnd > p)
            {
                string texName = System.Text.Encoding.ASCII.GetString(data.Slice(p, strEnd - p));
                texNames.Add(texName);
            }
            else
            {
                texNames.Add(string.Empty);
            }
            p = strEnd + 1;
        }

        bool anyRoad = false;
        for (int ci = 0; ci < mcnkOffsets.Count && ci < 256; ++ci)
        {
            int chunkRow = ci / 16;
            int chunkCol = ci % 16;

            uint mcnkOff = mcnkOffsets[ci];
            uint mcnkSize = BitConverter.ToUInt32(data.Slice((int)mcnkOff + 4, 4));
            uint mcnkEnd = mcnkOff + 8 + mcnkSize;

            if (mcnkEnd > fileSize)
                continue;

            // ofsLayer at offset 0x1C in SMChunk header (mcnkOff + 8 + 0x1C)
            uint ofsLayer = BitConverter.ToUInt32(data.Slice((int)mcnkOff + 8 + 0x1C, 4));
            if (ofsLayer == 0)
                continue;

            uint mclyOff = mcnkOff + ofsLayer;
            if (mclyOff + 8 > mcnkEnd)
                continue;

            uint mclyMagic = BitConverter.ToUInt32(data.Slice((int)mclyOff, 4));
            uint mclySize = BitConverter.ToUInt32(data.Slice((int)mclyOff + 4, 4));

            if (mclyMagic != 0x4D434C59) // 'MCLY' magic check in C++
                continue;

            int numLayers = (int)(mclySize / 16); // MCLYEntry size is 16

            for (int l = 0; l < numLayers; ++l)
            {
                int layerOffset = (int)(mclyOff + 8 + l * 16);
                uint texIdx = BitConverter.ToUInt32(data.Slice(layerOffset, 4));
                if (texIdx < texNames.Count && IsRoadTexture(texNames[(int)texIdx]))
                {
                    roadMask[chunkRow * 16 + chunkCol] = 1;
                    anyRoad = true;
                    break;
                }
            }
        }

        return anyRoad;
    }

    private static bool IsRoadTexture(string textureName)
    {
        if (string.IsNullOrEmpty(textureName))
            return false;

        string lower = textureName.ToLowerInvariant();
        foreach (var pattern in RoadPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }
        return false;
    }

    internal void ClearCache() { }
}
