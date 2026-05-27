using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.MmapExtractor.Recast;

internal static class RecastNative
{
    private const string DllName = "RecastBuilderDll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe bool BuildTile(
        RecastBuildParams* p,
        float* verts, int vertCount,
        int* tris, int triCount,
        byte* areaIds,
        float* offMeshConVerts,
        float* offMeshConRads,
        byte* offMeshConDirs,
        byte* offMeshConAreas,
        ushort* offMeshConFlags,
        int offMeshConCount,
        byte** outData, int* outSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void FreeBuffer(void* buffer);
}

[StructLayout(LayoutKind.Sequential)]
public struct RecastBuildParams
{
    // Must match native RecastBuilderDll.h exactly.
    public float BoundingBoxMinX;
    public float BoundingBoxMinY;
    public float BoundingBoxMinZ;
    public float BoundingBoxMaxX;
    public float BoundingBoxMaxY;
    public float BoundingBoxMaxZ;

    public float CellSize;
    public float CellHeight;
    public float WalkableSlopeAngle;
    public int WalkableHeight;
    public int WalkableRadius;
    public int WalkableClimb;
    public int TileX;
    public int TileY;

    public float MinRegionArea;
    public float MergeRegionArea;
    public float MaxSimplificationError;

    public int MaxVertsPerPoly;  // MUST be last field
}
