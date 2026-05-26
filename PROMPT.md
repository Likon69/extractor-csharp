# PROMPT — MaNGOS Unified Extractor C# (à coller dans MiniMax / GLM)

---

## CONTEXTE DU PROJET

Je porte en C# les 4 extracteurs C++ du serveur MaNGOS (World of Warcraft 3.3.5a / WotLK build 12340).  
Ces extracteurs lisent les fichiers du client WoW (formats MPQ, ADT, WDT, DBC, WMO, M2) et produisent les fichiers de données nécessaires au serveur : cartes de terrain (`.map`), visibility maps (vmaps), et navigation meshes (`.mmtile`).

**Je veux du code professionnel de niveau production, style ApocDev.** Apoc est le créateur d'Honorbuddy (le bot WoW C# de référence, 2010-2016). Son code est reconnaissable : types minuscules et précis, zéro verbosité, zéro commentaire évident, architecture sans surprise. Pas de prototypes, pas de TODO, pas de `Console.WriteLine` dans la logique métier.

---

## STACK TECHNIQUE OBLIGATOIRE

- .NET 8 / C# 12
- `<Nullable>enable</Nullable>` activé
- `System.Numerics.Vector3` (math)
- `Span<byte>` / `MemoryMarshal` pour le parsing binaire (PAS System.IO.Pipelines — inadapté aux fichiers)
- `Microsoft.Extensions.Logging.ILogger<T>` pour les logs
- P/Invoke pour StormLib.dll (lecture MPQ) et RecastDetour.dll (navmesh)
- **Aucune dépendance NuGet externe sauf celles citées** — si .NET 8 BCL suffit, pas de package
- Pour JSON config : `System.Text.Json` (inclus, pas Newtonsoft)
- Pour les tests : `xunit` uniquement
- *(pas de System.CommandLine — l'UI WPF est le point d'entrée principal)*

---

## RÈGLES DE CODE NON NÉGOCIABLES

1. Aucun `static` mutable — tout par injection ou paramètre
2. Aucun magic number — constantes nommées dans `WowConstants` / `RecastConstants`
3. `readonly struct` pour les types de données géométriques immuables
4. `Span<byte>` / `MemoryMarshal` pour le parsing — pas de `BinaryReader` dans les hot paths
5. Interfaces pour tout I/O : `IArchiveReader`, `IAdtReader`, `IMapFileWriter`
6. `CancellationToken ct` dans toutes les méthodes async
7. `sealed` sur toutes les classes concrètes sauf héritage justifié
8. `record` pour les configs immuables passées entre services
9. Pas de `catch (Exception)` générique — exceptions typées et loggées
10. XML doc sur toutes les interfaces et méthodes publiques

---

## RÈGLE DE SÉCURITÉ ABSOLUE

