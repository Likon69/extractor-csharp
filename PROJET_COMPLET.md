# MaNGOS Extractor C# — Documentation complète du portage

> WoW 3.3.5a (build 12340) — Portage complet C++ → C# .NET 10 WPF  
> Généré après fin de session — Mai 2026

---

## 1. Vue d'ensemble

Le projet est un **portage complet** du générateur de navmesh MaNGOS C++ en C# moderne (.NET 10, WPF).  
Il produit les mêmes fichiers que l'extracteur C++ original, compatibles avec HonorBuddy (Navigation.dll).

### Ce que fait le projet
- Lit les archives MPQ de WoW 3.3.5a (via StormLib)
- Extrait les fichiers de carte (`.map`), vmaps, road-maps et **mmaps (navmesh)**
- Génère des fichiers `.mmtile` au format **HonorBuddy** : 1 ADT → 16 sous-tiles (4×4) dans un fichier unique
- Interface WPF avec sélection de cartes, phases, paramètres Recast, et grille de progression

---

## 2. Architecture du projet

```
extractor-csharp/
├── src/
│   ├── CLI/                     — Interface ligne de commande
│   ├── Core/
│   │   ├── Constants/           — MagicBytes, WowConstants
│   │   ├── Interfaces/          — IArchiveReader
│   │   └── Models/              — ExtractorConfig, GoSpawn, TileProgressEvent
│   ├── Formats/
│   │   ├── Adt/                 — Parseur ADT (terrain, liquides, spawns M2/WMO)
│   │   ├── Dbc/                 — Lecteur DBC (Map.dbc, AreaTable.dbc, etc.)
│   │   ├── M2/                  — Parseur modèles M2 (collision)
│   │   ├── Mpq/                 — Wrapper StormLib (MpqArchive, MpqArchiveCollection)
│   │   ├── Vmap/                — Écriture VMAPt07
│   │   ├── Wdt/                 — Lecteur WDT (liste des ADT existants)
│   │   └── Wmo/                 — Parseur WMO (groupes, triangles, collision)
│   ├── MapExtractor/            — Extraction .map (heightmap, liquides, area flags)
│   ├── MmapExtractor/           — Extraction navmesh (cœur du projet)
│   │   ├── MmapExtractorService.cs
│   │   └── Recast/
│   │       └── RecastNativeBridge.cs   — P/Invoke → RecastBuilderDll.dll
│   ├── RoadExtractor/           — Extraction road-maps
│   ├── VmapExtractor/           — Extraction VMaps
│   └── UI/
│       ├── MainWindow.xaml      — Interface WPF
│       ├── MainWindow.xaml.cs   — Save/restore position fenêtre
│       ├── Config/              — ConfigFileManager (JSON)
│       └── ViewModels/
│           ├── MainViewModel.cs — VM principal, commandes, config
│           └── TileGridViewModel.cs — Grille de progression 64×64
└── native/
    └── RecastBuilderDll/        — DLL C++ native (Recast/Detour)
        └── RecastBuilderDll.cpp
```

---

## 3. Système de navmesh — Le portage principal

### 3.1 Format de sortie HonorBuddy (4×4 par ADT)

Chaque ADT (512m × 512m) est découpé en **16 sous-tiles Detour** (4 lignes × 4 colonnes).  
Ils sont tous écrits dans **un seul fichier `.mmtile`** :

```
Nom du fichier : {mapId:D3}{adtY:D2}{adtX:D2}.mmtile
Exemple        : 0004832.mmtile  → map=000, adtY=48, adtX=32
```

Structure du fichier :
```
[Header 20 bytes]
  uint32  magic        = 0x4D4D4150  ("MMAP")
  uint32  dtVersion    = 7           (Detour v7)
  uint32  mmapVersion  = 5
  uint32  subTileCount = 16          (4×4)
  uint32  usesLiquids  = 1

[16 × sous-tile]
  uint32  dataSize
  byte[]  detourNavMeshData
```

### 3.2 Pipeline de construction d'un tile

```
Parallel.ForEachAsync(adtTiles)          ← N threads (configurable 1–20)
  └── ProcessTileAsync(adtX, adtY)
        ├── LoadTileGeometryAsync()       ← charge ADT + voisins 3×3
        │     ├── ExtrudeTileGeometry()  ← terrain ADT (hauteur + liquides)
        │     ├── AppendM2Geometry()     ← modèles M2 (avec transform)
        │     └── AppendWmoGeometryAsync() ← WMO (groupes de collision)
        ├── BuildNavMeshSubTilesSync()   ← 16 appels Recast
        │     ├── filtre offmesh connections pour cet ADT
        │     └── BuildNavMeshTileSync() × 16
        │           └── RecastNative.BuildTile() ← DLL C++ native
        └── WriteMmtileAsync()           ← écrit le fichier .mmtile
```

