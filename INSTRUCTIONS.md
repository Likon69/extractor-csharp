# MaNGOS Unified Extractor — C# Rewrite

## Contexte

Ce projet est la réécriture complète en C# des 4 extracteurs C++ MaNGOS (WoW 3.3.5a / WotLK build 12340).  
Les extracteurs C++ originaux se trouvent dans `../mangostwo-server/src/tools/Extractor_projects/`.

**Objectif** : Un seul exécutable .NET 8 **avec interface graphique WPF** qui exécute les 4 phases d'extraction séquentiellement ou indépendamment, avec visualisation en temps réel de la progression des tiles sur une grille 2D de la carte WoW. Paramètres configurables via l'interface ou un fichier `ExtractorConfig.json`. Sans aucune dépendance à ACE, MySQL ou au serveur MaNGOS.

---

## Les 4 extracteurs originaux et leur rôle

### 1. `map-extractor`
- Lit les fichiers MPQ du client WoW (`*.mpq`)
- Parse les fichiers **ADT** (terrain, 16×16 MCNK chunks par tuile), **WDT** (index world 64×64), **DBC** (database tables)
- Produit les fichiers `.map` (hauteurs de terrain, zone/area IDs, liquides, flags)
- Output : `maps/{mapId:03d}{gx:02d}{gy:02d}.map` (ex: `maps/00003232.map`)

### 2. `vmap-extractor`
- Lit les MPQ pour extraire les modèles 3D : **WMO** (world map objects), **M2** (doodads/models)
- Produit les fichiers de visibility map (vmaps4) nécessaires au pathfinding
- Output : `vmaps/` (BVH trees, tile trees)

### 3. `road-extractor`
- Lit les fichiers ADT directement depuis les MPQ (chunks MCLY — couches texture)
- Détecte les MCNK qui contiennent une texture de type route ("road", "cobblestone", "path_stone", "bridgefloor", etc.)
- Output : `roadmaps/{mapId:03d}{gx:02d}{gy:02d}.road` — 256 bytes, 1 byte par MCNK (0=normal, 1=route détectée)

### 4. `mmap-extractor` (Movemap Generator)
- Lit les `.map` et vmaps produits par les phases précédentes
- Construit la géométrie de navigation (terrain + modèles)
- **Appelle Recast/Detour via P/Invoke** pour générer les navigation meshes
- Supporte le multithreading par tile
- Output : `mmaps/{mapId:000}.mmap` + `mmaps/{mapId:000}{subTileY:03}{subTileX:03}.mmtile`

> **Architecture 4×4 (HB-style)** : chaque ADT WoW génère **16 tiles Detour** (grille 4×4 = 133.333f par tile). Confìrmé par l'analyse HB : `MetaData.json → DetourTileSize = 133.333328f` et 16 sous-headers VAND par `.msh`. Les coords de sous-tiles vont de 0–255, d'où 3 chiffres dans le nom de fichier.

---

## Décisions d'architecture

### Stack technique
- **.NET 8** — C# 12 — `<Nullable>enable</Nullable>`
- **WPF** (`net8.0-windows`) — interface graphique principale, **aucun NuGet supplémentaire**
- **Solution multi-projets** (pas un seul gros projet)
- **P/Invoke** pour StormLib et Recast/Detour (DLLs C++ existantes)
- **System.Numerics** (Vector3, Matrix4x4) — remplace G3D
- **`Span<byte>` / `MemoryMarshal`** pour la lecture binaire haute performance (pas System.IO.Pipelines — inadapté au parsing de fichiers)
- **`IProgress<TileProgressEvent>`** pour le reporting temps réel UI ↔ services
- **`System.Text.Json`** pour `ExtractorConfig.json`
- **Microsoft.Extensions.Logging** pour les logs
- *(pas de System.CommandLine — le CLI est secondaire, l'UI est le point d'entrée principal)*

### Ce qui reste en C++
- `StormLib.dll` — lecture MPQ (P/Invoke)
- `RecastDetour.dll` — génération navmesh (P/Invoke) — **⚠ thin wrapper C++ à écrire** (voir section dédiée plus bas)
  - Recast/Detour vanilla n'expose PAS d'API C — il faut un wrapper DLL custom qui expose des fonctions `extern "C"` prenant des tableaux plats (float*, int*)

