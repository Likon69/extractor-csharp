# STATUS - Port MaNGOS C++ → C#

## Objectif
Port byte-pour-byte des 4 extracteurs MaNGOS (Map, Vmap, Road, Mmap).
**Seule addition autorisée** : sous-tuiles 4×4 dans mmap (.mmtile).
**Référentiel** : `C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a original\`

## Ce qui marche ✅

- **36/36 tests passent**, build propre (0 warning, 0 erreur)
- **Road** : `0004832.road` = 256 bytes, **0 diff** vs référence
- **Map** : `0002035.map` = 596 bytes, **0 diff** vs référence
- **dir_bin** : maintenant écrit correctement (82 KB pour Azeroth tile 32,48)
- **vmtree + vmtile** : générés (`000.vmtree` = 101 B, `000_32_48.vmtile` = 76 KB)
- **Buildings/** : fichiers .vmo et .vmd copiés (169 vmo, 2177 vmd pour tile 32,48)

## Ce qui reste ❌

### Bug 1 - Map section liquid (tile 32,48)
- `.map` C# = 174 224 bytes, REF = 174 740 bytes → **diff -516 bytes**
- 516 bytes = 129 floats = exactement **1 ligne × 129 colonnes** de liquid en moins
- Fichier : `src/Formats/Map/Writing/MapFileWriter.cs` lignes 105-188
- Cause probable : `for (int y2 = 0; y2 < 128; y2++)` au lieu de `< 129` dans le scan des bornes (ligne 105), ou off-by-one dans le calcul `liqH = liqMaxY - liqMinY + 2`
- Le C++ (`System.cpp:525`) a `bool liquid_show[ADT_GRID_SIZE][ADT_GRID_SIZE]` = 128×128 mais itère `cy = i*8 + y + offsetY` jusqu'à 197 pour chunk 15 → comportement indéfini côté C++ aussi, mais le résultat est plus grand
- **Fix** : changer `< 128` en `< 129` dans le scan, ou ajouter 1 à `liqH`/largeur

### Bug 2 - Pas testé
- Comparaison byte-pour-byte de `000.vmtree` (référence = 2.2 MB) — pas encore fait car le tile 32,48 n'a pas de référence
- MMAP 4×4 : pas testé (référence = 1×1, C# = 4×4)
- BIH byte-pour-byte : pas vérifié (dépend de vmtree correct)

## Commandes de test

```bash
cd "C:/Users/Texy6/Desktop/newhcb/Navigation View3D/Extractor_projects-master/extractor-csharp"
dotnet build src/MaNGOS.Extractor.csproj -c Release --nologo
dotnet test tests/MaNGOS.Extractor.Tests.csproj --nologo --no-restore

# Test map+road+vmap sur tile (32, 48) Azeroth :
rm -rf /tmp/etest
timeout 180 ./src/bin/Release/net10.0-windows/MaNGOS.Extractor.exe \
  --wow "C:/Users/Texy6/Desktop/World of Warcraft 3.3.5a original" \
  --out /tmp/etest --phases Map,Vmap,Road --maps 0 --tile 32,48

# Comparer byte-pour-byte :
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
│   │   ├── MangosTileAssembler.cs         ← TryReadMapSpawns
│   │   └── MangosVmapExtractorService.cs  ← orchestrateur vmap
│   ├── RoadExtractor/RoadExtractorService.cs
│   └── MmapExtractor/                     ← 4×4 sub-tiles (seule addition)
└── tests/                                 ← 36 tests
```

## Décisions clés

1. **MagicBytes** : constants little-endian. Disque "MVER" = `0x4D564552` bytes = `0x5245564D` uint32
2. **Chunk magics** : ADT/WDT/WMO stockent les magics **inversés** sur disque → `ReverseChunkMagic()` partout
3. **Output root** : `output/` contient `Buildings/`, `vmaps/`, `maps/`, `roadmaps/`, `mmaps/` (siblings, pas nested)
4. **dir_bin** : supprimer `if (File.Exists(dirBinPath))` avant chaque write (inverse de ce qu'il faut)
5. **4×4 mmap** : seule addition autorisée au format C++

## Prochaine étape

**Fixer le bug liquid de 516 bytes** (Bug 1) avant de tester MMAP.
Une fois OK : tester BIH byte-pour-byte sur un tile qui existe en référence (ex: 27,29).