### 3.3 Coordonnées Detour (même calcul que le C++ original)

```csharp
// Index Detour pour sous-tile (subX, subY) dans l'ADT (adtX, adtY)
int detourTileX = (maxAdtX - adtX) * 4 + subX;
int detourTileY = (maxAdtY - adtY) * 4 + subY;

// Espace Recast (conversion WoW → Recast)
// copyVertices: Recast(X, Y, Z) = (v.Y, v.Z, v.X)
// avec flip X et Y : v.X *= -1; v.Y *= -1;
```

### 3.4 Connexions offmesh (portage de TerrainBuilder::loadOffMeshConnections)

Format du fichier texte :
```
mapId tx,ty (x y z) (x y z) taille [areaType] [direction]
# exemple :
546 32,31 (476.47 50.86 -331.81) (472.84 47.51 -319.17) 0.40 1
```

- Commentaires avec `#` ou `//`
- `areaType` = 1 par défaut, `direction` = 1 (bidirectionnel) par défaut
- Verts stockés en espace Recast : `(p0[1], p0[2], p0[0])` — même convention que le C++
- `flags = 0x2F` (traversable par toutes les queries non-transport)

---

## 4. DLL native — RecastBuilderDll

### Signature C++ (14 paramètres)

```cpp
bool BuildTile(
    const RecastBuildParams* params,
    const float* verts,     int vertCount,
    const int*   tris,      int triCount,
    const uint8_t* areaIds,
    const float*   offMeshConVerts,
    const float*   offMeshConRads,
    const uint8_t* offMeshConDirs,
    const uint8_t* offMeshConAreas,
    const uint16_t* offMeshConFlags,
    int   offMeshConCount,
    uint8_t** outData,      int* outSize
);
```

### Paramètres Recast (RecastBuildParams)

```
CellSize          = 0.30303  (GRID_SIZE / 100 ≈ 533.33 / 1600 × 0.909...)
CellHeight        = 0.2
WalkableSlopeAngle= 50°
WalkableHeight    = 11  (voxels)
WalkableRadius    = 2
WalkableClimb     = 5
SubTileSize       = 133.333333f  (GRID_SIZE / 4)
```

### Build & deploy

```powershell
# Build C#
cd extractor-csharp
dotnet build MaNGOS.Extractor.sln -c Debug --nologo

# Deploy DLL native après chaque build (obligatoire)
Copy-Item "native\RecastBuilderDll\bin\Debug\RecastBuilderDll.dll" `
          "src\bin\Debug\net10.0-windows\" -Force