### Ce qui est réécrit en C#
- Parsing binaire : ADT, WDT, DBC, WMO, M2
- Logique de terrain : heightmap, liquids, flags
- Logique vmap : BIH tree, model assembly
- Logique mmap : TerrainBuilder, geometry pipeline → appel Recast
- **UI WPF** : visualisation grille 64×64 en temps réel, configuration
- Config JSON, logging, parallel job system

### `gameobject_spawns.bin` — requis pour le mmap-extractor
Fichier binaire de spawns de GameObjects (portes, objets interactifs) que le mmap builder rasterise dans le navmesh. Localisé dans le dossier courant ou configuré via `ExtractorConfig.json`.

**Format** (magic `GPSW`, version 1) :
```
Header : [uint32 magic=0x47505753][uint32 version=1][uint32 totalCount]
Par entrée (40 bytes) :
  [uint32 mapId][uint32 displayId]
  [float posX][float posY][float posZ]
  [float rot0][float rot1][float rot2][float rot3]
  [float scale]
```
Fichier à placer dans `extractor-csharp/gameobject_spawns.bin` (défaut) ou configurer `GoSpawnsPath` dans `ExtractorConfig.json`.

---

## Structure du projet

```
extractor-csharp/
├── INSTRUCTIONS.md             ← ce fichier
├── PROMPT.md                   ← prompt IA
├── src/
│   ├── MaNGOS.Extractor.sln
│   ├── Core/                   ← MaNGOS.Extractor.Core
│   │   ├── Interfaces/         ← IArchiveReader, IMapExtractor, IExtractionProgress
│   │   ├── Models/             ← ADT, WDT, DBC row types, MapFile, TileProgressEvent
│   │   ├── Constants/          ← BuildNumbers, MagicBytes, ChunkIds
│   │   └── Math/               ← Vector3Ex, BoundingBox, CoordConvert
│   ├── Formats/                ← MaNGOS.Extractor.Formats
│   │   ├── Mpq/                ← StormLib P/Invoke + IMpqArchive
│   │   ├── Adt/                ← AdtReader, McnkChunk, McinChunk, etc.
│   │   ├── Wdt/                ← WdtReader
│   │   ├── Dbc/                ← DbcReader<T>, generated row types
│   │   ├── Wmo/                ← WmoReader (root + group)
│   │   └── M2/                 ← M2Reader (headers + collision mesh)
│   ├── MapExtractor/           ← MaNGOS.Extractor.Map
│   │   ├── MapExtractorService.cs
│   │   ├── HeightmapBuilder.cs
│   │   ├── LiquidBuilder.cs
│   │   └── MapFileWriter.cs
│   ├── VmapExtractor/          ← MaNGOS.Extractor.Vmap
│   │   ├── VmapExtractorService.cs
│   │   ├── ModelAssembler.cs
│   │   ├── BihBuilder.cs
│   │   └── VmapFileWriter.cs
│   ├── RoadExtractor/          ← MaNGOS.Extractor.Road
│   │   └── RoadExtractorService.cs
│   ├── MmapExtractor/          ← MaNGOS.Extractor.Mmap
│   │   ├── MmapExtractorService.cs
│   │   ├── TerrainMeshBuilder.cs
│   │   ├── TileJobScheduler.cs
│   │   └── Recast/
│   │       ├── IRecastBridge.cs
│   │       ├── RecastNativeBridge.cs   ← P/Invoke
│   │       └── RecastParams.cs
│   └── CLI/                    ← MaNGOS.Extractor.CLI  (secondaire)
│       ├── Program.cs
│       ├── Commands/           ← ExtractMapCommand, etc.
│       └── Config/             ← ExtractorConfig (JSON)
│   └── UI/                     ← MaNGOS.Extractor.UI  (WPF, net8.0-windows)
│       ├── App.xaml
│       ├── MainWindow.xaml
│       ├── Controls/
│       │   └── MapGridControl.xaml   ← grille 64×64 WriteableBitmap
│       ├── ViewModels/
│       │   ├── MainViewModel.cs
│       │   ├── TileGridViewModel.cs
│       │   └── ExtractorSettingsViewModel.cs
│       └── Config/
│           └── ConfigFileManager.cs  ← charge/sauve ExtractorConfig.json
└── tests/
    └── MaNGOS.Extractor.Tests/
```

