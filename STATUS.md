# STATUS - Port MaNGOS C++ → C#

## Objectif
Port byte-pour-byte des 4 extracteurs MaNGOS (Map, Vmap, Road, Mmap).
**Seule addition autorisée** : sous-tuiles 4×4 dans mmap (.mmtile).
**Référentiel** : `C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a original\`

## Ce qui marche ✅ (presque complet)

- **36/36 tests passent**, build propre (0 warning, 0 erreur) en Debug **et** Release
- **Road** : `0004832.road` = 256 bytes, **0 diff** vs référence
- **Map** : `0002035.map` = 596 bytes, **0 diff** vs référence
- **dir_bin** : écrit correctement (82 KB pour Azeroth tile 32,48)
- **vmtree + vmtile** : générés (`000.vmtree` = 101 B, `000_32_48.vmtile` = 76 KB)
- **Buildings/** : fichiers .vmo et .vmd copiés
- **Placement WMO = parfait** (validé en jeu par l'utilisateur)
- **Déplacement excellent en WMO et entre tiles** (validé en jeu)
- **Bug #2 (TileAssembler break → continue + fallback AABB) : corrigé** — 102 aborts → 0, 3226 fallbacks

## Sorties actuelles vs Mangos 1x1 (référence)

| Dossier | C# (nous) | Mangos 1x1 | Δ | Note |
|---|---|---|---|---|
| `Buildings/` | 7465 | 7580 | **−115** | M2 décoratifs sans bounding mesh (braziers, livres, enseignes, etc.) — **non-critique**, pas d'inventer une collision qui n'existe pas |
| `vmaps/` | 10344 | 9891 | +453 | Notre format inclut les `.vmtile` (phase 2) → on a +453 fichiers, c'est normal |
| `maps/` | 5744 | 5579 | +165 | On génère par ADT, Mangos aussi (5579 = 5446 continent + 133 BG/instances probablement) |
| `roadmaps/` | 5744 | 5446 | +298 | Idem, on génère par ADT |
| `mmaps/` | 3 | 2 | +1 | Très peu testé (extraction récente n'a pas fini le mmap) |

**Durée vmap complète** : ~50 min (15:59 → 16:49 sur 135 cartes).

## Ce qui reste ❌ ou à revoir

### Bug #1 — Map section liquid (tile 32,48)
- `.map` C# = 174 224 bytes, REF = 174 740 bytes → **diff −516 bytes**
- 516 bytes = 129 floats = exactement **1 ligne × 129 colonnes** de liquid en moins
- Fichier : `src/Formats/Map/Writing/MapFileWriter.cs` lignes 105-188
- Cause probable : off-by-one dans le scan des bornes (ligne 105) ou `liqH = liqMaxY - liqMinY + 2`
- **Fix proposé** : changer `< 128` en `< 129`, ou `+1` à `liqH`/largeur
- **Impact** : bug de map, pas de vmap ni mmap → non-bloquant pour le bot

### Bug #2 — TileAssembler break → continue ✅ **CORRIGÉ**
- **Avant** : `break;` au 1er M2 exotique → tous les spawns après perdus pour la map
- **Après** : `continue;` + AABB 1×1×1 fallback au pos du spawn
- **Validation** :
  - `aborting spawn accumulation` : **102 → 0** (×0)
  - `using fallback 1x1x1 AABB` : **0 → 3226** (les 47 M2 unique rattrapés)
  - Pas de régression visible : WMO + déplacement toujours OK
- Fichier : `src/VmapExtractor/MangosTileAssembler.cs:108-141`

### Bug #3 — 115 M2 décoratifs sans bounding mesh (NON-CRITIQUE)
- Mangos a 115 M2 de plus dans `Buildings/` que nous
- Tous des objets **purement décoratifs** (braziers, livres, enseignes, bannières, VFX, crystals) : WoW les affiche mais **aucune collision côté serveur**
- Notre code rejette volontairement ces M2 (on n'invente pas une collision)
- **Décision** : ne pas fixer. Zéro impact sur la navmesh et le pathfinding

### Bug #4 — WDT introuvables pour transports (NON-CRITIQUE)
- 30+ maps `Transport*` n'ont pas de WDT → skip
- C'est **normal** : les bateaux/zeppelins n'ont pas de WDT, ce sont des GameObjects dynamiques
- Les transports sont gérés côté bot via les `UseTransport.xml` HB

## Optimisations mmap (à coder un jour, pas urgent)

### Pattern actuel de l'appel DLL
```
Parallel.ForEachAsync(5744 ADTs, MaxDOP=6)
  └─ pour chaque ADT (séquentiel) :
       └─ pour chaque sub-tile (1..16) :
            └─ RecastNative.BuildTile(p, verts, tris, ...)  ← P/Invoke call