```

---

## 5. Géométrie — Sources et portage

### Terrain ADT (ExtrudeTileGeometry)
- Lecture des chunks `MCVT` (hauteurs) + `MCNK` (flags, area ID)
- Triangulation 8×8 chunks × 9×9 + 8×8 points par chunk
- Extrusion vers le bas des bords de tile (pour combler les gaps entre ADTs)
- Liquides `MH2O` : surface plane + triangulation
- Flags area depuis `AreaTable.dbc` → types Recast (eau, lava, etc.)

### Modèles M2 (AppendM2Geometry)
- Lecture header M2 + vertices + triangles de la skin (LOD 0)
- Transform : rotation quaternion + scale + translation
- Flip X/Y pour espace Recast
- Filtrage par flags de collision

### WMO (AppendWmoGeometryAsync)
- Lecture root WMO + groupes de collision
- Rotation Euler XYZ (G3D `fromEulerAnglesXYZ`)
- Flags matériaux : `NoCollision` et `CollideHit` / `Hint`
- Filtre les groupes sans collision

### GameObjects (LoadGoSpawns)
- Lecture `gameobject_spawns.bin` (binaire custom)
- Chargement `GameObjectDisplayInfo.dbc` pour le modèle M2
- Inclus dans la géométrie comme modèles M2 avec leur transform in-world

---

## 6. Interface utilisateur (WPF)

### Fonctionnalités
- Sélection des cartes via `Map.dbc` (avec checkbox "Toutes les cartes")
- Phases : Map / Vmap / Road / Mmap (checkboxes, sauvegardées)
- Grille de progression 64×64 (Pending / Building / Done / Failed)
- Log en temps réel avec couleurs par niveau (Info / Warning / Error)
- Barre de stop avec annulation immédiate (CancellationToken)

### Paramètres sauvegardés (ExtractorConfig.json)
```json
{
  "WowClientPath", "OutputPath", "OffMeshPath", "GoSpawnsPath",
  "Locale", "Phases", "Maps", "Threads",
  "BigBaseUnit", "SingleTileEnabled", "SingleTileX", "SingleTileY",
  "RecastConfig": { "CellSize", "CellHeight", "WalkableSlopeAngle",
                    "WalkableHeight", "WalkableRadius", "WalkableClimb" },
  "WindowLeft", "WindowTop", "WindowWidth", "WindowHeight"
}
```

### Slider threads
- Slider de 1 à 20 (pas entier, snap-to-tick)
- Label valeur courante à gauche du slider
- Par défaut : 1 thread

---

## 7. Bugs corrigés durant le développement

| Bug | Cause | Fix |
|-----|-------|-----|
| App crash au 2ème lancement | `SaveConfig()` appelé pendant `LoadConfig()` via phase.IsEnabled → sérialise `double.NaN` → `System.Text.Json` lève exception | `_isLoadingConfig` guard dans `try/finally` + sanitisation NaN dans `SaveConfig()` |
| Un seul cœur utilisé malgré N threads | `NativeBuildLock` statique sérialisait tous les appels `BuildTile` | Suppression du lock — `BuildTile` est stateless |
| Stop ne stoppait pas immédiatement | `catch (Exception ex)` avalait `OperationCanceledException` | `catch (Exception ex) when (ex is not OperationCanceledException)` + `ct.ThrowIfCancellationRequested()` dans la boucle des sous-tiles |
| CS0246 `OffMeshConnection` not found | Struct et méthode jamais implémentés | Portage complet depuis `TerrainBuilder::loadOffMeshConnections` |
| BuildTile wrong arg count | Signature P/Invoke à 8 params au lieu de 14 | Mise à jour signature + passage des 6 paramètres offmesh |
| Position fenêtre NaN au 1er lancement | `WindowLeft/Top` initialisés à `double.NaN`, serialisés avant `OnClosed` | Guard `_isLoadingConfig` + valeur de repli `0.0` |

---

## 8. Test d'une tile

```powershell
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll" `
    --wow "C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a original" `
    --out output_test `
    --maps 0 --phases Mmap --tile 32,48 --threads 1
```

### Résultat vérifié
```
output_test\mmaps\000.mmap        — 28 bytes  (header de carte)
output_test\mmaps\0004832.mmtile  — 614 308 bytes

Header décodé :
  magic=0x4D4D4150  dtVer=7  mmapVer=5  subTiles=16  usesLiquids=1
  Tailles des 16 sous-tiles : 36976 26600 32952 36716 37548 27660
                               18612 40212 26764 50648 51808 51400
                               40388 25804 51236 58900
  → Toutes non-nulles ✅
```

---

## 9. Points encore à valider (déférés)

1. **Quaternion GameObjects** — ordre des paramètres `G3D::Quat(w,x,y,z)` à vérifier
2. **`cleanVertices`** — déduplication des vertices identiques (comme C++ original)
3. **Liquides ADT / WMO** — validation que les surfaces eau/lava sont correctes
4. **`DT_POLYREF64`** dans `RecastBuilderDll.vcxproj` — produit des polyRefs 64-bit; HonorBuddy attend peut-être 32-bit — à tester avec Navigation.dll

---

## 10. Commandes utiles

```powershell
# Build
cd extractor-csharp
dotnet build MaNGOS.Extractor.sln -c Debug --nologo

# Deploy DLL native
Copy-Item "native\RecastBuilderDll\bin\Debug\RecastBuilderDll.dll" `
          "src\bin\Debug\net10.0-windows\" -Force

# Lancer l'UI
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll"

# Test tile unique
dotnet "src\bin\Debug\net10.0-windows\MaNGOS.Extractor.dll" `
    --wow "C:\...\World of Warcraft 3.3.5a original" `
    --out output_test --maps 0 --phases Mmap --tile 32,48 --threads 1

# Inspecter un .mmtile
$b=[System.IO.File]::ReadAllBytes("output_test\mmaps\0004832.mmtile")
[System.BitConverter]::ToUInt32($b,0)   # magic
[System.BitConverter]::ToUInt32($b,12)  # nb sous-tiles
```