## Interface graphique — WPF (net8.0-windows)

**Aucun NuGet supplémentaire** — WPF est inclus dans .NET 8 pour Windows.

### Disposition de la fenêtre

```
+-------------------------------------------------------+
| [▶ Démarrer] [■ Stop]  Phase: [▾ Toutes]            |
+--------------------+----------------------------------+
| CARTES             | GRILLE 64×64 (WriteableBitmap)   |
| [0] Azeroth  ✓    |  # ■ = tile présente / couleur = état |
| [1] Kalimdor ✓    |                                  |
| [530] Outland...   |  [■■■■■■■■■]               |
| [571] Northrend ✓  |  [■■■■■■■■■]               |
|                    |  [■■■■■■■■■]               |
| PARAMÈTRES RECAST  |                                  |
| CellSize: [0.303]  |                                  |
| Threads:  [4    ]  |                                  |
| BigBase:  [ ]      |                                  |
+--------------------+----------------------------------+
| [■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■] 234/512 tiles
| [Log output scrollable — niveaux Info/Warn/Error    ] |
+-------------------------------------------------------+
```

### Couleurs de tiles
| Couleur | Signification |
|---|---|
| `#2d2d2d` noir | Tile n'existe pas sur cette carte (pas dans WDT) |
| `#4a4a6a` gris | Tile présente, en attente de traitement |
| `#f0a500` orange | En cours (actif dans un thread) |
| `#2ecc71` vert | Terminé avec succès |
| `#e74c3c` rouge | Erreur |

### `IProgress<TileProgressEvent>` — binding UI ↔ services
```csharp
// Core/Models/TileProgressEvent.cs
public record TileProgressEvent(
    int MapId,
    int TileX,
    int TileY,
    TileStatus Status,
    ExtractionPhase Phase,
    string? Message = null);

public enum TileStatus { Pending, Processing, Done, Failed }
public enum ExtractionPhase { Map, Vmap, Road, Mmap }
```
Chaque service (`MapExtractorService`, `MmapExtractorService`, etc.) reçoit un `IProgress<TileProgressEvent>` et appelle `progress.Report(...)` après chaque tile. Le `TileGridViewModel` s'abonne et met à jour le `WriteableBitmap` via `Dispatcher.InvokeAsync`.

### `ExtractorConfig.json`
```json
{
  "WowClientPath": "C:/World of Warcraft",
  "OutputPath": "D:/wow-data/output",
  "GoSpawnsPath": "gameobject_spawns.bin",
  "Phases": ["Map", "Vmap", "Road", "Mmap"],
  "Maps": [0, 1, 530, 571],
  "Threads": 4,
  "BigBaseUnit": false,
  "Recast": {
    "CellSize": 0.303030,
    "CellHeight": 0.2,
    "WalkableSlopeAngle": 50.0,
    "WalkableHeight": 11,
    "WalkableRadius": 2,
    "WalkableClimb": 5
  }
}
```

---

## RecastDetour.dll — Thin wrapper C++ à créer

Recast/Detour vanilla (1.6.0) **n'expose PAS d'API C**. Il faut créer un nouveau projet DLL C++ minimal qui enveloppe le pipeline de construction de tiles.

### ⚠ Ne pas télécharger une nouvelle version de Recast
Le projet est déjà sur **Recast 1.6.0** (mis à jour le 16/05/2026). Les `.lib` précompilés se trouvent dans `../mangostwo-server/dep/recastnavigation/`. Utiliser une version différente = format navmesh incompatible.

### Projet à créer : `native/RecastBuilderDll/`
- Localisation : `extractor-csharp/native/RecastBuilderDll/` (dans le dépôt)
- Liens contre : `Recast.lib` + `Detour.lib` de `dep/recastnavigation/`
- Compile flags : **même que le reste** — MSVC x64, `/MD`, `/O2`, `/D DT_POLYREF64`
- `/D DT_POLYREF64` est **obligatoire** (= `dtTileRef` est `uint64_t`, sans = crash/mismatch)

