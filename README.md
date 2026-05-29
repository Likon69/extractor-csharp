# MaNGOS Extractor C# — WoW 3.3.5a NavMesh

> Portage C++ → C# .NET 10 WPF du générateur MaNGOS navmesh.  
> Produit des `.mmtile` **au format HonorBuddy (PAMM, mmapVer=5)** compatibles avec `Navigation.dll`.

---

## Commandes rapides

```powershell
# 1. Build complet
cd extractor-csharp
dotnet build MaNGOS.Extractor.sln -c Debug --nologo

# 2. Déployer la DLL native après chaque build C++ (obligatoire)
Copy-Item "native\RecastBuilderDll\bin\Debug\RecastBuilderDll.dll" `
          "src\bin\Debug\net10.0-windows\" -Force

# 3. Lancer l'UI
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll"

# 4. Test tile unique (CLI headless)
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll" `
    --wow "C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a original" `
    --out output_test --maps 0 --phases Mmap --tile 32,48 --threads 1

# 5. Inspecter un .mmtile (PowerShell)
$b = [System.IO.File]::ReadAllBytes("output_test\mmaps\0004832.mmtile")
[System.BitConverter]::ToUInt32($b, 0)   # magic = 0x4D4D4150 "MMAP"
[System.BitConverter]::ToUInt32($b, 4)   # dtVersion  = 7
[System.BitConverter]::ToUInt32($b, 8)   # mmapVersion = 5
[System.BitConverter]::ToUInt32($b, 12)  # subTileCount = 16
[System.BitConverter]::ToUInt32($b, 16)  # usesLiquids  = 1
```

---

## Structure du projet

```
extractor-csharp/
├── src/
│   ├── CLI/                          — Interface ligne de commande (--wow, --out, --maps, --phases, --tile, --threads)
│   ├── Core/
│   │   ├── Constants/                — MagicBytes ("MMAP"/"MAP "), WowConstants (GRID_SIZE=533.333)
│   │   ├── Interfaces/               — IArchiveReader (abstraction MPQ)
│   │   └── Models/                   — ExtractorConfig, RecastConfig, GoSpawn, TileProgressEvent
│   ├── Formats/
│   │   ├── Adt/                      — Parser ADT : MCVT(heights) + MCNK(flags/area) + MH2O(liquid)
│   │   ├── Dbc/                      — Lecteur DBC : Map.dbc, AreaTable.dbc, GameObjectDisplayInfo.dbc
│   │   ├── M2/                       — Parser M2 : header + vertices + skin LOD0 + triangles collision
│   │   ├── Mpq/                      — Wrapper StormLib : MpqArchive, MpqArchiveCollection
│   │   ├── Vmap/                     — Écriture VMAPt07
│   │   ├── Wdt/                      — Lecteur WDT : liste ADT présents dans la map
│   │   └── Wmo/                      — Parser WMO : root + groupes collision, filtrage NoCollision
│   ├── MapExtractor/                 — Fichiers .map (heightmap, liquides, area flags)
│   ├── MmapExtractor/                — Cœur navmesh
│   │   ├── MmapExtractorService.cs   — Pipeline principal
│   │   └── Recast/
│   │       └── RecastNativeBridge.cs — P/Invoke → RecastBuilderDll.dll
│   ├── RoadExtractor/                — Fichiers .road
│   ├── VmapExtractor/                — Fichiers vmaps/
│   └── UI/
│       ├── MainWindow.xaml           — WPF layout
│       ├── Config/ConfigFileManager  — JSON ExtractorConfig.json (next to .dll)
│       └── ViewModels/MainViewModel  — Commandes, config, grille 64×64
└── native/
    └── RecastBuilderDll/             — DLL C++ native (Recast + Detour)
        ├── RecastBuilderDll.h
        └── RecastBuilderDll.cpp
```

---

## Format de sortie — PAMM mmtile (HonorBuddy)

```
Nom fichier : {mapId:D3}{adtY:D2}{adtX:D2}.mmtile
Exemple     : 0004832.mmtile  → map=0, adtY=48, adtX=32
```

Chaque ADT (533 m × 533 m) produit **un fichier unique contenant 16 sous-tiles Detour (4×4)**.  
Chaque sous-tile fait `533.333 / 4 = 133.333 m` — correspond à `MeshMapCalculator.Default` (HB, `subTilesPerAdt=4`).

### Structure binaire

```
Offset  Taille  Valeur
  0       4     magic       = 0x4D4D4150  ("MMAP")
  4       4     dtVersion   = 7
  8       4     mmapVersion = 5
 12       4     subTileCount = 16
 16       4     usesLiquids = 1
 20     ...     16 × [ uint32 dataSize | byte[] detourNavMeshData ]
```

