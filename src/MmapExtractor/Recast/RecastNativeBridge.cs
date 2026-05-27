using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.MmapExtractor.Recast;

internal static class RecastNative
{
    private const string DllName = "RecastBuilderDll";

    [DllImport(DllName)]
    public static extern unsafe bool BuildTile(
        RecastBuildParams* p,
        float* verts, int vertCount,
        int* tris, int triCount,
        byte* areaIds,
        byte** outData, int* outSize);

    [DllImport(DllName)]
    public static extern unsafe void FreeBuffer(void* buffer);
}

[StructLayout(LayoutKind.Sequential)]
public struct RecastBuildParams
{
    public float CellSize;
    public float CellHeight;
    public float WalkableSlopeAngle;
    public int WalkableHeight;
    public int WalkableRadius;
    public int WalkableClimb;
    public int MinRegionArea;    // cell count, e.g. rcSqr(20) = 400
    public int MergeRegionArea;  // cell count, e.g. rcSqr(40) = 1600
    public float MaxSimplificationError;
    public int TileX;
    public int TileY;
    public float BoundingBoxMinX;
    public float BoundingBoxMinY;
    public float BoundingBoxMinZ;
    public float BoundingBoxMaxX;
    public float BoundingBoxMaxY;
    public float BoundingBoxMaxZ;
    public int MaxVertsPerPoly;  // MUST be last field
}