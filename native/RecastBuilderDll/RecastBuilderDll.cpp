#define RECASTBUILDERDLL_EXPORTS
#include "RecastBuilderDll.h"

#include "Recast.h"
#include "DetourNavMesh.h"
#include "DetourNavMeshBuilder.h"

#include <cstring>
#include <vector>

extern "C"
{

bool BuildTile(
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
    uint8_t** outData, int* outSize)
{
    if (!params || !verts || !tris || !outData || !outSize)
        return false;

    *outData = nullptr;
    *outSize = 0;

    rcContext ctx;

    float bmin[3] = { params->BoundingBoxMinX, params->BoundingBoxMinY, params->BoundingBoxMinZ };
    float bmax[3] = { params->BoundingBoxMaxX, params->BoundingBoxMaxY, params->BoundingBoxMaxZ };

    int gridWidth, gridHeight;
    rcCalcGridSize(bmin, bmax, params->CellSize, &gridWidth, &gridHeight);

    rcHeightfield* hf = rcAllocHeightfield();
    if (!hf) return false;

    if (!rcCreateHeightfield(&ctx, *hf, gridWidth, gridHeight, bmin, bmax, params->CellSize, params->CellHeight))
    {
        rcFreeHeightField(hf);
        return false;
    }

    // Copy area ids so we can safely cast — caller owns the original buffer.
    uint8_t* areas = new uint8_t[triCount];
    if (areaIds)
        memcpy(areas, areaIds, triCount);
    else
        memset(areas, RC_WALKABLE_AREA, triCount);

    rcClearUnwalkableTriangles(&ctx, params->WalkableSlopeAngle, verts, vertCount, tris, triCount, areas);

    if (!rcRasterizeTriangles(&ctx, verts, vertCount, tris, areas, triCount, *hf, params->WalkableClimb))
    {
        delete[] areas;
        rcFreeHeightField(hf);
        return false;
    }
    delete[] areas;

    rcFilterLowHangingWalkableObstacles(&ctx, params->WalkableClimb, *hf);
    rcFilterWalkableLowHeightSpans(&ctx, params->WalkableHeight, *hf);
    rcFilterLedgeSpans(&ctx, params->WalkableHeight, params->WalkableClimb, *hf);

    rcCompactHeightfield* chf = rcAllocCompactHeightfield();
    if (!chf) { rcFreeHeightField(hf); return false; }

    if (!rcBuildCompactHeightfield(&ctx, params->WalkableHeight, params->WalkableClimb, *hf, *chf))
    {
        rcFreeCompactHeightfield(chf);
        rcFreeHeightField(hf);
        return false;
    }

    if (!rcErodeWalkableArea(&ctx, params->WalkableRadius, *chf))
    {
        rcFreeCompactHeightfield(chf);
        rcFreeHeightField(hf);
        return false;
    }

    rcFreeHeightField(hf);

    if (!rcBuildDistanceField(&ctx, *chf))
    {
        rcFreeCompactHeightfield(chf);
        return false;
    }

    if (!rcBuildRegions(&ctx, *chf, 0, (int)params->MinRegionArea, (int)params->MergeRegionArea))
    {
        rcFreeCompactHeightfield(chf);
        return false;
    }

    rcContourSet* cset = rcAllocContourSet();
    if (!cset) { rcFreeCompactHeightfield(chf); return false; }

    float maxError = params->MaxSimplificationError > 0.f ? params->MaxSimplificationError : 8.f;
    if (!rcBuildContours(&ctx, *chf, maxError, 41, *cset, RC_CONTOUR_TESS_WALL_EDGES))
    {
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    int nvp = params->MaxVertsPerPoly > 0 ? params->MaxVertsPerPoly : 6;

    rcPolyMesh* pmesh = rcAllocPolyMesh();
    if (!pmesh) { rcFreeContourSet(cset); rcFreeCompactHeightfield(chf); return false; }

    if (!rcBuildPolyMesh(&ctx, *cset, nvp, *pmesh))
    {
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    rcPolyMeshDetail* dmesh = rcAllocPolyMeshDetail();
    if (!dmesh) { rcFreePolyMesh(pmesh); rcFreeContourSet(cset); rcFreeCompactHeightfield(chf); return false; }

    float detailSampleDist = params->CellSize * 16.f;
    float detailSampleMaxError = params->CellHeight * 1.f;
    if (!rcBuildPolyMeshDetail(&ctx, *pmesh, *chf, detailSampleDist, detailSampleMaxError, *dmesh))
    {
        rcFreePolyMeshDetail(dmesh);
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    rcFreeContourSet(cset);
    rcFreeCompactHeightfield(chf);

    for (int i = 0; i < pmesh->npolys; i++)
    {
        if (pmesh->areas[i] == RC_WALKABLE_AREA)
            pmesh->areas[i] = 1;
        // area 2 = NAV_WATER → flags 0x04 (swim), else NAV_GROUND → flags 0x01
        pmesh->flags[i] = (pmesh->areas[i] == 2) ? 0x04 : 0x01;
    }

    dtNavMeshCreateParams navParams;
    memset(&navParams, 0, sizeof(navParams));
    navParams.tileX           = params->TileX;
    navParams.tileY           = params->TileY;
    navParams.verts           = pmesh->verts;
    navParams.vertCount       = pmesh->nverts;
    navParams.polys           = pmesh->polys;
    navParams.polyAreas       = pmesh->areas;
    navParams.polyFlags       = pmesh->flags;
    navParams.polyCount       = pmesh->npolys;
    navParams.nvp             = pmesh->nvp;
    navParams.detailMeshes    = dmesh->meshes;
    navParams.detailVerts     = dmesh->verts;
    navParams.detailVertsCount = dmesh->nverts;
    navParams.detailTris      = dmesh->tris;
    navParams.detailTriCount  = dmesh->ntris;
    memcpy(navParams.bmin, pmesh->bmin, sizeof(float) * 3);
    memcpy(navParams.bmax, pmesh->bmax, sizeof(float) * 3);
    navParams.cs              = pmesh->cs;
    navParams.ch              = pmesh->ch;
    navParams.walkableHeight  = params->WalkableHeight * params->CellHeight;
    navParams.walkableRadius  = 0.400f;
    navParams.walkableClimb   = params->WalkableClimb * params->CellHeight;
    navParams.buildBvTree     = true;

    std::vector<unsigned int> offMeshUserIds;
    if (offMeshConCount > 0 && offMeshConVerts && offMeshConRads && offMeshConDirs && offMeshConAreas && offMeshConFlags)
    {
        offMeshUserIds.resize(offMeshConCount);
        for (int i = 0; i < offMeshConCount; ++i)
            offMeshUserIds[i] = (unsigned int)(i + 1);

        navParams.offMeshConVerts  = offMeshConVerts;
        navParams.offMeshConRad    = offMeshConRads;
        navParams.offMeshConDir    = offMeshConDirs;
        navParams.offMeshConAreas  = offMeshConAreas;
        navParams.offMeshConFlags  = offMeshConFlags;
        navParams.offMeshConUserID = offMeshUserIds.data();
        navParams.offMeshConCount  = offMeshConCount;
    }

    uint8_t* navData  = nullptr;
    int      navDataSize = 0;
    if (!dtCreateNavMeshData(&navParams, &navData, &navDataSize))
    {
        rcFreePolyMeshDetail(dmesh);
        rcFreePolyMesh(pmesh);
        return false;
    }

    rcFreePolyMeshDetail(dmesh);
    rcFreePolyMesh(pmesh);

    *outData = navData;
    *outSize = navDataSize;
    return true;
}

void FreeBuffer(void* ptr)
{
    dtFree(ptr);
}

} // extern "C"