### `dtMeshHeader` (offset 0 dans chaque blob Detour)

```
+0   magic          0x4D534554
+4   version        7
+8   x              tileX Detour
+12  y              tileY Detour
+72  bmin[3]        coin min monde (espace Recast)
+84  bmax[3]        coin max monde
+96  bvQuantFactor  ≈ 3.300003  (= 1/cs = 1/0.303030)
```

---

## DLL native — `RecastBuilderDll.dll`

### Exports C

#### `BuildTile` — construit un sous-tile Detour

```c
bool BuildTile(
    const RecastBuildParams* params,  // voir struct ci-dessous
    const float*    verts,            // sommets géométrie (espace Recast, triplets XYZ)
    int             vertCount,
    const int*      tris,             // indices triangles (triplets)
    int             triCount,
    const uint8_t*  areaIds,          // area par triangle : 1=GROUND, 2=WATER, 3=LAVA, 0=NULL
    const float*    offMeshConVerts,  // 6 floats par connexion (start XYZ + end XYZ)
    const float*    offMeshConRads,   // rayon par connexion
    const uint8_t*  offMeshConDirs,   // 0=unidirectionnel, 1=bidirectionnel
    const uint8_t*  offMeshConAreas,  // area de la connexion
    const uint16_t* offMeshConFlags,  // flags (0x2F = traversable)
    int             offMeshConCount,
    uint8_t**       outData,          // [out] données Detour allouées en C++ — libérer avec FreeBuffer
    int*            outSize           // [out] taille des données
);
// Retourne false si aucun polygone généré.
```

#### `FreeBuffer` — libère la mémoire allouée par BuildTile

```c
void FreeBuffer(void* ptr);
```

#### `TestPathfinding` — test pathfinding sur un seul mmtile

```c
int TestPathfinding(
    const uint8_t* mmtileData, int mmtileSize,
    float startX, float startY, float startZ,  // espace Recast (X=droite, Y=haut, Z=profondeur)
    float endX,   float endY,   float endZ,
    float* outPath,                            // [out] waypoints XYZ (maxPathPts triplets)
    int    maxPathPts
);
// Retourne : nombre de points écrits dans outPath, ou code erreur négatif :
//  -1  paramètre nul
//  -2  mmtile trop petit
//  -3  dtAllocNavMesh échoué
//  -4  dtNavMesh::init échoué
//  -5  dtAllocNavMeshQuery échoué
//  -6  dtNavMeshQuery::init échoué
//  -7  aucun poly trouvé près du start ou end
//  -8  findPath échoué ou chemin vide
```

#### `TestPathfindingTwoFiles` — test pathfinding cross-ADT (2 mmtiles)

```c
int TestPathfindingTwoFiles(
    const uint8_t* tile1Data, int tile1Size,
    const uint8_t* tile2Data, int tile2Size,
    float startX, float startY, float startZ,
    float endX,   float endY,   float endZ,
    float* outPath, int maxPathPts
);
// Charge les deux mmtiles dans un seul dtNavMesh, corrige les coordonnées absolutes
// depuis bmin de chaque sous-tile, puis résout les liens cross-ADT.
// Mêmes codes d'erreur que TestPathfinding.
```

### Struct `RecastBuildParams`

```c
struct RecastBuildParams {
    float BoundingBoxMinX, BoundingBoxMinY, BoundingBoxMinZ;  // bbox étendue (inclut border)
    float BoundingBoxMaxX, BoundingBoxMaxY, BoundingBoxMaxZ;

    float CellSize;             // 0.303030  (= GRID_SIZE / VERTEX_PER_MAP = 533.333/1760)
    float CellHeight;           // 0.2
    float WalkableSlopeAngle;   // 50.0°
    int   WalkableHeight;       // 11  (voxels = 2.2 m)
    int   WalkableRadius;       // 2   (voxels = 0.606 m)
    int   WalkableClimb;        // 5   (voxels = 1.0 m)
    int   TileX;                // index Detour X du sous-tile
    int   TileY;                // index Detour Y du sous-tile

    float MinRegionArea;        // 400  (20² cellules)
    float MergeRegionArea;      // 1600 (40² cellules)
    float MaxSimplificationError; // 1.3
    int   MaxVertsPerPoly;      // 6
    int   BorderSize;           // WalkableRadius + 3 = 5  (cellules)
                                // exception map 571 Dalaran : WalkableRadius + 5 = 7
};
```

