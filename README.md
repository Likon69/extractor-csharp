# MaNGOS Extractor C# — WoW 3.3.5a NavMesh for CopilotBuddy

Navmesh extractor for World of Warcraft 3.3.5a (WotLK), originally
developed for CopilotBuddy. It produces `.mmtile` files in the HonorBuddy
format (PAMM, `mmapVer = 5`) compatible with `Navigation.dll`, with a
**4x4 split per ADT**, full interior support (houses, dungeons, caves) and
automatic obstacle avoidance (WMO, M2, GameObjects).

The tool is released as open source and can be driven both from the WPF
graphical interface and from the headless command line. The Recast
parameters are strictly identical to those of the HonorBuddy mesh
generator.

---

## Table of contents

1. [Features](#features)
2. [Quick build](#quick-build)
3. [Command line usage (CLI)](#command-line-usage-cli)
4. [Graphical interface usage (UI)](#graphical-interface-usage-ui)
5. [4x4 split and interiors](#4x4-split-and-interiors)
6. [Object avoidance](#object-avoidance)
7. [Native API — `RecastBuilderDll`](#native-api--recastbuilderdll)
8. [PAMM output format](#pamm-output-format)
9. [Recast parameters — identical to HonorBuddy](#recast-parameters--identical-to-honorbuddy)
10. [Project layout](#project-layout)

---

## Features

- Full extraction straight from the 3.3.5a client MPQ files (StormLib
  bundled).
- Generation of every MaNGOS artefact: `dbc/`, `maps/`, `vmaps/`,
  `mmaps/`, `roadmaps/`.
- **4x4 navmesh per ADT**: every ADT tile of 533 m is split into 16
  Detour sub-tiles of 133.333 m on each side.
- **Interior support**: houses, dungeons, caves and caverns through WMO
  (World Model Objects) integration with their decorative M2 models.
- **Automatic obstacle avoidance**: WMO/M2 geometry rasterized into the
  heightfield, GameObjects computed via `GameObjectDisplayInfo.dbc` and
  deduplicated.
- Offmesh connections (jumps, teleports, transports) loaded from a plain
  text file.
- Configurable parallelism, two modes: WPF GUI or headless CLI.
- Native C++ DLL `RecastBuilderDll.dll` exposing `BuildTile`,
  `TestPathfinding`, `TestPathfindingTwoFiles` for integration into any
  third-party bot or tool.
- Recast parameters strictly aligned with HonorBuddy's mesh generator
  (see [Recast parameters](#recast-parameters--identical-to-honorbuddy)).

---

## Quick build

Requirements: Visual Studio 2022 with the *.NET desktop development*
workload, MSBuild, and a C++ compiler (MSVC or MinGW) for the native
DLL.

```powershell
cd extractor-csharp
dotnet build MaNGOS.Extractor.sln -c Debug --nologo
```

The native DLL must be copied next to the managed executable after each
C++ build:

```powershell
Copy-Item "native\RecastBuilderDll\bin\Debug\RecastBuilderDll.dll" `
          "src\bin\Debug\net10.0-windows\" -Force
```

---

## Command line usage (CLI)

The CLI mode is meant for scripts, CI/CD automation, build farms or
headless servers.

### Syntax

```
MaNGOS.Extractor.CLI [options]
  --wow <path>        Path to the WoW 3.3.5a client (required)
  --out <path>        Output directory (required)
  --phases <csv>      Phases to run (default: Dbc,Map,VmapExtract,VmapAssemble,Road,Mmap)
  --maps <csv>        Map IDs (default: 0,1,530,571)
  --tile <x,y>        Limit extraction to a single ADT tile
  --threads <n>       Number of threads (default: 4)
  --locale <code>     Locale code (default: enUS)
  --gospawns <path>   gameobject_spawns.bin path
  --offmesh <path>    offmesh.txt path
  --no-spatial-filter Disable the per-sub-tile spatial pre-filter
  --help              Show help
```

### Available phases

| Phase             | Description                                              |
|-------------------|----------------------------------------------------------|
| `Dbc`             | Extract DBC/DB2 tables                                   |
| `Map`             | Generate `.map` files (heightmap, liquids)               |
| `VmapExtract`     | Raw WMO/M2 extraction to `Buildings/`                    |
| `VmapAssemble`    | BIH assembly to `vmaps/` (`.vmtree` + `.vmtile`)         |
| `Vmap`            | Combines `VmapExtract` + `VmapAssemble`                  |
| `Road`            | Generate `.road` files                                   |
| `Mmap`            | Generate the navmesh (4x4 per ADT)                       |

### Examples

Full extraction of every default map:

```powershell
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll" `
    --wow "C:\Games\World of Warcraft 3.3.5a" `
    --out "C:\output\extracted"
```

Single ADT tile test (useful to iterate on parameters):

```powershell
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll" `
    --wow "C:\Games\World of Warcraft 3.3.5a" `
    --out output_test --maps 0 --phases Mmap --tile 32,48 --threads 1
```

Outland only, without the spatial pre-filter:

```powershell
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll" `
    --wow "C:\Games\World of Warcraft 3.3.5a" `
    --out "C:\output\outland" `
    --maps 530 --phases Dbc,Map,VmapExtract,VmapAssemble,Road,Mmap `
    --no-spatial-filter --threads 8
```

---

## Graphical interface usage (UI)

The WPF interface provides real-time feedback, a clickable 64x64 grid to
pick a map and ADT zone, and JSON configuration file management
(`ExtractorConfig.json`).

Launch:

```powershell
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll"
```

The UI mirrors every CLI option (phases, maps, threads, offmesh,
GameObjects) and writes the config file next to the `.dll`.

---

## 4x4 split and interiors

### The 4x4 principle

Every ADT tile covers 533.333 meters (`GRID_SIZE = 533.333` per side,
terrain laid out as a 64-cell WoW grid). HonorBuddy stores the navmesh as
much smaller Detour sub-tiles for two reasons:

- finer streaming granularity at read time,
- better mesh resolution in dense areas.

The extractor reproduces HonorBuddy's exact strategy
(`MeshMapCalculator.Default`, `subTilesPerAdt = 4`):

```
ADT 533.333 m x 533.333 m
+-- 4 x 4 = 16 Detour sub-tiles
    +-- each sub-tile = 133.333 m x 133.333 m
        +-- borderSize = 5 cells (walkableRadius + 3)
```

The internal pipeline subdivides each sub-tile into **mini-tiles** of 80
cells per side (identical to `VERTEX_PER_TILE = 80` in
`MapBuilder.cpp`), then merges the resulting poly-meshes into a single
`dtNavMeshData`.

### Interiors: houses, dungeons, caves

Interior zones do **not** live on the ADT terrain itself. They come from
WMO files listed in the ADT's `MODF` chunks, themselves composed of:

- root WMO geometry — outer shell and main collision volumes,
- WMO doodads — small architectural add-ons,
- M2 models — doors, furniture, traps, etc.

The extractor loads the full WMO hierarchy, keeps only the groups flagged
`Flag_NoCollision = 0`, and injects the triangles into the Recast
heightfield. As a result, a bot can path **inside** houses, follow
corridors, descend into caves and exit through the right door.

Cave entrances (areas where the ADT itself is fully covered by a WMO)
are handled by exclusion: terrain triangles falling under a WMO are
**not** rasterized (`continue; // cave entrance / pit`), otherwise Recast
would see two solid layers and could not form the Detour portal between
the outside and the inside.

---

## Object avoidance

In addition to architectural WMOs, two more obstacle categories are
taken into account.

### GameObjects (DBC + user-provided binary)

In-game GameObjects (mailboxes, alchemy tables, campfires, portals,
doors, etc.) are injected into the navmesh via their real collision
model rather than a simple bounding cylinder.

The pipeline is:

1. Read `GameObjectDisplayInfo.dbc` — maps a `displayId` to a model
   path (M2 or WMO).
2. Read the user-provided binary `gameobject_spawns.bin` — gives, for
   every spawn, `mapId`, position, rotation quaternion, `displayId`.
3. For each sub-tile, spatial filtering of GameObjects by AABB: only
   those whose AABB overlaps the sub-tile + border bbox are processed.
4. Fetch the collision model (M2 → skin LOD0 bounding mesh, or WMO →
   collision groups), apply the quaternion + position transform, and
   rasterize into the heightfield.

A concurrent cache deduplicates already-loaded models: a single table
model used 200 times is parsed only once.

### Decorative M2 (WMO doodads)

M2 placed by `MODF` entries in WMOs are parsed directly and their
collision triangles injected. Bad winding and coordinate order have
been fixed compared to the initial C# extractor
(`fixCoords = (vz, vx, vy)` + flip).

### Roads

The road mask (the `.road` file produced by the `Road` phase) is folded
into the geometry; road areas keep `area = 1` but allow bots to follow
paths without breaking the mesh.

---

## Native API — `RecastBuilderDll`

The C++ DLL exposes a small, stable ABI, suitable for any third-party
integration (bot, pathfinding tool, viewer). Names and signatures follow
C conventions.

### `BuildTile`

Builds a single Detour sub-tile.

```c
bool BuildTile(
    const RecastBuildParams* params,
    const float*    verts, int vertCount,
    const int*      tris,  int triCount,
    const uint8_t*  areaIds,
    const float*    offMeshConVerts,
    const float*    offMeshConRads,
    const uint8_t*  offMeshConDirs,
    const uint8_t*  offMeshConAreas,
    const uint16_t* offMeshConFlags,
    int             offMeshConCount,
    uint8_t**       outData,
    int*            outSize);
```

Returns `false` if no polygon could be generated. `outData` is allocated
on the C++ side and must be released with `FreeBuffer`.

### `TestPathfinding`

Solves a path on a single `.mmtile`.

```c
int TestPathfinding(
    const uint8_t* mmtileData, int mmtileSize,
    float startX, float startY, float startZ,
    float endX,   float endY,   float endZ,
    float* outPath, int maxPathPts);
```

Returns the number of points written into `outPath`, or a negative error
code:

| Code | Meaning                           |
|------|-----------------------------------|
| -1   | Null parameter                    |
| -2   | `.mmtile` too small               |
| -3   | `dtAllocNavMesh` failed           |
| -4   | `dtNavMesh::init` failed          |
| -5   | `dtAllocNavMeshQuery` failed      |
| -6   | `dtNavMeshQuery::init` failed     |
| -7   | No poly near start or end         |
| -8   | `findPath` failed or empty        |

### `TestPathfindingTwoFiles`

Cross-ADT variant: loads two `.mmtile` files, fixes absolute coordinates
from each sub-tile's `bmin`, and resolves cross-tile links. Same error
codes as `TestPathfinding`.

### `FreeBuffer`

```c
void FreeBuffer(void* ptr);
```

Must be called for every buffer returned by `BuildTile`.

### `RecastBuildParams` struct

```c
struct RecastBuildParams {
    float BoundingBoxMinX, BoundingBoxMinY, BoundingBoxMinZ;
    float BoundingBoxMaxX, BoundingBoxMaxY, BoundingBoxMaxZ;

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
    int   BorderSize;
};
```

The default values used by the extractor are identical to HonorBuddy's
— see the table below.

---

## PAMM output format

```
File name : {mapId:D3}{adtY:D2}{adtX:D2}.mmtile
Example   : 0004832.mmtile  →  map = 0, adtY = 48, adtX = 32
```

Every ADT file contains a 20-byte PAMM header followed by 16 Detour
blobs (one per 4x4 sub-tile).

### Binary header

```
Offset  Size  Field
  0       4    magic        = 0x4D4D4150  ("MMAP")
  4       4    dtVersion    = 7
  8       4    mmapVersion  = 5
 12       4    subTileCount = 16
 16       4    usesLiquids  = 1
 20     ...    16 x [ uint32 dataSize | byte[] detourNavMeshData ]
```

### `dtMeshHeader` (start of each Detour blob)

```
+0    magic           0x4D534554
+4    version         7
+8    x               Detour tileX
+12   y               Detour tileY
+72   bmin[3]         world-space min corner (Recast space)
+84   bmax[3]         world-space max corner
+96   bvQuantFactor   ≈ 3.300003  (= 1/cs = 1/0.303030)
```

### Detour index calculation

```
detourTileX = (maxAdtX - adtX) * 4 + subX      (subX ∈ [0,3])
detourTileY = (maxAdtY - adtY) * 4 + subY      (subY ∈ [0,3])
```

Identical to HonorBuddy's `MeshMapCalculator.GetDetourTile`.

### Coordinate convention

```
WoW     : X = East,  Y = North,   Z = up
Recast  : X = -Y_wow, Y = Z_wow,  Z = -X_wow
         (copyVertices in MmapExtractorService: (-v.Y, v.Z, -v.X))
```

### Offmesh connections

Text format:

```
# mapId  tileX,tileY  (x y z)  (x y z)  radius  [areaType]  [direction]
0 32,48  (476.47 50.86 -331.81)  (472.84 47.51 -319.17)  0.40 1 1
```

- `areaType`: 1 by default (GROUND)
- `direction`: 1 = bidirectional, 0 = unidirectional
- vertices stored in Recast space: `(p.Y, p.Z, p.X)`
- `flags`: 0x2F (every non-transport query)

---

## Recast parameters — identical to HonorBuddy

The values below are the ones used by the extractor and match strictly
the HonorBuddy mesh generator.

| Parameter                | Value      | Notes                                          |
|--------------------------|------------|------------------------------------------------|
| `cs` (cellSize)          | 0.303030   | = GRID_SIZE / VERTEX_PER_MAP = 533.333/1760   |
| `ch` (cellHeight)        | 0.2        |                                                |
| `walkableSlopeAngle`     | 50.0 deg   |                                                |
| `walkableHeight`         | 11         | voxels = 2.2 m                                 |
| `walkableRadius`         | 2          | voxels = 0.606 m                               |
| `walkableClimb`          | 5          | voxels = 1.0 m                                 |
| `borderSize`             | 5          | walkableRadius + 3 (7 on Dalaran map 571)      |
| `minRegionArea`          | 400        | = rcSqr(20)                                    |
| `mergeRegionArea`        | 1600       | = rcSqr(40)                                    |
| `maxSimplificationError` | 1.3        |                                                |
| `MaxVertsPerPoly`        | 6          |                                                |
| `MINI_TILE_SIZE`         | 80         | identical to HB's VERTEX_PER_TILE             |

---

## Project layout

```
extractor-csharp/
+- src/
|  +- CLI/                          Command-line interface
|  +- Core/
|  |  +- Constants/                MagicBytes, WowConstants (GRID_SIZE, SubTilesPerAdtSide=4)
|  |  +- Interfaces/               IArchiveReader (MPQ abstraction)
|  |  +- Models/                   ExtractorConfig, RecastConfig, GoSpawn, TileProgressEvent
|  +- Formats/
|  |  +- Adt/                      ADT parser: MCVT/MCNK/MH2O
|  |  +- Dbc/                      DBC reader
|  |  +- M2/                       M2 parser: vertices + skin LOD0 + collision
|  |  +- Mpq/                      StormLib wrapper
|  |  +- Vmap/                     VMAPt07 writer
|  |  +- Wdt/                      WDT reader (list of ADTs in the map)
|  |  +- Wmo/                      WMO parser: root + collision groups
|  +- DbcExtractor/                 DBC/DB2 extraction service
|  +- MapExtractor/                 .map file generation
|  +- MmapExtractor/                4x4 navmesh core
|  |  +- MmapExtractorService.cs   Main pipeline
|  |  +- MangosVmapGeometryLoader.cs WMO/M2 loader from vmaps/
|  |  +- MangosVmtileLoader.cs     .vmtile loader
|  |  +- Recast/
|  |     +- RecastNativeBridge.cs  P/Invoke -> RecastBuilderDll.dll
|  +- RoadExtractor/                .road file generation
|  +- VmapExtractor/                vmaps/ generation
|  +- UI/
|     +- MainWindow.xaml           WPF layout
|     +- Config/ConfigFileManager  ExtractorConfig.json
|     +- ViewModels/MainViewModel  Commands, config, 64x64 grid
+- native/
   +- RecastBuilderDll/             Native C++ DLL (Recast + Detour)
      +- RecastBuilderDll.h
      +- RecastBuilderDll.cpp
```

---

## License

This project is released as open source. See the `LICENSE` file for the
full terms.