### API `extern "C"` à exposer
```cpp
// RecastBuilderDll/RecastApi.h
extern "C"
{
    // Construit un tile navmesh à partir de géométrie plate
    // Retourne true si OK ; *outData et *outSize = buffer alloué (libérer avec Recast_Free)
    __declspec(dllexport)
    bool Recast_BuildTile(
        const RecastBuildParamsC* params,
        const float* verts, int vertCount,       // positions (x,y,z)*vertCount
        const int*   tris,  int triCount,        // triplets d'indices
        const uint8_t* areaIds,                  // 1 id de zone par triangle
        uint8_t** outData, int* outSize);        // tile navmesh alloué

    __declspec(dllexport)
    void Recast_Free(void* ptr);                 // libère outData
}

struct RecastBuildParamsC
{
    float cs, ch, walkableSlopeAngle;
    int   walkableHeight, walkableRadius, walkableClimb;
    int   minRegionArea, mergeRegionArea;
    float maxSimplificationError;
    int   tileX, tileY;
    float bmin[3], bmax[3];
    int   maxVertsPerPoly;
};
```

---

## Conventions de code (style apocdex/HB)

### Règles absolues
1. **Aucun `static` state** — tout passe par injection de dépendances ou paramètres
2. **Aucun magic number** — toutes les constantes sont nommées dans `Constants/`
3. **Nullable enabled** — `string?` explicite partout, pas de NPE silencieuses
4. **`readonly struct`** pour les types de données immuables (Vector3, BoundingBox, ChunkId)
5. **`Span<byte>` / `Memory<byte>`** pour la lecture binaire — zéro allocation inutile
6. **Interfaces pour tout ce qui est I/O** — `IArchiveReader`, `IFileSystem` → testable
7. **`ILogger<T>`** partout — jamais `Console.WriteLine` dans la logique métier
8. **`CancellationToken`** propagé dans toutes les méthodes async
9. **Pas d'héritage profond** — préférer composition + interfaces
10. **`record` types** pour les configurations immuables

### Naming
```csharp
// Types : PascalCase
public readonly struct ChunkId { ... }
public interface IArchiveReader { ... }
public sealed class AdtReader : IAdtReader { ... }

// Méthodes : PascalCase verbes
public ValueTask<AdtFile> ReadAsync(string path, CancellationToken ct)

// Constantes : PascalCase dans une classe statique sealed
public static class WowConstants
{
    public const int TileSize = 533;
    public const int ChunksPerTile = 16;
    public const float MapHalfSize = 17066.666f;
}
```

### Parsing binaire (BinaryReader remplacé par Span)
```csharp
// ✅ Correct
private static AdtHeader ParseHeader(ReadOnlySpan<byte> data)
{
    ref readonly var raw = ref MemoryMarshal.AsRef<RawAdtHeader>(data);
    return new AdtHeader(raw.Flags, raw.OffsetMcin);
}

// ❌ Interdit
var reader = new BinaryReader(stream); // trop lent, trop d'allocations
```

---

## Contrats P/Invoke — StormLib

```csharp
// Formats/Mpq/StormLib.cs
internal static class StormLib
{
    private const string DllName = "StormLib";

    [DllImport(DllName, EntryPoint = "SFileOpenArchive", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenArchive(string mpqName, uint priority, uint flags, out IntPtr archive);

    [DllImport(DllName, EntryPoint = "SFileOpenFileEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenFile(IntPtr archive, string archivedFile, uint searchScope, out IntPtr fileHandle);

    [DllImport(DllName, EntryPoint = "SFileReadFile")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool ReadFile(IntPtr file, void* buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport(DllName, EntryPoint = "SFileCloseFile")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseFile(IntPtr file);

    [DllImport(DllName, EntryPoint = "SFileCloseArchive")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseArchive(IntPtr archive);
}
```

## Contrats P/Invoke — Recast/Detour

