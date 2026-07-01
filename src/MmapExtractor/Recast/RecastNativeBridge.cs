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

    /// <summary>
    /// Loads an mmtile (mmapVer=5), builds a dtNavMesh, and runs a straight-path query.
    /// Returns the number of path points written to outPath, or a negative error code.
    /// outPath receives (x,y,z) per point.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int TestPathfinding(
        byte* mmtileData, int mmtileSize,
        float startX, float startY, float startZ,
        float endX,   float endY,   float endZ,
        float* outPath, int maxPathPts);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int TestPathfindingTwoFiles(
        byte* tile1Data, int tile1Size,
        byte* tile2Data, int tile2Size,
        float startX, float startY, float startZ,
        float endX,   float endY,   float endZ,
        float* outPath, int maxPathPts);
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

    public int MaxVertsPerPoly;
    /// <summary>Border size in voxels. Expanded bbox = tile bbox ± BorderSize*CellSize. Passed to rcBuildRegions.</summary>
    public int BorderSize;

    // ---- World-unit Detour params (Mangos C++ hardcodes 2.22155 / 0.4 / 1.0) ----
    // Stored in the Detour tile header and used by pathfinding queries.
    // Per-map overrides (e.g. map 562 walkableRadius=0) are applied in C# before
    // the call.
    public float WalkableHeightWorld;
    public float WalkableRadiusWorld;
    public float WalkableClimbWorld;

    /// <summary>1 = build BVH (Mangos default), 0 = skip.</summary>
    public int BuildBvTree;
}
