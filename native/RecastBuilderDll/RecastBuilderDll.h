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

    RECAST_API void FreeBuffer(void* ptr);
}