```csharp
// MmapExtractor/Recast/RecastNativeBridge.cs
internal static class RecastNative
{
    private const string DllName = "RecastDetour"; // DLL C++ à builder séparément

    [DllImport(DllName)]
    internal static extern unsafe bool Recast_BuildTile(
        RecastBuildParams* p,
        float* verts, int vertCount,
        int* tris, int triCount,
        byte* areaIds,
        byte** outNavData, int* outNavDataSize);

    [DllImport(DllName)]
    internal static extern unsafe void Recast_FreeBuffer(void* buffer);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RecastBuildParams
{
    public float CellSize;
    public float CellHeight;
    public float AgentHeight;
    public float AgentRadius;
    public float AgentMaxClimb;
    public float AgentMaxSlope;
    public int TileX;
    public int TileY;
    // ... (voir RECAST_PARAMS_HISTORY.md pour les valeurs MaNGOS validées)
}
```

---

## Coordonnées WoW → Recast

```
WoW (espace monde) : X = Est/Ouest,  Y = Altitude (haut),  Z = Nord/Sud
Recast             : X = Est/Ouest,  Y = Altitude (haut),  Z = Nord/Sud
→ Pas de flip d'axe, Y = altitude dans les deux systèmes.

Conversion d'une tuile (tileX, tileY) → coin min monde :
  originX = (32 - tileX) * TileSize    // TileSize = 533.333f
  originZ = (32 - tileY) * TileSize

Les vertices ADT (MCVT) sont stockés relatifs à l'origine du chunk :
  worldX = originX - (mcnkIndexX * ChunkSize) - vx
  worldZ = originZ - (mcnkIndexY * ChunkSize) - vy
  worldY = mcnkBaseHeight + mcvtDelta   // altitude directe, pas d'inversion

⚠ Note : dans les fichiers ADT, les indices de grille vont du coin
  NORD-OUEST vers le SUD-EST. Le signe négatif ci-dessus (- vx / - vy)
  est nécessaire pour retourner l'axe et aligner sur Recast.
  Voir TerrainBuilder.cpp lignes ~200-280 pour l'implémentation de référence.
```

---

## Fichiers de référence C++ (à portager)

| C++ source | Correspondance C# |
|---|---|
| `TerrainBuilder.cpp` | `MmapExtractor/TerrainMeshBuilder.cs` |
| `MapBuilder.cpp` | `MmapExtractor/MmapExtractorService.cs` |
| `TileThreadPool.cpp` | `MmapExtractor/TileJobScheduler.cs` |
| `IntermediateValues.cpp` | intégré dans `TerrainMeshBuilder` |
| `VMapExtensions.cpp` | intégré dans `VmapExtractor/` |
| `loadlib/adt.cpp` | `Formats/Adt/AdtReader.cs` |
| `loadlib/wdt.cpp` | `Formats/Wdt/WdtReader.cs` |
| `loadlib/mpq.cpp` | `Formats/Mpq/MpqArchive.cs` |

---

## Paramètres Recast validés MaNGOS 3.3.5a

Valeurs extraites directement de `MapBuilder.cpp` (bigBaseUnit=false, mode normal).