**Ce projet écrit UNIQUEMENT dans son propre dossier.** Le code généré ne doit JAMAIS :
- Modifier, supprimer ou toucher les dossiers frères (`mangostwo-server/`, `shared/`, etc.)
- Écrire des fichiers output dans le répertoire du projet (output = dossier configuré par l'utilisateur)
- Appeler `Directory.Delete`, `File.Delete` sur des chemins hors du dossier de travail configuré

---

## WORKFLOW GIT — À INTÉGRER DÈS LE PREMIER FICHIER

Après chaque étape fonctionnelle, commiter avec un message naturel :
```
"add StormLib P/Invoke bindings"
"ADT MHDR/MCIN parsing complete"
"fix MCNK vertex offset for WotLK"
"road texture detection working on map 0"
```
Pas de préfixes robotiques `feat:` / `chore:` / `refactor:`. Messages courts, anglais, clairs.

---

## TÂCHE — PHASE 1 : COUCHE CORE + MPQ

Génère les fichiers C# suivants, complets et compilables.

### Fichier 1 : `src/Core/Constants/WowConstants.cs`

Constantes du client WoW 3.3.5a :
- `TileSize = 533.333333f` (taille d'un ADT en unités monde)
- `ChunksPerTile = 16` (16×16 = 256 MCNK chunks par ADT)
- `ChunkSize = TileSize / ChunksPerTile` (≈ 33.333f)
- `MapHalfSize = 17066.666f` (32 × TileSize = demi-largeur monde)
- `GridSize = 64` (grille 64×64 d'ADTs par carte)
- `TargetBuild = 12340u` (WotLK 3.3.5a)
- Tableau `uint[] SupportedBuilds` : `{ 5875, 6005, 6141, 8606, 12340, 15595, 18414 }` (Classic/TBC/WotLK/Cata/MoP)

### Fichier 2 : `src/Core/Constants/MagicBytes.cs`

Magic bytes des formats de fichiers — valeurs réelles extraites de `ExtractorCommon.cpp` :
- `MapMagicWotlk = "v1.5"` (WotLK 3.3.5a — CLIENT_WOTLK)
- `MapMagicTbc = "s1.5"`, `MapMagicClassic = "z1.5"` (pour référence)
- `VMapMagicWotlk = "VMAPt07"` (WotLK)
- `MMapMagicWotlk = "t06"` (WotLK)
- Chunk IDs ADT (little-endian uint32 — pattern = FourCC ASCII lu big-endian, car WoW stocke les FourCC **reversés** en fichier) :
  - `Mver = 0x4D564552` ('MVER'), `Mhdr = 0x4D484452` ('MHDR')
  - `Mcin = 0x4D43494E` ('MCIN'), `Mcnk = 0x4D434E4B` ('MCNK')
  - `Mtex = 0x4D544558` ('MTEX'), `Mcly = 0x4D434C59` ('MCLY')
  - `Mfbo = 0x4D46424F` ('MFBO'), `Mh2o = 0x4D48324F` ('MH2O')
- Chunk IDs WMO : `Mohd = 0x4D4F4844`, `Mogi = 0x4D4F4749`, `Modn = 0x4D4F444E`, `Modd = 0x4D4F4444`, `Mods = 0x4D4F4453`

### Fichier 3 : `src/Core/Models/WowCoordinates.cs`

```csharp
// Conversion coordonnées WoW ↔ indices de tuiles ↔ Recast
// WoW : X=Est/Ouest, Y=Altitude, Z=Nord/Sud
// Tuile (tileX, tileY) → origine monde en X,Z
public static class CoordConverter
{
    // Retourne l'origine monde (X, Z) d'une tuile
    public static (float X, float Z) TileToWorld(int tileX, int tileY);
    // Retourne les indices de tuile depuis des coordonnées monde
    public static (int TileX, int TileY) WorldToTile(float x, float z);
}
```

### Fichier 4 : `src/Core/Interfaces/IArchiveReader.cs`

```csharp
public interface IArchiveReader : IDisposable
{
    bool FileExists(string path);
    bool TryReadFile(string path, out ReadOnlyMemory<byte> data);
    IEnumerable<string> ListFiles(string pattern);
}
```

### Fichier 5 : `src/Formats/Mpq/StormLib.cs`

P/Invoke complet pour StormLib (Windows x64) :
- `SFileOpenArchive` → `OpenArchive(string path, uint priority, uint flags, out IntPtr handle)`
- `SFileOpenFileEx` → `OpenFile(IntPtr archive, string name, uint scope, out IntPtr fileHandle)`
- `SFileGetFileSize` → `GetFileSize(IntPtr file, out uint fileSizeHigh) : uint`
- `SFileReadFile` → `ReadFile(IntPtr file, void* buffer, uint toRead, out uint read, IntPtr ov) : bool`
- `SFileCloseFile` → `CloseFile(IntPtr file) : bool`
- `SFileCloseArchive` → `CloseArchive(IntPtr handle) : bool`
- `SFileFindFirstFile` → pour énumération
- `SFileFindNextFile`
- `SFileFindClose`
- Constantes : `OpenArchiveFlags`, `OpenFileFlags` (MPQ_OPEN_NO_LISTFILE etc.)

### Fichier 6 : `src/Formats/Mpq/MpqArchive.cs`

Implémentation de `IArchiveReader` au-dessus de `StormLib` :
- `sealed class MpqArchive : IArchiveReader`
- Constructeur prend `string path, ILogger<MpqArchive> logger`
- `TryReadFile` : alloue via `ArrayPool<byte>`, retourne `ReadOnlyMemory<byte>`
- `ListFiles` : énumère via `SFileFindFirstFile` / `SFileFindNextFile`
- Gestion correcte du `SafeHandle` ou `IntPtr` avec `Dispose`
- Lance `MpqException` (custom) si archive invalide

### Fichier 7 : `src/Formats/Mpq/MpqArchiveCollection.cs`

```csharp
// Ouvre plusieurs MPQs dans l'ordre de priorité WoW et cherche dans tous
// Priorité : locale (enUS) > expansion > base > base-{locale}
public sealed class MpqArchiveCollection : IArchiveReader
{
    // Lit la liste des MPQ depuis le répertoire WoW dans l'ordre correct
    public static MpqArchiveCollection FromWoWDirectory(string wowDir, string locale, ILoggerFactory loggerFactory);
}
```

---

## TÂCHE — PHASE 2 : PARSER ADT (à demander dans la session suivante)

Dans la session suivante, je demanderai le parsing complet du format ADT :
- Chunk `MHDR` (header offsets)
- Chunk `MCIN` (256 entrées MCNK)
- Chunk `MCNK` (145 vertices hauteur, 8 couches texture, données liquide)
- Chunk `MH2O` (WotLK water système)
- Chunk `MFBO` (flight bounds)
- Sortie : `AdtFile` avec `HeightMap[256][9*9+8*8]`, `AreaId[256]`, `LiquidData[256]`

---

## FORMAT DES RÉPONSES ATTENDU

Pour chaque fichier :
1. Chemin du fichier en commentaire en tête
2. Namespace correct (`MaNGOS.Extractor.Core.Constants`, etc.)
3. Code complet, compilable, pas de `// TODO`
4. XML doc `<summary>` sur les types et méthodes publiques — **une seule ligne, factuelle**
5. Aucune référence à des DLLs inexistantes (sauf StormLib et RecastDetour)

---

## STYLE APOCDEV — RÈGLES DE CODE

**Commentaires** : un commentaire n'existe que s'il dit quelque chose que le code ne peut pas dire seul.

```csharp
// ❌ Interdit — commentaire inutile (le code parle de lui-même)
var count = 0; // initialise le compteur
result.Clear(); // efface les résultats
return null; // retourne null

// ❌ Interdit — commentaire de navigation / section
// ============ PRIVATE METHODS ============

// ❌ Interdit — commentaire qui répète la signature
// Lit le fichier depuis l'archive
public bool TryReadFile(string path, out ReadOnlyMemory<byte> data)

// ✅ Autorisé — explique un comportement non-évident
// WoW stores FourCCs reversed in file; read as uint32 LE to get ASCII big-endian value.
constexpr uint32_t MCNK = 0x4D434E4B;

// ✅ Autorisé — documente une contrainte externe
// 4×4 sub-tiling per ADT: tileWidth = 133.333f. VertexPerSubTile must divide 440 (= 1760/4).
const int VertexPerSubTile = 40;  // 440/40 = 11 Recast internal tiles per Detour tile

// ✅ Autorisé — avertissement crité
// Y before X in filename, 3 digits — sub-tile coords 0–255, matches MoveMap.cpp getTileName().
var path = $"mmaps/{mapId:000}{subTileY:03}{subTileX:03}.mmtile";
```

**Style général** :
- Types et méthodes : **noms précis**, pas de préfixes hongrois, pas de suffixes Manager/Helper/Util inutiles
- `readonly struct` pour les données immuables — pas de classe à la place
- `sealed` partout sauf héritage justifié
- `private` par défaut — n'exposer que ce qui doit l'être
- Pas de méthode de plus de ~35 lignes — découper proprement
- Pas de constructeur de plus de 3 lignes d’assignation
- Les `switch` sur des enums/constantes : expressions `switch` C# 8, pas de `if-else` en cascade

---

## EXEMPLE DE TON ET QUALITÉ ATTENDUS

```csharp
// src/Core/Constants/WowConstants.cs
namespace MaNGOS.Extractor.Core.Constants;

/// <summary>
/// World of Warcraft 3.3.5a (build 12340) client constants.
/// All spatial values are in WoW world units.
/// </summary>
public static class WowConstants
{
    /// <summary>Size of one ADT tile in world units.</summary>
    public const float TileSize = 533.333333f;

    /// <summary>Number of MCNK chunks per ADT row (and column).</summary>
    public const int ChunksPerTile = 16;

    /// <summary>Size of one MCNK chunk in world units.</summary>
    public const float ChunkSize = TileSize / ChunksPerTile; // 33.333f

    // ... etc
}
```

---

## CE QU'IL NE FAUT PAS FAIRE

- ❌ `Console.WriteLine(...)` dans les services
- ❌ `Thread.Sleep(...)`
- ❌ Classes avec plus de 300 lignes non justifiées
- ❌ Méthodes avec plus de 40 lignes
- ❌ Paramètres `object` non typés
- ❌ `dynamic`
- ❌ `GC.Collect()`
- ❌ `catch(Exception ex) { }` sans re-throw ou log structuré
- ❌ Importer `System.Drawing` (utiliser `System.Numerics` pour les coords)
- ❌ Code WinForms (ListBox.Items.Add, etc.) — c'est un projet WPF + MVVM
- ❌ Mutater l'état UI depuis un thread non-UI sans `Dispatcher.InvokeAsync`
- ❌ Commentaires évidents qui répètent ce que le code dit déjà
- ❌ Blocs de commentaires `// === Section ===` de navigation
- ❌ XML doc vide ou générique (`<summary>Does the thing.</summary>`)
- ❌ Abréviations ambigües (`mgr`, `tmp`, `val`, `obj`) — nommer ce que c'est réellement

---

## RÉFÉRENCE — STRUCTURE FICHIER .map MaNGOS WotLK

Structure réelle extraite de `MangosMap.h` (source : `mangostwo-server/src/shared/MangosMap.h`) :

```
GridMapFileHeader (44 bytes = 11 × uint32) :
  uint32 mapMagic        // "v1.5" WotLK (4 ASCII bytes, CLIENT_WOTLK)
  uint32 versionMagic    // version interne
  uint32 buildMagic      // 12340
  uint32 areaMapOffset   // offset vers la section AreaMap
  uint32 areaMapSize
  uint32 heightMapOffset // offset vers GridMapHeightHeader
  uint32 heightMapSize
  uint32 liquidMapOffset // offset vers GridMapLiquidHeader (0 si absent)
  uint32 liquidMapSize
  uint32 holesOffset     // offset vers la bitmask des trous
  uint32 holesSize

GridMapHeightHeader (16 bytes) :
  uint32 fourcc          // FourCC de la section hauteurs
  uint32 flags           // MAP_HEIGHT_NO_HEIGHT=0x1, MAP_HEIGHT_AS_INT16=0x2, MAP_HEIGHT_AS_INT8=0x4
  float  gridHeight      // altitude minimale du tile (base)
  float  gridMaxHeight   // altitude maximale
  // suivi de float[9*9 + 8*8] = 145 floats par MCNK (ou int16/int8 selon flags)

GridMapLiquidHeader (16 bytes) :
  uint32 fourcc
  uint16 flags           // MAP_LIQUID_NO_TYPE=0x1, MAP_LIQUID_NO_HEIGHT=0x2
  uint16 liquidType      // WATER=0x1, OCEAN=0x2, MAGMA=0x4, SLIME=0x8
  uint8  offsetX         // décalage X dans la grille 16×16
  uint8  offsetY
  uint8  width           // largeur de la zone liquide
  uint8  height
  float  liquidLevel     // niveau d'eau
  // suivi de float[width*height] niveaux si MAP_LIQUID_NO_HEIGHT non set
```

---

## RÉFÉRENCE — FORMAT FICHIERS MMAP OUTPUT (compatibilité Navigation.dll)

> Ces formats sont lus par `MoveMap.cpp` (Navigation.dll C++). Toute valeur incorrecte = tile silencieusement rejetée ou crash.

### `.mmap` — Paramètres navmesh par carte
**Nom** : `mmaps/{mapId:000}.mmap`  
**Contenu** : raw `dtNavMeshParams` struct — **zéro magic, zéro header supplémentaire**.

```cpp
dtNavMeshParams {
    float orig[3];    // bmin de la tile (tileXMax, tileYMax)
    float tileWidth;  // 133.333333f  ← GRID_SIZE/4 — architecture 4×4 HB-style
    float tileHeight; // 133.333333f  ← GRID_SIZE/4
    int   maxTiles;   // numAdts * 16  (16 sous-tiles par ADT)
    int   maxPolys;   // 1 << DT_POLY_BITS
}
```

### `.mmtile` — Tile navmesh individuelle
**Nom** : `mmaps/{mapId:000}{subTileY:03}{subTileX:03}.mmtile`  
⚠ **Y AVANT X**, **3 chiffres** (0–255) — car chaque ADT génère 4×4 = 16 sous-tiles.  
`subTileX = adtX * 4 + dx` (dx ∈ 0–3), `subTileY = adtY * 4 + dy` (dy ∈ 0–3)

```cpp
struct MmapTileHeader {   // 17 bytes, puis navData de header.size bytes
    uint32 mmapMagic;     // 0x4d4d4150 = 'MMAP'
    uint32 dtVersion;     // DT_NAVMESH_VERSION (= 7, Detour 1.6.0)
    uint32 mmapVersion;   // 4 = MMAP_VERSION
    uint32 size;          // taille navData en bytes
    bool   usesLiquids:1; // 1 bit
};
// Validation : si magic | dtVersion | mmapVersion ≠ attendu → tile rejetée
```

### `.offmesh` — Connexions hors-maillage par tile  
**Nom** : `mmaps/{mapId:000}_{tileX:02}_{tileY:02}.offmesh`  
⚠ **X AVANT Y** avec underscores — **inverse du `.mmtile`** !  
⚠ **ADT coords** (0–63, 2 chiffres) — **PAS** les sub-tile coords 4×4.  
Fichier optionnel (pas d'erreur si absent).

```
Header (16 bytes) :
  uint32  magic   = 0x4D4D4F46  // 'OFFM'
  uint32  version = 2           // OFFMESH_VERSION_LATEST
  uint32  mapId
  uint32  count

Par entrée v2 — 52 bytes :
  float  startX, startY, startZ   (12)
  float  endX,   endY,   endZ     (12)
  float  radius                   ( 4)
  uint16 polyIndex                ( 2)
  uint8  flags    // 0=ONEWAY  1=BIDIRECTIONAL
  uint8  side     // 0
  uint32 userId                   ( 4)
  uint8  type     // 0=Normal 1=Elevator 2=Portal 3=InteractUnit 4=InteractObject
  uint8  reserved = 0
  uint16 waitTimeMs               ( 2)
  uint32 interactId               ( 4)
  uint32 sourceMapId              ( 4)
  uint32 targetMapId              ( 4)
```

### Poly flags — HB AbilityFlags bitmask
```csharp
// Assignés dans buildMoveMapTile() — PathFinder::createFilter() les filtre
// NAV_WATER  → polyFlags = 0x04  (AbilityFlags.Swim)
// tout autre → polyFlags = 0x01  (AbilityFlags.Run)
// (NAV_GROUND, NAV_ROAD, NAV_LAVA, NAV_FALL = tous 0x01)
```

### NavTerrain — Area IDs (enum MoveMapSharedDefines.h)
```
NAV_EMPTY=0  NAV_GROUND=1  NAV_WATER=2  NAV_LAVA=3  NAV_ROAD=4
NAV_FALL=5   NAV_ELEVATOR=6  NAV_GATE=7  NAV_PORTAL=8
NAV_INTERACT_UNIT=13  NAV_INTERACT_OBJECT=14  NAV_BLOCKED=12
NAV_HORDE=15  NAV_ALLIANCE=16  NAV_BLACKSPOT=17  (max=63, 6 bits)
```

### Paramètres Recast validés — MapBuilder.cpp (bigBaseUnit=false)
```
cs = 0.303030f    ch = 0.2f    slopeAngle = 50.0°
walkableHeight = 11 cells    walkableRadius = 2 cells    walkableClimb = 5 cells
minRegionArea = 400    mergeRegionArea = 1600    maxSimplificationError = 1.3f
detailSampleDist = cs×16    detailSampleMaxError = ch×1

dtNavMeshCreateParams (world units) :
  params.walkableHeight = 2.22155f   // HB exact (11×0.2018)
  params.walkableRadius = 0.400f     // HB header value
  params.walkableClimb  = 1.0f       // HB exact (5×0.2)

Overrides par carte :
  Map 571 (Northrend/Dalaran)  → cs=0.2424f, borderSize=walkableRadius+5
  Map 562 (Blade's Edge Arena) → walkableRadius=0
  Map  48 (Blackfathom Deeps)  → ch×=2
```

### Architecture TerrainMeshBuilder — boucle 4×4 par ADT

Confirmé par `TerrainBuilder.cpp` lignes 110–119 : **9 ADTs chargés** par tile build (centre + 8 voisins cardinaux/diagonaux). La géométrie des voisins est indispensable pour que les bords de sous-tiles se connectent sans gap.

**Étape 1 — charger la géométrie une fois par ADT :**
```
loadMap(adtX, adtY)   // charge automatiquement ±1 X, ±1 Y, diagonales = 9 ADTs
loadVMap(adtX, adtY)
loadGameObjects(adtX, adtY)
```

**Étape 2 — pour chaque sous-tile (dx ∈ 0–3, dy ∈ 0–3), 16× Recast :**
```
subBmin.x = adtOriginX + dx * 133.333f
subBmin.z = adtOriginZ + dy * 133.333f
subBmax.x = subBmin.x + 133.333f
subBmax.z = subBmin.z + 133.333f

// Recast rcConfig pour cette sous-tile :
tileCfg.bmin = { subBmin.x - borderSize*cs, globalBmin.y, subBmin.z - borderSize*cs }
tileCfg.bmax = { subBmax.x + borderSize*cs, globalBmax.y, subBmax.z + borderSize*cs }
tileCfg.width  = VERTEX_PER_SUB_TILE + borderSize * 2  // 440 + padding
tileCfg.height = VERTEX_PER_SUB_TILE + borderSize * 2

// Passer TOUTE la géométrie des 9 ADTs — rcHeightfield ignore les triangles hors bounds.
// Pas de clipping manuel : le filtrage est implicite via rcRasterizeTriangles.
rcBuildHeightfield(ctx, *hf, tileCfg, ...)
```

**Sortie :** écrire 16 fichiers `{mapId:000}{subTileY:03}{subTileX:03}.mmtile`  
où `subTileX = adtX*4 + dx`, `subTileY = adtY*4 + dy`

---

## SESSION A — QUESTION À POSER EN PREMIER (fichiers 1–4, zéro P/Invoke)

"Génère les fichiers 1 à 4 de la Phase 1 listados ci-dessus, dans l'ordre, complets et compilables. Respecte exactement les namespaces, les conventions de nommage et les règles énoncées. Ne génère pas de fichier .sln ou .csproj pour l'instant."

> Valider la qualité avant de continuer : si `MemoryMarshal.AsRef<T>`, `ArrayPool<byte>` et `SafeHandle` sont utilisés correctement, le modèle est fiable. Sinon, changer de modèle.

---

## SESSION B — QUESTION À POSER ENSUITE (fichiers 5–7, P/Invoke dangereux)

"Génère maintenant les fichiers 5, 6 et 7 de la Phase 1. Code P/Invoke complet pour StormLib.dll (Windows x64). `MpqArchive` utilise `SafeHandle` et `ArrayPool<byte>`. `MpqArchiveCollection` détect automatiquement les MPQ WoW dans l'ordre correct. Respecte toutes les règles précédentes."

> **Test de validation** : demande d'abord juste `StormLib.cs` seul avec `SFileFindFirstFile` et la struct `SFILE_FIND_DATA`. Si la struct est correcte et les EntryPoints exacts, la DLL est bien connue du modèle.
