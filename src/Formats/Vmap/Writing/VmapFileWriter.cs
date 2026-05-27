using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Vmap.Models;
using MaNGOS.Extractor.Formats.Wmo.Models;

namespace MaNGOS.Extractor.Formats.Vmap.Writing;

/// <summary>
/// Writes VMAP (visibility map) files from parsed WMO and M2 data.
/// Produces raw binary files that are later compiled by the server.
/// </summary>
public sealed class VmapFileWriter
{
    private readonly ILogger<VmapFileWriter> _logger;
    private readonly string _outputDir;

    public VmapFileWriter(string outputDir, ILogger<VmapFileWriter> logger)
    {
        _outputDir = outputDir;
        _logger = logger;

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    /// <summary>
    /// Writes VMAP tile files for a map.
    /// </summary>
    /// <param name="mapId">Map ID.</param>
    /// <param name="tiles">Dictionary of (tileX, tileY) -> VmapTile.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of tiles written.</returns>
    public async Task<int> WriteVmapFilesAsync(uint mapId, Dictionary<(int, int), VmapTile> tiles, CancellationToken ct = default)
    {
        int written = 0;

        foreach (var kvp in tiles)
        {
            ct.ThrowIfCancellationRequested();

            string dirPath = Path.Combine(_outputDir, $"{mapId:D3}");
            if (!Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            string fileName = $"{mapId:D3}_{kvp.Key.Item1:D2}_{kvp.Key.Item2:D2}.vmap";
            string filePath = Path.Combine(dirPath, fileName);

            await WriteTileAsync(kvp.Value, filePath, ct);
            written++;
        }

        _logger.LogInformation("Wrote {Count} VMAP tiles for map {MapId}", written, mapId);
        return written;
    }

    private async Task WriteTileAsync(VmapTile tile, string filePath, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            using var writer = new BinaryWriter(stream);

            // Write header: "VMAPt07" + group count + build
            writer.Write(VmapTileHeader.Magic);
            writer.Write(tile.Groups.Length);
            writer.Write(WowConstants.TargetBuild);

            // Write group data (WMO references)
            foreach (var group in tile.Groups)
            {
                WriteGroupData(writer, group);
            }

            // Write model placements (M2 references)
            foreach (var model in tile.Models)
            {
                WriteModelPlacement(writer, model);
            }
        }, ct);
    }

    private void WriteGroupData(BinaryWriter writer, VmapGroupData group)
    {
        // Format matches C++ ConvertToVMAPGroupWmo
        writer.Write(group.Flags);          // mogpFlags
        writer.Write(group.GroupWmoId);     // groupWMOID

        // Bounding box
        writer.Write(group.BoundingBoxMin.X);
        writer.Write(group.BoundingBoxMin.Y);
        writer.Write(group.BoundingBoxMin.Z);
        writer.Write(group.BoundingBoxMax.X);
        writer.Write(group.BoundingBoxMax.Y);
        writer.Write(group.BoundingBoxMax.Z);

        writer.Write(group.LiquidFlags);    // liquflags

        // GRP section: batch counts
        int batchCount = group.BatchCount;
        int mobaSize = batchCount * 4 + 4;
        writer.Write(0x20505247u);          // "GRP "
        writer.Write(mobaSize);
        writer.Write(batchCount);
        if (group.MobaData != null)
            foreach (var b in group.MobaData) writer.Write(b);

        // INDX section: triangle indices
        int nIndexes = group.Indices?.Length ?? 0;
        writer.Write(0x58444E49u);          // "INDX"
        writer.Write(4 + nIndexes * 2);
        writer.Write(nIndexes);
        if (group.Indices != null)
            foreach (var idx in group.Indices) writer.Write(idx);

        // VERT section: vertex positions
        int nVertices = (group.Vertices?.Length ?? 0) / 3;
        writer.Write(0x54524556u);          // "VERT"
        writer.Write(4 + nVertices * 12);
        writer.Write(nVertices);
        if (group.Vertices != null)
            foreach (var v in group.Vertices) writer.Write(v);
    }

    private void WriteModelPlacement(BinaryWriter writer, VmapModelPlacement model)
    {
        // Model name (null-terminated)
        byte[] nameBytes = Encoding.ASCII.GetBytes(model.Name);
        writer.Write(nameBytes);
        writer.Write((byte)0);

        // Position
        writer.Write(model.PositionX);
        writer.Write(model.PositionY);
        writer.Write(model.PositionZ);

        // Rotation
        writer.Write(model.RotationY);
        writer.Write(model.RotationX);
        writer.Write(model.RotationZ);

        // Scale
        writer.Write(model.Scale);

        // Flags
        writer.Write(model.Flags);
    }

    /// <summary>
    /// Converts WMO data to VMAP group entries.
    /// </summary>
    public static VmapGroupData[] FromWmoRoot(WmoRootFile root)
    {
        var groups = new VmapGroupData[root.Header.GroupCount];

        for (uint i = 0; i < root.Header.GroupCount; i++)
        {
            groups[i] = new VmapGroupData
            {
                Name = $"{root.FileName}{i:D3}",
                Flags = 0, // Will be set by group parser
                BoundingBoxMin = root.Header.BoundingBoxMin,
                BoundingBoxMax = root.Header.BoundingBoxMax,
                LiquidFlags = root.Header.WmoId
            };
        }

        return groups;
    }

    /// <summary>
    /// Converts a WMO instance (from ADT MODF) to a VMAP group placement.
    /// </summary>
    public static VmapGroupData FromWmoInstance(WmoRootFile root, WmoGroupFile group, int instanceId)
    {
        return new VmapGroupData
        {
            Name = group.Name,
            Flags = GetGroupFlags(group.Header),
            BoundingBoxMin = group.Header.BoundingBoxMin,
            BoundingBoxMax = group.Header.BoundingBoxMax,
            LiquidFlags = group.Header.LiquidType
        };
    }

    /// <summary>
    /// Converts a doodad placement (MDDF) to a VMAP model placement.
    /// </summary>
    public static VmapModelPlacement FromDoodadPlacement(AdtMddf placement, string modelName)
    {
        return new VmapModelPlacement
        {
            Name = modelName,
            PositionX = placement.PositionX,
            PositionY = placement.PositionY,
            PositionZ = placement.PositionZ,
            RotationY = placement.RotationY,
            RotationX = placement.RotationX,
            RotationZ = placement.RotationZ,
            Scale = placement.Scale,
            Flags = 1 // MOD_M2
        };
    }

    private static uint GetGroupFlags(WmoGroupHeader header)
    {
        uint flags = 0;
        if (header.IsIndoor) flags |= 0x00000001;
        if (header.HasLiquids) flags |= 0x00000004;
        return flags;
    }
}