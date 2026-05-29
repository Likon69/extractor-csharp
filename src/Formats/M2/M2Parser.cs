using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Interfaces;

namespace MaNGOS.Extractor.Formats.M2;

/// <summary>
/// Minimal M2 parser that reads only the bounding (collision) mesh from a WotLK M2 file.
/// The bounding mesh is the hull used for VMap/mmap collision checks.
/// </summary>
public sealed class M2Parser
{
    private readonly IArchiveReader _archive;

    public M2Parser(IArchiveReader archive)
    {
        _archive = archive;
    }

    /// <summary>
    /// Reads the bounding collision mesh from an M2 file.
    /// Returns false if the file is not found, invalid, or has no bounding geometry.
    /// </summary>
    /// <param name="path">MPQ path to the .m2 file.</param>
    /// <param name="vertices">
    ///   Output flat vertex array: [x0,y0,z0, x1,y1,z1, ...] in model-local space.
    ///   These are in WoW model space (Y=forward, Z=up — transform is applied by the caller).
    /// </param>
    /// <param name="indices">
    ///   Output index array. Count is always a multiple of 3 (one entry per index, 3 per triangle).
    ///   Note: nBoundingTriangles in the M2 header is the number of uint16 indices, not triangle count.
    /// </param>
    public bool TryParseBoundingMesh(string path, out float[] vertices, out ushort[] indices)
    {
        vertices = Array.Empty<float>();
        indices = Array.Empty<ushort>();

        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
            return false;

        var span = data.Span;

        // WotLK M2 magic is "MD20"
        if (span.Length < 236
            || span[0] != 'M' || span[1] != 'D' || span[2] != '2' || span[3] != '0')
            return false;

        // ModelHeaderOthers byte layout (pragma pack 1) — WotLK offsets:
        //   offset 216 : uint32 nBoundingTriangles   (= number of uint16 indices, NOT triangle count)
        //   offset 220 : uint32 ofsBoundingTriangles
        //   offset 224 : uint32 nBoundingVertices
        //   offset 228 : uint32 ofsBoundingVertices
        // Offset 216 = 4(id)+4(ver)+4*9(nameLen..nViews)+4*30(nColors..ofsTexAnimLookup)+56(floats[14])
        var reader = new SpanReader(data);

        reader.Seek(216);
        int nBoundingIndices = (int)reader.ReadUInt32();
        int ofsBoundingTriangles = (int)reader.ReadUInt32();
        int nBoundingVertices = (int)reader.ReadUInt32();
        int ofsBoundingVertices = (int)reader.ReadUInt32();

        if (nBoundingIndices <= 0 || nBoundingVertices <= 0
            || nBoundingIndices % 3 != 0)
        {
            // Valid M2 file but no bounding collision mesh — normal for decorative objects.
            // Return true with empty arrays so callers can distinguish from "file not found".
            return true;
        }

        // Bounds check
        if (ofsBoundingVertices + nBoundingVertices * 12 > span.Length)
            return false;
        if (ofsBoundingTriangles + nBoundingIndices * 2 > span.Length)
            return false;

        // Parse bounding vertices (Vec3D = 3 floats = 12 bytes each)
        vertices = new float[nBoundingVertices * 3];
        reader.Seek(ofsBoundingVertices);
        for (int i = 0; i < nBoundingVertices; i++)
        {
            vertices[i * 3 + 0] = reader.ReadFloat();
            vertices[i * 3 + 1] = reader.ReadFloat();
            vertices[i * 3 + 2] = reader.ReadFloat();
        }

        // Parse bounding indices (uint16 each)
        indices = new ushort[nBoundingIndices];
        reader.Seek(ofsBoundingTriangles);
        for (int i = 0; i < nBoundingIndices; i++)
            indices[i] = reader.ReadUInt16();

        return true;
    }
}