```
// BASE_UNIT_DIM = 1 / 3.300003 = 0.303030f
cs (cellSize)            = 0.303030f   // HB bvQuantFactor=3.300003
ch (cellHeight)          = 0.2f
walkableHeight           = 11 cells    // 11 × 0.2 = 2.2 WoW units
walkableRadius           = 2 cells     // 2 × 0.303 ≈ 0.606 WoW units
walkableClimb            = 5 cells     // 5 × 0.2 = 1.0 WoW unit (step)
walkableSlopeAngle       = 50.0f       // HB WotLK confirmé (Apoc 2010)
                                       // ⚠ PAS 60° — cause le walk sur les falaises
minRegionArea            = rcSqr(20)   // = 400 sq cells
mergeRegionArea          = rcSqr(40)   // = 1600 sq cells
maxSimplificationError   = 1.3f
maxEdgeLen               = VERTEX_PER_TILE + 1 = 81 cells
detailSampleDist         = cs * 16 ≈ 4.848f
detailSampleMaxError     = ch * 1 = 0.2f
maxVertsPerPoly          = DT_VERTS_PER_POLYGON (= 6)

// dtNavMeshCreateParams — valeurs en WORLD UNITS (pas en cells) :
params.walkableHeight = 2.22155f    // exact HB — 11cells × 0.2ch → 2.22155
params.walkableRadius = 0.400f      // exact HB header value (NOT 0.606)
params.walkableClimb  = 1.0f        // exact HB — 5cells × 0.2ch = 1.0

// dtNavMeshParams (contenu du .mmap) — ARCHITECTURE 4×4 :
nmParams.tileWidth  = GRID_SIZE / 4 = 133.333333f  // HB-style, confirmé ANALYSE_VERIFIEE_NAVMESH.md
nmParams.tileHeight = GRID_SIZE / 4 = 133.333333f
nmParams.maxTiles   = numAdts * 16                 // 16 sous-tiles par ADT
nmParams.maxPolys   = 1 << DT_POLY_BITS
nmParams.orig[3]    = bmin de la tile (tileXMax, tileYMax)

// Cellules par sous-tile (4×4) :
// cellsPerSubTile = 133.333f / 0.303030f = 440 cells
// VERTEX_PER_TILE interne = 40 (divides 440 : 440/40 = 11 → 11×11 Recast sub-tiles fusionnés)
// config.maxEdgeLen = VERTEX_PER_TILE + 1 = 41

// Overrides par carte :
// Map 571 (Northrend/Dalaran) → cs=0.2424f, borderSize=walkableRadius+5
// Map 562 (Blade's Edge Arena) → walkableRadius=0 (pour marcher sur les cordes)
// Map  48 (Blackfathom Deeps) → ch × 2 (double height, souterrain multi-étage)
```

---

## FORMAT DES FICHIERS OUTPUT — Compatibilité Navigation.dll

> Ces formats sont lus par `MoveMap.cpp` (C++ Navigation.dll). Toute divergence = silently failed tile load ou crash.

### Fichier `.mmap` — Paramètres NavMesh par carte
**Nom** : `mmaps/{mapId:000}.mmap`  
**Contenu** : raw `dtNavMeshParams` struct — **aucun magic, aucun header supplémentaire**.  
Lu directement par `fread(&params, sizeof(dtNavMeshParams), 1, file)` dans `loadMapData()`.

### Fichier `.mmtile` — Tile navmesh individuelle
**Nom** : `mmaps/{mapId:000}{subTileY:03}{subTileX:03}.mmtile`  
⚠ **3 chiffres** (0–255) car chaque ADT génère 4×4 = 16 sous-tiles.  
`subTileX = adtX * 4 + dx` (dx ∈ 0–3), `subTileY = adtY * 4 + dy` (dy ∈ 0–3)  
⚠ **Y AVANT X** dans le nom — voir `getTileName()` dans MoveMap.cpp.

Navigation.dll requiert les changements suivants pour 4×4 :
```cpp
// MoveMap.cpp — getTileName() :
if (x < 10) tileName.append("0");
if (x < 100) tileName.append("0");  // ← ajouter (3 chiffres)
tileName.append(NumberToString(x));
if (y < 10) tileName.append("0");
if (y < 100) tileName.append("0");  // ← ajouter (3 chiffres)
tileName.append(NumberToString(y));
// Appelé avec sub-tile coords, pas ADT coords

// MoveMap.cpp — loadMap(mapId, adtX, adtY) :
// Charger les 16 sub-tiles pour cet ADT :
for (int dy = 0; dy < 4; ++dy)
    for (int dx = 0; dx < 4; ++dx)
        loadSubTile(mapId, adtX * 4 + dx, adtY * 4 + dy);
```

**Structure** :
```cpp
struct MmapTileHeader {           // 17 bytes
    uint32 mmapMagic;             // 0x4d4d4150 = 'MMAP'
    uint32 dtVersion;             // DT_NAVMESH_VERSION (= 7 pour Detour 1.6.0)
    uint32 mmapVersion;           // MMAP_VERSION = 4
    uint32 size;                  // taille des données navmesh qui suivent
    bool   usesLiquids : 1;       // 1 bit flag
};                                // suivi de size bytes de données dtNavMesh tile
```