### Pipeline interne BuildTile (mini-tiles)

```
BuildTile reçoit un sous-tile 133.333 m × 133.333 m
  → soustrait la border (5 × cs = 1.515 m) pour obtenir le "core"
  → subdivise le core en MINI_TILE_SIZE=80 cellules par côté
    = même VERTEX_PER_TILE=80 que MapBuilder.cpp
  → pour chaque mini-tile :
      rcCreateHeightfield
      rcClearUnwalkableTriangles (avec conversion area→RC_WALKABLE_AREA + restore)
      rcRasterizeTriangles
      rcFilter{LowHanging,LowHeight,Ledge}
      rcBuildCompactHeightfield
      rcErodeWalkableArea (radius=2)
      rcBuildDistanceField + rcBuildRegions (borderSize=5)
      rcBuildContours (RC_CONTOUR_TESS_WALL_EDGES, maxEdgeLen=81)
      rcBuildPolyMesh + rcBuildPolyMeshDetail
  → rcMergePolyMeshes (tous les mini-tiles)
  → assign area/flags HB : area=1 (GROUND), flags=0x0001
  → dtNavMeshCreateParams → dtCreateNavMeshData
  → outData / outSize
```

---

## Coordonnées — Convention WoW ↔ Recast ↔ Detour

```
WoW      :  X = Est,  Y = Nord, Z = haut
Recast   :  X = -Y_wow, Y = Z_wow, Z = -X_wow
           (copyVertices dans MmapExtractorService : (−v.Y, v.Z, −v.X))

Index Detour d'un sous-tile dans l'ADT (adtX, adtY), sous-tile (subX, subY) :
  detourTileX = (maxAdtX - adtX) * 4 + subX    (subX ∈ [0,3])
  detourTileY = (maxAdtY - adtY) * 4 + subY    (subY ∈ [0,3])

Identique à HB MeshMapCalculator :
  GetDetourTile(adt, subX, subY).X = (adt.X - 32) * 4 + subX
```

---

## Connexions offmesh

Format du fichier texte (champ `OffMeshPath`) :

```
# mapId  tileX,tileY  (x y z)  (x y z)  rayon  [areaType]  [direction]
0 32,48  (476.47 50.86 -331.81)  (472.84 47.51 -319.17)  0.40 1 1
```

- `areaType` : 1 par défaut (GROUND)
- `direction` : 1 = bidirectionnel, 0 = unidirectionnel
- Verts stockés en espace Recast : `(p.Y, p.Z, p.X)` (même convention que C++ TerrainBuilder)
- `flags` : 0x2F (toutes les queries non-transport)

---

## Paramètres Recast — Comparaison avec l'original C++

| Paramètre | Notre extractor | C++ `MapBuilder.cpp` (`bigBaseUnit=false`) | Match |
|---|---|---|---|
| `cs` | 0.303030 | 0.303030 | ✅ |
| `ch` | 0.2 | 0.2 | ✅ |
| `walkableSlopeAngle` | 50° | 50° | ✅ |
| `walkableHeight` | 11 | 11 | ✅ |
| `walkableRadius` | 2 | 2 | ✅ |
| `walkableClimb` | 5 | 5 | ✅ |
| `borderSize` | radius+3 = 5 | radius+3 = 5 | ✅ |
| `minRegionArea` | 400 | rcSqr(20)=400 | ✅ |
| `mergeRegionArea` | 1600 | rcSqr(40)=1600 | ✅ |
| `maxSimplificationError` | 1.3 | 1.3 | ✅ |
| `MINI_TILE_SIZE` | 80 | VERTEX_PER_TILE=80 | ✅ |

