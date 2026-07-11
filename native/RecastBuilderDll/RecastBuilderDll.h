#pragma once

#ifdef RECASTBUILDERDLL_EXPORTS
#define RECAST_API __declspec(dllexport)
#else
#define RECAST_API __declspec(dllimport)
#endif

#include <cstdint>

// Must match MaNGOS.Extractor.MmapExtractor.Recast.RecastBuildParams (C# StructLayout.Sequential).
struct RecastBuildParams
{
    float BoundingBoxMinX;
    float BoundingBoxMinY;
    float BoundingBoxMinZ;
    float BoundingBoxMaxX;
    float BoundingBoxMaxY;
    float BoundingBoxMaxZ;

    float CellSize;
    float CellHeight;
    float WalkableSlopeAngle;
    int   WalkableHeight;
    int   WalkableRadius;
    int   WalkableClimb;
    int   TileX;
    int   TileY;

    float MinRegionArea;
    float MergeRegionArea;
    float MaxSimplificationError;

    int   MaxVertsPerPoly;
    int   BorderSize;  // cells; expand bbox by BorderSize*cs on each XZ side, pass to rcBuildRegions
};

enum RecastBuildResult
{
    RECAST_BUILD_SUCCESS = 0,
    RECAST_BUILD_EMPTY = 1,
    RECAST_BUILD_BAD_INPUT = -1,
    RECAST_BUILD_OUT_OF_MEMORY = -2,
    RECAST_BUILD_MERGE_FAILED = -3,
    RECAST_BUILD_CREATE_NAV_DATA_FAILED = -4
};

extern "C"
{
    RECAST_API bool BuildTile(
        const RecastBuildParams* params,
        const float* verts, int vertCount,
        const int*   tris,  int triCount,
        const uint8_t* areaIds,
        const float* offMeshConVerts,
        const float* offMeshConRads,
        const uint8_t* offMeshConDirs,
        const uint8_t* offMeshConAreas,
        const uint16_t* offMeshConFlags,
        int offMeshConCount,
        uint8_t** outData, int* outSize);

    RECAST_API int BuildTileDetailed(
        const RecastBuildParams* params,
        const float* verts, int vertCount,
        const int*   tris,  int triCount,
        const uint8_t* areaIds,
        const float* offMeshConVerts,
        const float* offMeshConRads,
        const uint8_t* offMeshConDirs,
        const uint8_t* offMeshConAreas,
        const uint16_t* offMeshConFlags,
        int offMeshConCount,
        uint8_t** outData, int* outSize);

    RECAST_API void FreeBuffer(void* ptr);

    // Loads an mmtile (mmapVer=5 format), builds a dtNavMesh from all sub-tiles, and runs a
    // findStraightPath between start and end.  Returns the number of straight-path points written
    // to outPath (x,y,z per point), or a negative error code:
    //  -1  bad input
    //  -2  mmtile too small
    //  -3  dtAllocNavMesh failed
    //  -4  dtNavMesh::init failed
    //  -5  dtAllocNavMeshQuery failed
    //  -6  dtNavMeshQuery::init failed
    //  -7  no poly found near start or end
    //  -8  findPath failed or returned empty path
    RECAST_API int TestPathfinding(
        const uint8_t* mmtileData, int mmtileSize,
        float startX, float startY, float startZ,
        float endX,   float endY,   float endZ,
        float* outPath, int maxPathPts);

    // Loads two separate mmtile files into a single dtNavMesh so that Detour
    // resolves the cross-ADT tile links, then runs findStraightPath.
    // Error codes same as TestPathfinding.
    RECAST_API int TestPathfindingTwoFiles(
        const uint8_t* tile1Data, int tile1Size,
        const uint8_t* tile2Data, int tile2Size,
        float startX, float startY, float startZ,
        float endX,   float endY,   float endZ,
        float* outPath, int maxPathPts);
}