Validation dans `loadMap()` :
```cpp
if (header.mmapMagic != MMAP_MAGIC || header.dtVersion != DT_NAVMESH_VERSION || header.mmapVersion != MMAP_VERSION)
    return false; // tile rejetée silencieusement
```
> `DT_NAVMESH_VERSION` est une constante Detour — doit être le même build que le Navigation.dll. Le `RecastBuilderDll` C++ wrapper partage les mêmes `.lib` donc ce sera automatiquement cohérent.

### Fichier `.offmesh` — Connexions hors-maillage par tile
**Nom** : `mmaps/{mapId:000}_{tileX:02}_{tileY:02}.offmesh`  
⚠ **X AVANT Y** avec underscores — inverse du `.mmtile` !  
⚠ **ADT coords** (0–63) avec 2 chiffres — **PAS les sub-tile coords** 4×4.  
Fichier optionnel : s'il n'existe pas, le tile se charge quand même sans offmesh.

**Header** :
```
uint32 magic   = 0x4D4D4F46  // 'OFFM'
uint32 version = 2           // OFFMESH_VERSION_LATEST
uint32 mapId
uint32 count
```

**Par entrée v2 — 52 bytes** :
```
float  startX, startY, startZ   (12 bytes)
float  endX,   endY,   endZ     (12 bytes)
float  radius                   ( 4 bytes)
uint16 polyIndex                ( 2 bytes)
uint8  flags     // 0=ONEWAY, 1=BIDIRECTIONAL
uint8  side
uint32 userId                   ( 4 bytes)
uint8  type      // 0=Normal,1=Elevator,2=Portal,3=InteractUnit,4=InteractObject
uint8  reserved  = 0
uint16 waitTimeMs               ( 2 bytes)
uint32 interactId               ( 4 bytes)
uint32 sourceMapId              ( 4 bytes)
uint32 targetMapId              ( 4 bytes)
// Total = 52 bytes
```

### Poly flags — HB AbilityFlags (dans les polygones navmesh)
Assignés lors du `buildMoveMapTile()` dans MapBuilder.cpp :
```csharp
// AbilityFlags bitmask — doit correspondre exactement
// Sinon PathFinder::createFilter() inclut/exclut les mauvais polys
switch (areaId) {
    case NAV_WATER:  polyFlags = 0x04; break; // AbilityFlags.Swim
    default:         polyFlags = 0x01; break; // AbilityFlags.Run
}
// → NAV_GROUND, NAV_ROAD, NAV_LAVA, NAV_FALL = tous 0x01
// → NAV_WATER uniquement = 0x04
```

### NavTerrain — Area IDs (enum MoveMapSharedDefines.h)
```csharp
NAV_EMPTY  = 0,  NAV_GROUND = 1,  NAV_WATER = 2,  NAV_LAVA = 3,
NAV_ROAD   = 4,  NAV_FALL   = 5,  NAV_ELEVATOR = 6, NAV_GATE = 7,
NAV_PORTAL = 8,  NAV_DEFENDERS_PORTAL = 9,
NAV_HORDE_PORTAL = 10, NAV_ALLIANCE_PORTAL = 11,
NAV_BLOCKED = 12, NAV_INTERACT_UNIT = 13, NAV_INTERACT_OBJECT = 14,
NAV_HORDE = 15,  NAV_ALLIANCE = 16, NAV_BLACKSPOT = 17,
NAV_KNOWN_BUILDING = 18,
NAV_MISC1=20 ... NAV_MISC10=29  // max 63 (6 bits dans dtPoly.areaAndtype)
```

---

## Règles de sécurité — À respecter absolument

1. **Ce projet crée UNIQUEMENT des fichiers dans son propre dossier `extractor-csharp/`**
2. **Ne jamais modifier, supprimer ou toucher les dossiers frères** : `mangostwo-server/`, `shared/`, `map-extractor/`, `Movemap-Generator/`, etc.
3. En lecture seule uniquement : les sources C++ dans `../mangostwo-server/` sont une référence de lecture, jamais une cible d'écriture
4. L'output de l'extracteur va dans un dossier configuré par l'utilisateur (ex: `D:/wow-data/output/`), **jamais dans le répertoire du projet**

---

## Workflow Git — Dans `extractor-csharp/`

