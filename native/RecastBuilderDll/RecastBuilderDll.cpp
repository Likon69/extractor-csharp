#define RECASTBUILDERDLL_EXPORTS
#include "RecastBuilderDll.h"

#include "Recast/Include/Recast.h"
#include "Recast/Include/RecastAlloc.h"
#include "Recast/Include/RecastAssert.h"
#include "Detour/Include/DetourNavMesh.h"
#include "Detour/Include/DetourNavMeshBuilder.h"

#include <cstring>

extern "C"
{

bool BuildTile(
    RecastBuildParams* params,
    float* verts, int vertCount,
    int* tris, int triCount,
    unsigned char* areaIds,
    unsigned char** outData, int* outSize)
{
    if (!params || !verts || !tris || !outData || !outSize)
        return false;

    *outData = nullptr;
    *outSize = 0;

    // Allocate local context per call (no static state)
    rcContext* ctx = new rcContext();

    float bmin[3] = { params->BoundingBoxMinX, params->BoundingBoxMinY, params->BoundingBoxMinZ };
    float bmax[3] = { params->BoundingBoxMaxX, params->BoundingBoxMaxY, params->BoundingBoxMaxZ };

    rcHeightfield* hf = rcAllocHeightfield();
    if (!hf)
    {
        delete ctx;
        return false;
    }

    int gridWidth, gridHeight;
    rcCalcGridSize(bmin, bmax, params->CellSize, &gridWidth, &gridHeight);

    if (!rcCreateHeightfield(ctx, *hf, gridWidth, gridHeight, bmin, bmax, params->CellSize, params->CellHeight))
    {
        rcFreeHeightField(hf);
        delete ctx;
        return false;
    }

    unsigned char* areas = areaIds;
    bool allocAreas = false;
    if (!areas)
    {
        areas = new unsigned char[triCount];
        memset(areas, RC_WALKABLE_AREA, triCount);
        allocAreas = true;
    }

    if (!rcRasterizeTriangles(ctx, verts, vertCount, tris, areas, triCount, *hf, 1))
    {
        if (allocAreas) delete[] areas;
        rcFreeHeightField(hf);
        delete ctx;
        return false;
    }

    if (allocAreas) delete[] areas;

    rcFilterLowHangingWalkableObstacles(ctx, params->WalkableClimb, *hf);
    rcFilterWalkableLowHeightSpans(ctx, params->WalkableHeight, *hf);
    rcFilterLedgeSpans(ctx, params->WalkableHeight, params->WalkableClimb, *hf);

    rcCompactHeightfield* chf = rcAllocCompactHeightfield();
    if (!chf)
    {
        rcFreeHeightField(hf);
        delete ctx;
        return false;
    }

    if (!rcBuildCompactHeightfield(ctx, params->WalkableHeight, params->WalkableClimb, *hf, *chf))
    {
        rcFreeCompactHeightfield(chf);
        rcFreeHeightField(hf);
        delete ctx;
        return false;
    }

    if (!rcErodeWalkableArea(ctx, params->WalkableRadius, *chf))
    {
        rcFreeCompactHeightfield(chf);
        rcFreeHeightField(hf);
        delete ctx;
        return false;
    }

    // Heightfield is no longer needed after compact heightfield is built.
    rcFreeHeightField(hf);
    hf = nullptr;  // Prevent double-free on error paths below.

    if (!rcBuildDistanceField(ctx, *chf))
    {
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    if (!rcBuildRegions(ctx, *chf, 0, (int)params->MinRegionArea, (int)params->MergeRegionArea))
    {
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    rcContourSet* cset = rcAllocContourSet();
    if (!cset)
    {
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    float maxEdgeLen = 41; // spec: VERTEX_PER_TILE + 1
    float maxError = params->MaxSimplificationError > 0 ? params->MaxSimplificationError : 8.0f;
    int buildFlags = RC_CONTOUR_TESS_WALL_EDGES;

    if (!rcBuildContours(ctx, *chf, maxError, (int)maxEdgeLen, *cset, buildFlags))
    {
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    int nvp = params->MaxVertsPerPoly > 0 ? params->MaxVertsPerPoly : 6;

    rcPolyMesh* pmesh = rcAllocPolyMesh();
    if (!pmesh)
    {
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    if (!rcBuildPolyMesh(ctx, *cset, nvp, *pmesh))
    {
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    rcPolyMeshDetail* dmesh = rcAllocPolyMeshDetail();
    if (!dmesh)
    {
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    float detailDist = params->CellSize * 16.0f;  // spec: 4.848f = 0.303030 x 16
    float detailMaxError = params->CellHeight * 1.0f;   // spec: 0.2f = 0.2 x 1
    if (!rcBuildPolyMeshDetail(ctx, *pmesh, *chf, detailDist, detailMaxError, *dmesh))
    {
        rcFreePolyMeshDetail(dmesh);
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        delete ctx;
        return false;
    }

    rcFreeContourSet(cset);
    cset = nullptr;
    rcFreeCompactHeightfield(chf);
    chf = nullptr;

    for (int i = 0; i < pmesh->npolys; i++)
    {
        if (pmesh->areas[i] == RC_WALKABLE_AREA)
            pmesh->areas[i] = 1;
        
        pmesh->flags[i] = (pmesh->areas[i] == 2) ? 0x04 : 0x01; // NAV_WATER(2)=0x04 swim, else NAV_GROUND=0x01
    }

    dtNavMeshCreateParams navParams;
    memset(&navParams, 0, sizeof(navParams));
    navParams.tileX = params->TileX;
    navParams.tileY = params->TileY;

    navParams.verts = pmesh->verts;
    navParams.vertCount = pmesh->nverts;
    navParams.polys = pmesh->polys;
    navParams.polyAreas = pmesh->areas;
    navParams.polyFlags = pmesh->flags;
    navParams.polyCount = pmesh->npolys;
    navParams.nvp = pmesh->nvp;

    navParams.detailMeshes = dmesh->meshes;
    navParams.detailVerts = dmesh->verts;
    navParams.detailVertsCount = dmesh->nverts;
    navParams.detailTris = dmesh->tris;
    navParams.detailTriCount = dmesh->ntris;

    memcpy(navParams.bmin, pmesh->bmin, sizeof(float) * 3);
    memcpy(navParams.bmax, pmesh->bmax, sizeof(float) * 3);

    // Cell size/height — required by dtCreateNavMeshData for BV-tree quantization
    // and vertex dequantization.  Previously left at 0 (undefined behavior).
    navParams.cs = pmesh->cs;
    navParams.ch = pmesh->ch;

    // World units (not cell-based)
    navParams.walkableHeight = params->WalkableHeight * params->CellHeight;
    navParams.walkableRadius = 0.400f;  // spec: hardcoded exact HB value, NOT walkableRadius * cellSize
    navParams.walkableClimb = params->WalkableClimb * params->CellHeight;
    navParams.buildBvTree = true;

    unsigned char* navData = nullptr;
    int navDataSize = 0;

    if (!dtCreateNavMeshData(&navParams, &navData, &navDataSize))
    {
        rcFreePolyMeshDetail(dmesh);
        rcFreePolyMesh(pmesh);
        delete ctx;
        return false;
    }

    rcFreePolyMeshDetail(dmesh);
    rcFreePolyMesh(pmesh);
    delete ctx;

    *outData = navData;
    *outSize = navDataSize;

    return true;
}

void FreeBuffer(void* buffer)
{
    if (buffer)
        delete[] static_cast<unsigned char*>(buffer);
}

}
