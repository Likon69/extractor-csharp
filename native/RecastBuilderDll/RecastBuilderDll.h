#pragma once

#ifdef RECASTBUILDERDLL_EXPORTS
#define RECASTBUILDERDLL_API __declspec(dllexport)
#else
#define RECASTBUILDERDLL_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

struct RecastBuildParams
{
    float BoundingBoxMinX, BoundingBoxMinY, BoundingBoxMinZ;
    float BoundingBoxMaxX, BoundingBoxMaxY, BoundingBoxMaxZ;
    float CellSize;
    float CellHeight;
    float WalkableSlopeAngle;
    int WalkableHeight;
    int WalkableRadius;
    int WalkableClimb;
    int TileX;
    int TileY;
    float MinRegionArea;
    float MergeRegionArea;
    float MaxSimplificationError;
    int MaxVertsPerPoly;
};

RECASTBUILDERDLL_API bool BuildTile(
    RecastBuildParams* params,
    float* verts, int vertCount,
    int* tris, int triCount,
    unsigned char* areaIds,
    unsigned char** outData, int* outSize);

RECASTBUILDERDLL_API void FreeBuffer(void* buffer);

#ifdef __cplusplus
}
#endif