```bash
# Initialisation (une seule fois)
cd extractor-csharp
git init
git add INSTRUCTIONS.md PROMPT.md
git commit -m "initial project setup"
```

**Stratégie de commits — style humain :**
- Commits petits et logiques, pas un gros commit final
- Messages naturels, pas de préfixes `feat:/fix:/chore:` robotiques
- Exemples de bons messages :
  ```
  "add StormLib P/Invoke bindings"
  "ADT header parsing works on map 0"
  "fix MCNK height offset calculation"
  "road texture detection handles mixed case"
  "hook up Recast bridge, first mmtile generated"
  ```
- Commiter après chaque étape fonctionnelle (parser qui compile, test qui passe, etc.)
- Un `.gitignore` dès le début : `bin/`, `obj/`, `*.user`, `.vs/`

---

## Dépendances — Principe de sobriété

- **Zéro NuGet superflu** — si .NET 8 BCL suffit, pas de package externe
- Packages autorisés (car unavoidables) :
  - `Microsoft.Extensions.Logging` (logging)
  - `System.CommandLine` (CLI)
- **Interdit** : Entity Framework, Newtonsoft.Json (utiliser `System.Text.Json`), AutoMapper, MediatR, etc.
- Pour le parsing JSON de config : `System.Text.Json` (inclus dans .NET 8)
- Pour les tests : `xunit` + `xunit.runner.visualstudio` uniquement

---

## Fichiers binaires de référence pour les tests

Sans samples, les tests unitaires des parsers ADT/DBC sont impossibles à bootstrapper. **À fournir dans `tests/data/`** :

| Fichier | Source | Utilité |
|---|---|---|
| `test_adt_0_32_32.adt` | Client WoW 12340 MPQ `World/Maps/Azeroth/Azeroth_32_32.adt` | Test parser ADT/MCNK de base |
| `test_wdt_0.wdt` | `World/Maps/Azeroth/Azeroth.wdt` | Test index tiles présentes |
| `test_Map.dbc` | `DBFilesClient/Map.dbc` | Test DbcReader générique |
| `test_0_32_32.map` | Sortie du map-extractor MaNGOS | Test lecture fichier .map généré |

Comment les extraire :
```csharp
// Dans un test d'initialisation (une seule fois) :
using var archive = MpqArchive.Open("path/to/Patch-3.MPQ");
archive.TryReadFile(@"World\Maps\Azeroth\Azeroth_32_32.adt", out var data);
File.WriteAllBytes("tests/data/test_adt_0_32_32.adt", data.ToArray());
```

> Ces fichiers ne contiennent que du terrain statique public (pas de code Blizzard). Les inclure dans le dépôt comme fixtures de test est légalement acceptable dans le contexte d'un serveur privé open-source.

---

## Phase 1 — Ce qui doit être implémenté en premier

**Ordre recommandé :**
1. `Core` — interfaces + modèles + constantes + `TileProgressEvent` + `ExtractorConfig`
2. `Formats/Mpq` — P/Invoke StormLib, `IMpqArchive`, `MpqArchive`
3. `Formats/Dbc` — `DbcReader<T>` générique
4. `Formats/Adt` — `AdtReader` (parsing MCNK, MFBO, MHDR)
5. `MapExtractor` — `MapExtractorService`, `MapFileWriter`
6. Tests unitaires sur les parsers (avec fichiers binaires de référence — voir section ci-dessus)
7. **`UI`** — `MainWindow`, `TileGridViewModel`, `MapGridControl` (WriteableBitmap)
8. `Formats/Wmo` + `Formats/M2` — parsers collision mesh
9. `VmapExtractor` — assemblage BIH/VMaps
10. **`native/RecastBuilderDll`** — thin wrapper C++ (voir section dédiée)
11. `MmapExtractor` — TerrainMeshBuilder + Recast P/Invoke + lecture `gameobject_spawns.bin`
12. `RoadExtractor` — **priorité basse** : optionnel, le serveur MaNGOS fonctionne sans

> **Note road-extractor** : les fichiers `.road` ne sont pas utilisés par le serveur MaNGOS vanilla ni par le pathfinding Detour. Ils servent à de l'optimisation de navigation bot (préférer les chemins pavés). Implémenter en dernier ou omettre.