`BigBaseUnit` : option morte dans notre extractor (présente dans l'UI, jamais passée au service). Dans le C++ original, `bigBaseUnit=true` changerait `cs=0.533333`, `ch=0.4`, etc. — HB n'utilise pas cette option.

---

## Différences avec l'extracteur MaNGOS C++ original

### Format de sortie — principal changement

| | C++ original MaNGOS | Ce projet (format HB) |
|---|---|---|
| Fichier `.mmap` | `{mapId:D3}.mmap` — paramètres `dtNavMeshParams` | Absent (HB n'en a pas besoin) |
| Fichier `.mmtile` | un fichier par ADT, contient le blob Detour brut | un fichier par ADT, **header PAMM 20 bytes** + 16 × `[uint32 size + blob]` |
| Taille tile Detour | `GRID_SIZE = 533.333 m` (1 tile par ADT) | `133.333 m` (4×4 = 16 sous-tiles par ADT) |
| Nom de fichier | `{mapId:D3}{adtY:D2}{adtX:D2}.mmtile` | identique |

### Pipeline navmesh

| | C++ original | Ce projet |
|---|---|---|
| `buildMoveMapTile` | boucle `TILES_PER_MAP × TILES_PER_MAP` mini-tiles → 1 tile Detour par ADT | `BuildNavMeshSubTilesSync` → 16 × `BuildNavMeshTileSync` → 16 blobs Detour |
| Appel Recast | direct depuis C++ | P/Invoke via `RecastBuilderDll.dll` |
| Parallélisme | ACE threadpool | `Parallel.ForEachAsync` avec `MaxDegreeOfParallelism` |

### Géométrie

| | C++ original | Ce projet |
|---|---|---|
| Terrain ADT | `TerrainBuilder::loadMap` | `MmapExtractorService.ExtrudeTileGeometry` |
| Vmaps / M2 | `TerrainBuilder::loadVMap` (fichiers `.vmtile` pré-extraits) | Lecture directe MPQ (pas de fichiers intermédiaires) |
| WMO | idem vmaps | idem |
| GameObjects | `TerrainBuilder::loadGameObjects` via `.obj` binaire | `LoadGoSpawns` depuis `gameobject_spawns.bin` |
| Offmesh | `TerrainBuilder::loadOffMeshConnections` | `LoadOffMeshConnections` (portage direct) |

### Chemins MPQ — bug critique corrigé

C++ utilise `%u` (pas de zéro-padding) : `Azeroth_32_48.adt`.  
Le C# original utilisait `{tileX:D2}_{tileY:D2}` → **100% des tiles Outland/Northrend échouaient.**  
Fix : `{mapName}_{tileX}_{tileY}.adt` sans padding — voir `AdtFile.cs`, `VmapExtractorService.cs`, etc.

### Gestion des coordonnées WMO / M2

| | C++ original | Ce projet (fix appliqué) |
|---|---|---|
| Vertices WMO | double-transform (bug) | `AppendWmoGeometryAsync` passe `(v.X, v.Y, v.Z)` brut |
| M2 GameObjects | mauvais flip | `LoadGameObjectM2` : `fixCoords = (vz, vx, vy)` + flip winding |

---

## Bugs corrigés vs extractor C++ original

| Bug | Cause | Fix |
|---|---|---|
| `NativeBuildLock` bloquait le parallélisme | Lock statique autour de `BuildTile` | Supprimé — `BuildTile` est stateless |
| Stop ne stoppait pas | `catch(Exception)` avalait `OperationCanceledException` | `when (ex is not OperationCanceledException)` |
| `area=16` Alliance incorrecte | Mauvais mappage area C# | `area=1` pour tout le terrain walkable |
| ADT D2 padding | `{tileX:D2}` → `World_032_048.adt` (introuvable) | `{tileX}_{tileY}` sans padding |
| `WdtReader` ne se réinitialise pas | `_tileExists` non remis à zéro | `Array.Clear(_tileExists)` dans `LoadAsync` |
| WMO crash | chunks inconnus non ignorés | `default: reader.Skip()` dans `WmoParser` |
| Vertices WMO double-transformés | Coordonnées appliquées deux fois | Passage des coords brutes dans `AppendWmoGeometryAsync` |
| GO M2 coords incorrectes | Mauvais ordre flip | `fixCoords = (vz, vx, vy)` + winding inversé pour M2 |
| Crash 2ème lancement (NaN) | `SaveConfig` pendant `LoadConfig` → sérialise `NaN` | Guard `_isLoadingConfig` + sanitisation NaN |
| CS7022 (entry point hijacked) | Fichier `src/-n/Program.cs` fantôme | Supprimé du projet |
| `Map.dbc` introuvable | Double backslash `@"DBFilesClient\\Map.dbc"` | Backslash simple |

---

## Points déférés

| # | Sujet | Fichier |
|---|---|---|
| 1 | Quaternion GO — vérifier ordre `G3D::Quat(w,x,y,z)` | `MmapExtractorService.cs` |
| 2 | `cleanVertices` — déduplication sommets identiques | `MmapExtractorService.cs` |
| 3 | Liquides ADT / WMO — validation surfaces eau/lava | `AdtFile.cs` |
| 4 | `DT_POLYREF64` dans `RecastBuilderDll.vcxproj` — HB attend peut-être 32-bit polyRefs | `RecastBuilderDll.vcxproj` |
| 5 | Override Dalaran (map 571) — `borderSize = walkableRadius + 5` | `MmapExtractorService.cs` |
