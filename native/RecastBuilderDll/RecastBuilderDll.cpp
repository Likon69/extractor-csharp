#define RECASTBUILDERDLL_EXPORTS
#include "RecastBuilderDll.h"

#include "Recast/Include/Recast.h"
#include "Recast/Include/RecastAlloc.h"
#include "Recast/Include/RecastAssert.h"
#include "Detour/Include/DetourNavMesh.h"
#include "Detour/Include/DetourNavMeshBuilder.h"

#include <cstring>

static rcContext* s_ctx = nullptr;

static rcContext* getContext()
{
    if (!s_ctx)
        s_ctx = rcAllocContext();
    return s_ctx;
}

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

    rcContext* ctx = getContext();

    float bmin[3] = { params->BoundingBoxMinX, params->BoundingBoxMinY, params->BoundingBoxMinZ };
    float bmax[3] = { params->BoundingBoxMaxX, params->BoundingBoxMaxY, params->BoundingBoxMaxZ };

    rcHeightfield* hf = rcAllocHeightfield();
    if (!hf)
        return false;

    int gridWidth, gridHeight;
    rcCalcGridSize(bmin, bmax, params->CellSize, &gridWidth, &gridHeight);

    if (!rcCreateHeightfield(ctx, *hf, gridWidth, gridHeight, bmin, bmax, params->CellSize, params->CellHeight))
    {
        rcFreeHeightfield(hf);
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
        rcFreeHeightfield(hf);
        return false;
    }

    if (allocAreas) delete[] areas;

    rcFilterLowHangingWalkableObstacles(ctx, params->WalkableClimb, *hf);
    rcFilterWalkableLowHeightSpans(ctx, params->WalkableHeight, *hf);
    rcFilterLedgeSpans(ctx, params->WalkableHeight, params->WalkableClimb, *hf);

    rcCompactHeightfield* chf = rcAllocCompactHeightfield();
    if (!chf)
    {
        rcFreeHeightfield(hf);
        return false;
    }

    if (!rcBuildCompactHeightfield(ctx, params->WalkableHeight, params->WalkableClimb, *hf, *chf))
    {
        rcFreeCompactHeightfield(chf);
        rcFreeHeightfield(hf);
        return false;
    }

    rcFreeHeightfield(hf);

    if (!rcErodeWalkableArea(ctx, params->WalkableRadius, *chf))
    {
        rcFreeCompactHeightfield(chf);
        return false;
    }

    if (!rcBuildDistanceField(ctx, *chf))
    {
        rcFreeCompactHeightfield(chf);
        return false;
    }

    if (!rcBuildRegions(ctx, *chf, 0, (int)params->MinRegionArea, (int)params->MergeRegionArea))
    {
        rcFreeCompactHeightfield(chf);
        return false;
    }

    rcContourSet* cset = rcAllocContourSet();
    if (!cset)
    {
        rcFreeCompactHeightfield(chf);
        return false;
    }

    float maxEdgeLen = 0;
    float maxError = params->MaxSimplificationError > 0 ? params->MaxSimplificationError : 8.0f;
    int buildFlags = RC_CONTOUR_TESS_WALL_EDGES;

    if (!rcBuildContours(ctx, *chf, maxError, (int)maxEdgeLen, *cset, buildFlags))
    {
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    int nvp = params->MaxVertsPerPoly > 0 ? params->MaxVertsPerPoly : 6;

    rcPolyMesh* pmesh = rcAllocPolyMesh();
    if (!pmesh)
    {
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    if (!rcBuildPolyMesh(ctx, *cset, nvp, *pmesh))
    {
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    rcPolyMeshDetail* dmesh = rcAllocPolyMeshDetail();
    if (!dmesh)
    {
        rcFreePolyMesh(pmesh);
        rcFreeContourSet(cset);
        rcFreeCompactHeightfield(chf);
        return false;
    }

    if (!rcBuildPolyMeshDetail(ctx, *pmesh, *chf, 4.0f, 4.0f, *dmesh))
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
            pmesh->areas[i] = 0;
        pmesh->flags[i] = 1;
    }

    dtNavMeshCreateParams navParams;
    memset(&navParams, 0, sizeof(navParams));

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

    navParams.cs = params->CellSize;
    navParams.ch = params->CellHeight;
    navParams.walkableHeight = params->WalkableHeight * params->CellHeight;
    navParams.walkableRadius = params->WalkableRadius * params->CellSize;
    navParams.walkableClimb = params->WalkableClimb * params->CellHeight;
    navParams.buildBvTree = true;

    unsigned char* navData = nullptr;
    int navDataSize = 0;

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

void FreeBuffer(void* buffer)
{
    if (buffer)
        delete[] static_cast<unsigned char*>(buffer);
}

}