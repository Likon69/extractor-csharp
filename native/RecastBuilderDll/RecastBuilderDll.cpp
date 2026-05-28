#define RECASTBUILDERDLL_EXPORTS
#include "RecastBuilderDll.h"

#include "Recast.h"
#include "DetourNavMesh.h"
#include "DetourNavMeshBuilder.h"
#include "DetourNavMeshQuery.h"

#include <cstring>
#include <cmath>
#include <algorithm>
#include <vector>

extern "C"
{

// ---------------------------------------------------------------------------
// BuildTile — tiled Recast build matching MapBuilder.cpp VERTEX_PER_TILE=80.
//
// Each sub-tile bbox is subdivided into 80-cell mini-tiles, each processed
// through the full Recast pipeline independently, then all poly meshes are
// merged with rcMergePolyMeshes before creating the single Detour tile.
//
// This mirrors the old C++ MapBuilder pipeline exactly:
//   VERTEX_PER_TILE = 80, TILES_PER_MAP = 1760/80 = 22 per ADT
//   → per sub-tile (1/4 ADT): 440/80 ≈ 5-6 mini-tiles per side
//   → same minRegionArea, mergeRegionArea, maxSimplificationError
//   → RC_CONTOUR_TESS_WALL_EDGES, maxEdgeLen = MINI_TILE_SIZE+1
// ---------------------------------------------------------------------------

static const int MINI_TILE_SIZE     = 80;           // cells per mini-tile side (= MapBuilder.cpp VERTEX_PER_TILE)
static const int MINI_TILE_EDGE_LEN = MINI_TILE_SIZE + 1;  // maxEdgeLen = tileSize+1, effectively uncapped within tile

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

    const float cs           = params->CellSize;
    const float ch           = params->CellHeight;
    const int   borderSize   = params->BorderSize;
    const float borderMeters = borderSize * cs;

    float bmin[3] = { params->BoundingBoxMinX, params->BoundingBoxMinY, params->BoundingBoxMinZ };
    float bmax[3] = { params->BoundingBoxMaxX, params->BoundingBoxMaxY, params->BoundingBoxMaxZ };

    // -----------------------------------------------------------------------
    // Derive the CORE sub-tile bounds by stripping the global border that was
    // added in C#.  We then subdivide the core into MINI_TILE_SIZE-cell slabs,
    // re-adding the same border for each mini-tile so neighbour geometry is
    // rasterized — identical to MapBuilder.cpp tileCfg.bmin/bmax logic.
    // -----------------------------------------------------------------------
    float coreBmin[3] = { bmin[0] + borderMeters, bmin[1], bmin[2] + borderMeters };
    float coreBmax[3] = { bmax[0] - borderMeters, bmax[1], bmax[2] - borderMeters };

    float coreW = coreBmax[0] - coreBmin[0];
    float coreH = coreBmax[2] - coreBmin[2];

    int nTilesX = (int)ceilf(coreW / (MINI_TILE_SIZE * cs));
    int nTilesZ = (int)ceilf(coreH / (MINI_TILE_SIZE * cs));
    if (nTilesX < 1) nTilesX = 1;
    if (nTilesZ < 1) nTilesZ = 1;

    const float detailSampleDist     = cs * 16.f;
    const float detailSampleMaxError = ch * 1.f;
    const float maxSimplError = params->MaxSimplificationError > 0.f ? params->MaxSimplificationError : 8.f;
    const int   nvp           = params->MaxVertsPerPoly > 0 ? params->MaxVertsPerPoly : 6;

    // Per-mini-tile area copy buffer (so rcClearUnwalkableTriangles doesn't corrupt the original).
    uint8_t* areasBuf  = new uint8_t[triCount];
    // savedAreas holds the original C# area types (1=GROUND, 2=WATER, 3=LAVA) so we can restore them
    // after the slope check. rcClearUnwalkableTriangles only touches area==RC_WALKABLE_AREA (63), but
    // C# passes custom area types (1/2/3). Without the temp conversion, steep walls on M2/WMO objects
    // are never cleared to RC_NULL_AREA → navmesh passes through trees and rocks.
    uint8_t* savedAreas = new uint8_t[triCount];

    std::vector<rcPolyMesh*>       pmeshes;
    std::vector<rcPolyMeshDetail*> dmeshes;

    for (int tz = 0; tz < nTilesZ; ++tz)
    {
        for (int tx = 0; tx < nTilesX; ++tx)
        {
            // Mini-tile bounding box: core slab + border on every side,
            // CLAMPED to the sub-tile full bbox [bmin, bmax].
            // Clamping is critical: without it the last mini-tile in each axis
            // would extend past bmax, so rcBuildRegions(borderSize=5) would NOT
            // exclude the sub-tile border zone → polys leak into the border →
            // Detour cross-sub-tile links are never created.
            float tileBmin[3], tileBmax[3];
            tileBmin[0] = std::max(coreBmin[0] + tx * MINI_TILE_SIZE * cs - borderMeters, bmin[0]);
            tileBmin[1] = bmin[1];
            tileBmin[2] = std::max(coreBmin[2] + tz * MINI_TILE_SIZE * cs - borderMeters, bmin[2]);
            tileBmax[0] = std::min(coreBmin[0] + (tx + 1) * MINI_TILE_SIZE * cs + borderMeters, bmax[0]);
            tileBmax[1] = bmax[1];
            tileBmax[2] = std::min(coreBmin[2] + (tz + 1) * MINI_TILE_SIZE * cs + borderMeters, bmax[2]);

            int tileW, tileH;
            rcCalcGridSize(tileBmin, tileBmax, cs, &tileW, &tileH);

            rcHeightfield* hf = rcAllocHeightfield();
            if (!hf) continue;
            if (!rcCreateHeightfield(&ctx, *hf, tileW, tileH, tileBmin, tileBmax, cs, ch))
            {
                rcFreeHeightField(hf);
                continue;
            }

            // Copy area ids — each mini-tile needs an independent copy.
            if (areaIds)
                memcpy(areasBuf, areaIds, triCount);
            else
                memset(areasBuf, RC_WALKABLE_AREA, triCount);

            // rcClearUnwalkableTriangles only processes area==RC_WALKABLE_AREA (63).
            // The C# code passes custom area types (1=GROUND, 2=WATER, 3=LAVA) directly.
            // Without the temp conversion, steep walls on M2/WMO objects are never cleared
            // to RC_NULL_AREA, so the navmesh passes right through trees and buildings.
            // Fix: temporarily set all non-null areas to RC_WALKABLE_AREA so the slope
            // check runs on every triangle, then restore the original area type.
            memcpy(savedAreas, areasBuf, triCount);
            for (int i = 0; i < triCount; i++)
                if (areasBuf[i] != RC_NULL_AREA)
                    areasBuf[i] = RC_WALKABLE_AREA;

            rcClearUnwalkableTriangles(&ctx, params->WalkableSlopeAngle,
                verts, vertCount, tris, triCount, areasBuf);

            // Restore original area types for triangles that passed the slope check.
            for (int i = 0; i < triCount; i++)
                if (areasBuf[i] != RC_NULL_AREA)
                    areasBuf[i] = savedAreas[i];

            rcRasterizeTriangles(&ctx, verts, vertCount, tris, areasBuf, triCount,
                *hf, params->WalkableClimb);

            rcFilterLowHangingWalkableObstacles(&ctx, params->WalkableClimb, *hf);
            rcFilterWalkableLowHeightSpans(&ctx, params->WalkableHeight, *hf);
            rcFilterLedgeSpans(&ctx, params->WalkableHeight, params->WalkableClimb, *hf);

            rcCompactHeightfield* chf = rcAllocCompactHeightfield();
            if (!chf) { rcFreeHeightField(hf); continue; }
            if (!rcBuildCompactHeightfield(&ctx, params->WalkableHeight, params->WalkableClimb, *hf, *chf))
            {
                rcFreeCompactHeightfield(chf);
                rcFreeHeightField(hf);
                continue;
            }
            rcFreeHeightField(hf);

            if (!rcErodeWalkableArea(&ctx, params->WalkableRadius, *chf))
            {
                rcFreeCompactHeightfield(chf);
                continue;
            }

            if (!rcBuildDistanceField(&ctx, *chf))
            {
                rcFreeCompactHeightfield(chf);
                continue;
            }

            if (!rcBuildRegions(&ctx, *chf, borderSize,
                (int)params->MinRegionArea, (int)params->MergeRegionArea))
            {
                rcFreeCompactHeightfield(chf);
                continue;
            }

            rcContourSet* cset = rcAllocContourSet();
            if (!cset) { rcFreeCompactHeightfield(chf); continue; }

            if (!rcBuildContours(&ctx, *chf, maxSimplError, MINI_TILE_EDGE_LEN,
                *cset, RC_CONTOUR_TESS_WALL_EDGES))
            {
                rcFreeContourSet(cset);
                rcFreeCompactHeightfield(chf);
                continue;
            }

            rcPolyMesh* pmesh = rcAllocPolyMesh();
            if (!pmesh || !rcBuildPolyMesh(&ctx, *cset, nvp, *pmesh))
            {
                if (pmesh) rcFreePolyMesh(pmesh);
                rcFreeContourSet(cset);
                rcFreeCompactHeightfield(chf);
                continue;
            }
            rcFreeContourSet(cset);

            rcPolyMeshDetail* dmesh = rcAllocPolyMeshDetail();
            if (!dmesh || !rcBuildPolyMeshDetail(&ctx, *pmesh, *chf,
                detailSampleDist, detailSampleMaxError, *dmesh))
            {
                if (dmesh) { rcFreePolyMeshDetail(dmesh); dmesh = nullptr; }
            }
            rcFreeCompactHeightfield(chf);

            if (pmesh->npolys > 0)
            {
                pmeshes.push_back(pmesh);
                dmeshes.push_back(dmesh);
            }
            else
            {
                rcFreePolyMesh(pmesh);
                if (dmesh) rcFreePolyMeshDetail(dmesh);
            }
        }
    }

    delete[] areasBuf;
    delete[] savedAreas;

    if (pmeshes.empty())
        return false;

    // -----------------------------------------------------------------------
    // Merge all mini-tile poly meshes — same as MapBuilder.cpp rcMergePolyMeshes.
    // -----------------------------------------------------------------------
    rcPolyMesh* pmesh = rcAllocPolyMesh();
    if (!pmesh)
    {
        for (auto p : pmeshes) rcFreePolyMesh(p);
        for (auto d : dmeshes) { if (d) rcFreePolyMeshDetail(d); }
        return false;
    }
    if (!rcMergePolyMeshes(&ctx, pmeshes.data(), (int)pmeshes.size(), *pmesh))
    {
        rcFreePolyMesh(pmesh);
        for (auto p : pmeshes) rcFreePolyMesh(p);
        for (auto d : dmeshes) { if (d) rcFreePolyMeshDetail(d); }
        return false;
    }
    for (auto p : pmeshes) rcFreePolyMesh(p);

    rcPolyMeshDetail* dmesh = rcAllocPolyMeshDetail();
    if (dmesh)
    {
        std::vector<rcPolyMeshDetail*> validDmeshes;
        for (auto d : dmeshes) { if (d) validDmeshes.push_back(d); }
        if (validDmeshes.empty() ||
            !rcMergePolyMeshDetails(&ctx, validDmeshes.data(), (int)validDmeshes.size(), *dmesh))
        {
            rcFreePolyMeshDetail(dmesh);
            dmesh = nullptr;
        }
    }
    for (auto d : dmeshes) { if (d) rcFreePolyMeshDetail(d); }

    // -----------------------------------------------------------------------
    // Assign HB-compatible area types and flags — identical to MapBuilder.cpp.
    // -----------------------------------------------------------------------
    for (int i = 0; i < pmesh->npolys; i++)
    {
        if (pmesh->areas[i] == RC_WALKABLE_AREA)
            pmesh->areas[i] = 1;
        // area 2 = NAV_WATER → flags 0x04 (swim), else flags 0x01 (run)
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
    if (dmesh)
    {
        navParams.detailMeshes     = dmesh->meshes;
        navParams.detailVerts      = dmesh->verts;
        navParams.detailVertsCount = dmesh->nverts;
        navParams.detailTris       = dmesh->tris;
        navParams.detailTriCount   = dmesh->ntris;
    }
    // Use the full sub-tile bbox from params (includes border) — matches rcVcopy(params.bmin,bmin)
    // in MapBuilder.cpp rather than reading back from the merged poly mesh.
    memcpy(navParams.bmin, bmin, sizeof(float) * 3);
    memcpy(navParams.bmax, bmax, sizeof(float) * 3);
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
        if (dmesh) rcFreePolyMeshDetail(dmesh);
        rcFreePolyMesh(pmesh);
        return false;
    }

    if (dmesh) rcFreePolyMeshDetail(dmesh);
    rcFreePolyMesh(pmesh);

    *outData = navData;
    *outSize = navDataSize;
    return true;
}