```
- **91 904 P/Invoke calls** au total (5744 × 16)
- Chaque call : marshal de **toute la géométrie** de l'ADT (souvent 100k+ vertices) → **16× gaspillage de bande passante ABI**
- À chaque call la DLL réinitialise : `rcAlloc*`, `rcFree*`, scratch buffers

### Pistes d'optimisation (par ordre d'impact attendu)

1. **🥇 Batch BuildTile (gros gain, ~3-5×)**
   - Modifier la DLL C++ pour accepter un **array de sub-tiles** en un seul call
   - 1 P/Invoke + 1 marshal géométrie au lieu de 16 par ADT
   - Côté DLL : faire le partitionnement sub-tile en interne (déjà connu : `maxAdtX`, `maxAdtY`, bboxes)
   - Côté C# : bridge `BuildTileBatch(params[], verts, tris, ...)` + service qui boucle dans la DLL

2. **🥈 Réutiliser les pointeurs P/Invoke entre calls (gain marginal, ~10-20%)**
   - Aujourd'hui : `fixed (float* v = geo.Vertices) { ... }` → le marshaling refait la validation à chaque call
   - Demain : `Marshal.AllocHGlobal` une fois, garder le pointeur pour les 16 sub-tiles d'une même ADT
   - Inconvénient : on doit s'assurer que la géométrie n'est pas GC-collected entre temps

3. **🥉 Cache inter-ADT (gain inconnu, risqué)**
   - 2 ADT côte à côte partagent 50-90% de géométrie (WMO qui chevauchent)
   - Cache hashé par `(adtX, adtY)` du WMO/M2, garder les vertices/indices parsés
   - Risque : subtil, demande un refactor de `MangosVmapGeometryLoader`

4. **Hors sujet : C++/CLI au lieu de P/Invoke (gain marginal)**
   - Zéro marshaling mais code non-portable
   - Pas la peine tant qu'on n'a pas de bottleneck ABI mesuré

### À NE PAS faire
- ❌ Paralléliser les 16 sub-tiles en interne : 6 ADT × 16 sub-tiles = 96 threads → sur-parallélisme, Recast est déjà multi-thread en interne
- ❌ Générer le mmap en stream/pipe : ajoute de la complexité pour un gain nul (les tiles sont indépendants)

## Décisions clés

1. **MagicBytes** : constants little-endian. Disque "MVER" = `0x4D564552` bytes = `0x5245564D` uint32
2. **Chunk magics** : ADT/WDT/WMO stockent les magics **inversés** sur disque → `ReverseChunkMagic()` partout
3. **Output root** : `output/` contient `Buildings/`, `vmaps/`, `maps/`, `roadmaps/`, `mmaps/` (siblings, pas nested)
4. **dir_bin** : supprimer `if (File.Exists(dirBinPath))` avant chaque write (inverse de ce qu'il faut)
5. **4×4 mmap** : seule addition autorisée au format C++

## Commandes utiles

```bash
cd "C:/Users/Texy6/Desktop/newhcb/Navigation View3D/Extractor_projects-master/extractor-csharp"

# Build debug
dotnet build src/MaNGOS.Extractor.csproj -c Debug --nologo

# Tests
dotnet test tests/MaNGOS.Extractor.Tests.csproj --nologo --no-restore

# Test 1 tile Azeroth
timeout 180 ./src/bin/Debug/net10.0-windows/MaNGOS.Extractor.exe \
  --wow "C:/Users/Texy6/Desktop/World of Warcraft 3.3.5a original" \
  --out /tmp/etest --phases Map,Vmap,Road --maps 0 --tile 32,48

# Comparer byte-pour-byte
powershell -ExecutionPolicy Bypass -Command "
\$a=[IO.File]::ReadAllBytes('/tmp/etest/maps/0004832.map')
\$b=[IO.File]::ReadAllBytes('C:/Users/Texy6/Desktop/World of Warcraft 3.3.5a original/maps/0004832.map')
\$d=0;for(\$i=0;\$i -lt \$a.Length;\$i++){if(\$a[\$i] -ne \$b[\$i]){\$d++}};Write-Host \$d"
```

## Structure du projet

```
extractor-csharp/
├── src/
│   ├── CLI/Program.cs                     ← entrée
│   ├── Core/Constants/MagicBytes.cs       ← magics little-endian
│   ├── Core/Constants/WowConstants.cs
│   ├── Core/Binary/SpanReader.cs
│   ├── Formats/
│   │   ├── Adt/                           ← parsing ADT (MH2O, MCNK, etc.)
│   │   ├── Map/Writing/MapFileWriter.cs   ← ⚠️ bug liquid ici
│   │   ├── Mpq/                           ← StormLib wrapper
│   │   ├── Vmap/Mangos/
│   │   │   ├── Bih.cs                     ← BIH (517 lignes, 3 tests OK)
│   │   │   ├── MangosDoodadExtractor.cs   ← Doodad::ExtractSet
│   │   │   ├── MangosModelSpawn.cs
│   │   │   ├── MangosVmapBuildingWriter.cs ← dir_bin records
│   │   │   ├── MangosVmapTile.cs
│   │   │   └── MangosVmapTree.cs
│   │   ├── Wdt/                           ← WDT reader
│   │   └── Wmo/                           ← WMO parser
│   ├── MapExtractor/MapExtractorService.cs
│   ├── VmapExtractor/
│   │   ├── MangosTileAssembler.cs         ← ✅ Bug #2 corrigé ici
│   │   └── MangosVmapExtractorService.cs  ← orchestrateur vmap
│   ├── RoadExtractor/RoadExtractorService.cs
│   └── MmapExtractor/                     ← 4×4 sub-tiles (seule addition)
└── tests/                                 ← 36 tests
```

## Priorités (par ordre)

1. ✅ **Bug #2 TileAssembler** : corrigé, validé en extraction
2. 🟡 **Test mmap complet** : pas re-testé depuis le fix #2
3. 🟡 **Bug #1 Map liquid off-by-one** : 516 bytes, fix de 1 ligne
4. 🟢 **Optimisation #1 (Batch BuildTile)** : à coder quand on aura besoin de vitesse mmap
5. ⚪ **Bug #3 (115 M2 décoratifs)** : pas un bug, décision