void FreeBuffer(void* ptr)
{
    dtFree(ptr);
}

int TestPathfinding(
    const uint8_t* mmtileData, int mmtileSize,
    float startX, float startY, float startZ,
    float endX,   float endY,   float endZ,
    float* outPath, int maxPathPts)
{
    if (!mmtileData || mmtileSize < 24) return -1;

    // Parse custom file header: magic(4) dtVer(4) mmapVer(4) nTiles(4) usesLiq(4) = 20 bytes
    uint32_t nTiles = *reinterpret_cast<const uint32_t*>(mmtileData + 12);
    if (nTiles == 0 || nTiles > 256) return -2;

    // Read first non-empty sub-tile header to derive navmesh origin
    const float TILE_WIDTH = 133.333333f;
    float orig0 = 0.f, orig2 = 0.f;
    bool foundOrigin = false;
    {
        const uint8_t* p = mmtileData + 20;
        for (uint32_t i = 0; i < nTiles && !foundOrigin; i++)
        {
            if (p + 4 > mmtileData + mmtileSize) break;
            uint32_t sz = *reinterpret_cast<const uint32_t*>(p);
            p += 4;
            if (sz >= 100)
            {
                const dtMeshHeader* hdr = reinterpret_cast<const dtMeshHeader*>(p);
                orig0 = hdr->bmin[0] - hdr->x * TILE_WIDTH;
                orig2 = hdr->bmin[2] - hdr->y * TILE_WIDTH;
                foundOrigin = true;
            }
            p += sz;
        }
    }
    if (!foundOrigin) return -2;

    // Create dtNavMesh
    dtNavMeshParams params;
    memset(&params, 0, sizeof(params));
    params.orig[0]    = orig0;
    params.orig[1]    = 0.f;
    params.orig[2]    = orig2;
    params.tileWidth  = TILE_WIDTH;
    params.tileHeight = TILE_WIDTH;
    params.maxTiles   = (int)nTiles;
    params.maxPolys   = 1 << 20;

    dtNavMesh* mesh = dtAllocNavMesh();
    if (!mesh) return -3;

    if (dtStatusFailed(mesh->init(&params)))
    {
        dtFreeNavMesh(mesh);
        return -4;
    }

    // Add all sub-tiles (copy each blob so Detour can own + free it)
    const uint8_t* p = mmtileData + 20;
    for (uint32_t i = 0; i < nTiles; i++)
    {
        if (p + 4 > mmtileData + mmtileSize) break;
        uint32_t sz = *reinterpret_cast<const uint32_t*>(p);
        p += 4;
        if (sz == 0) { continue; }
        if (p + sz > mmtileData + mmtileSize) break;

        uint8_t* copy = (uint8_t*)dtAlloc(sz, DT_ALLOC_PERM);
        if (!copy) { p += sz; continue; }
        memcpy(copy, p, sz);

        dtTileRef tileRef = 0;
        mesh->addTile(copy, (int)sz, DT_TILE_FREE_DATA, 0, &tileRef);
        p += sz;
    }

    // Create query
    dtNavMeshQuery* query = dtAllocNavMeshQuery();
    if (!query) { dtFreeNavMesh(mesh); return -5; }

    if (dtStatusFailed(query->init(mesh, 4096)))
    {
        dtFreeNavMeshQuery(query);
        dtFreeNavMesh(mesh);
        return -6;
    }

    // Find path between start and end
    dtQueryFilter filter;
    filter.setIncludeFlags(0xFFFF);
    filter.setExcludeFlags(0);

    float ext[3] = { 30.f, 400.f, 30.f };
    float sp[3]  = { startX, startY, startZ };
    float ep[3]  = { endX,   endY,   endZ   };

    dtPolyRef startRef = 0, endRef = 0;
    float nearStart[3] = {}, nearEnd[3] = {};

    query->findNearestPoly(sp, ext, &filter, &startRef, nearStart);
    query->findNearestPoly(ep, ext, &filter, &endRef,   nearEnd);

    if (!startRef || !endRef)
    {
        dtFreeNavMeshQuery(query);
        dtFreeNavMesh(mesh);
        return -7;
    }

    const int MAX_POLYS = 512;
    dtPolyRef pathPolys[MAX_POLYS];
    int pathPolyCount = 0;

    dtStatus status = query->findPath(startRef, endRef, nearStart, nearEnd,
                                      &filter, pathPolys, &pathPolyCount, MAX_POLYS);
    if (dtStatusFailed(status) || pathPolyCount == 0)
    {
        dtFreeNavMeshQuery(query);
        dtFreeNavMesh(mesh);
        return -8;
    }

    // Smooth to straight path
    int nStraight = 0;
    if (outPath && maxPathPts > 0)
    {
        const int cap = maxPathPts < MAX_POLYS ? maxPathPts : MAX_POLYS;
        std::vector<float> pts(cap * 3);
        query->findStraightPath(nearStart, nearEnd, pathPolys, pathPolyCount,
                                pts.data(), nullptr, nullptr, &nStraight, cap);
        memcpy(outPath, pts.data(), nStraight * 3 * sizeof(float));
    }
    else
    {
        nStraight = pathPolyCount;
    }

    dtFreeNavMeshQuery(query);
    dtFreeNavMesh(mesh);
    return nStraight;
}

} // extern "C"
