# Optimisation du build mmap — pour Sonnet

## Contexte

Le build mmap prend ~1h+ pour 5744 ADT (×16 sub-tiles = 91 904 tiles Detour).
Sur portable 2 cœurs, ça chauffe fort. Le goulot principal est `MmapExtractorService.BuildNavMeshTileSync()` qui boucle 16× par ADT en faisant 16 P/Invoke vers la DLL Recast.

## Pattern actuel (extrait)

```
Parallel.ForEachAsync(5744 ADTs, MaxDOP=2)
  └─ pour chaque ADT (séquentiel) :
       └─ pour chaque sub-tile (1..16) :
            └─ RecastNative.BuildTile(p, verts, tris, ...)  ← P/Invoke
```

- **91 904 P/Invoke calls** au total (5744 × 16)
- Chaque call marshal `toute la géométrie` de l'ADT (souvent 100k+ vertices) → **16× gaspillage de bande passante ABI**
- À chaque call la DLL réinitialise : `rcAlloc*`, `rcFree*`, scratch buffers internes
- Le marshaling utilise `fixed (float* v = geo.Vertices)` à chaque sub-tile (refait la validation GC 16×)

## Pistes d'optimisation (par ordre d'impact attendu)

### 🥇 #1 — Batch BuildTile dans la DLL (gain attendu : 3-5×)

**Problème** : 16 P/Invoke par ADT, chacun refait un round-trip ABI complet.

**Solution** : modifier la DLL pour accepter un **array de sub-tiles** en un seul call. Le C# passe 1 fois la géométrie + un tableau de 16 `RecastBuildParams`. La DLL fait le partitionnement sub-tile en interne.

Côté DLL (RecastBuilderDll.cpp + .h) :
```cpp
// AVANT
bool BuildTile(const RecastBuildParams* p, ...);  // 16 calls / ADT

// APRÈS
bool BuildTileBatch(
    const RecastBuildParams* paramsArr,  // tableau de 16 params
    int batchSize,
    const float* verts, int vertCount,
    const int* tris, int triCount,
    const byte* areaIds,
    const float* offMeshConVerts, int offMeshConCount,
    ...
    byte** outDataArr, int* outSizeArr);
```

Côté C# (RecastNativeBridge.cs) :
- Ajouter `BuildTileBatch(paramsArr, batchSize, ...)`
- Service : 1 P/Invoke + 1 marshal géométrie au lieu de 16 par ADT

**Pourquoi gros gain** :
- 16× moins d'overhead ABI
- 16× moins de bande passante (géométrie marshalée 1 fois)
- DLL peut réutiliser ses scratch buffers entre les 16 sub-tiles d'une même ADT
- Pas besoin de changer la logique C# de partitionnement sub-tile — juste déplacer le `for (int slot = 0; slot < 16; slot++)` dans la DLL

### 🥈 #2 — Réutiliser les pointeurs P/Invoke entre calls (gain marginal, ~10-20%)

**Problème** : `fixed (float* v = geo.Vertices) { ... }` à chaque sub-tile → le marshaling refait la validation et le pinning GC à chaque appel.

**Solution** : allouer via `Marshal.AllocHGlobal` une fois par ADT, garder le pointeur pour les 16 sub-tiles.

```csharp
// AVANT
for (int slot = 0; slot < 16; slot++) {
    fixed (float* v = geo.Vertices)   // 16× pinning
    fixed (int* i = geo.Indices)
    ... BuildTile(...);
}

// APRÈS
IntPtr vPtr = Marshal.AllocHGlobal(geo.Vertices.Length * sizeof(float));
Marshal.Copy(geo.Vertices, 0, vPtr, geo.Vertices.Length);
for (int slot = 0; slot < 16; slot++) {
    // même vPtr réutilisé pour les 16 sub-tiles
    BuildTile(..., (float*)vPtr.ToPointer(), ...);
}
Marshal.FreeHGlobal(vPtr);
```

**Risque** : GC peut bouger la `geo.Vertices` si on l'a pas épinglé. Solution : utiliser `GCHandle.Alloc(geo.Vertices, GCHandleType.Pinned)` ou copier dans un buffer natif une fois.

### 🥉 #3 — Cache inter-ADT (gain inconnu, risqué)

**Problème** : 2 ADT côte à côte partagent 50-90% de géométrie (WMO qui chevauchent). On les retélécharge à chaque fois.

**Solution** : cacher la géométrie parsée par hash `(adtX, adtY, wmoPath)`.

**Risque** : subtil, demande un refactor de `MangosVmapGeometryLoader.cs`. Pas recommandé sans mesurer d'abord le gain réel.

### #4 — Multi-threading interne des sub-tiles

**NON recommandé** : on est déjà à MaxDOP=2 (limité par le portable). Multiplier par 16 sub-tiles en parallèle = 32 threads → sur-parallélisme, cache thrashing, contexte switches. Recast est déjà multi-thread en interne (`rcBuildTileParallel`).

### #5 — Profiling d'abord

**Avant tout** : utiliser `dotnet-trace` ou `perf` pour mesurer où le temps passe réellement. Hypothèses à confirmer :
- % du temps en P/Invoke ABI vs calcul C++ dans la DLL
- % du temps en GC (allocations temporaires)
- % du temps en I/O disque (lecture .vmtile, écriture .mmtile)

```bash
# Exemple avec dotnet-trace
dotnet trace collect --process-id <pid> --providers Microsoft-DotNETCore-SampleProfiler
# Ou plus simple : Visual Studio Profiler attaché au process extractor
```

## Optimisations simples à gain immédiat (sans toucher la DLL)

### A. Logger moins pendant le hot loop

Chaque `BuildTile DLL call: adt=(...) 964160v 1151151t` log à INFO. Sur 91 904 calls = 91 904 lignes de log.
- Passer en `LogDebug` au lieu de `LogInformation` (filtré par défaut)
- Garder `LogInformation` seulement au niveau ADT (1 par ADT = 5744 logs au lieu de 91 904)

Impact : réduit la pression I/O et CPU sur `ILogger`. Gain estimé : 5-10%.

### B. Éviter les allocations temporaires dans le hot loop

```csharp
// AVANT (à chaque sub-tile)
float[] omVerts = ... new float[...] // déjà fait hors du for slot, OK
int idx = ly * (liq.Width + 1) + lx; // OK
// mais: RecastBuildParams p = new RecastBuildParams { ... } // 16× par ADT
```

Le `RecastBuildParams` est un struct stack-allocated, donc gratuit. Mais le logging avec string interpolation alloue à chaque fois.

### C. Batch file I/O pour .mmtile

Chaque ADT écrit 1 fichier .mmtile. Si le filesystem est lent (HDD portable), on peut :
- Bufferiser plusieurs .mmtile avant flush
- Utiliser `FileStream` avec buffer > 4KB (défaut)

## Recommandation

**Étape 1** : Profiling pour confirmer où est le bottleneck (5 min).
**Étape 2** : Fix A + B + C (1-2h de travail). Gain attendu cumulé : 10-20%.
**Étape 3** : Si encore trop lent, fix #1 (Batch BuildTile dans DLL). 4-8h de travail (DLL + bridge + service). Gain attendu : 3-5×.

## Fichiers concernés (6 max pour Sonnet)

1. `src/MmapExtractor/MmapExtractorService.cs` — service principal (62 KB, hot loop + Parallel.ForEachAsync)
2. `src/MmapExtractor/Recast/RecastNativeBridge.cs` — pont P/Invoke (signature BuildTile)
3. `src/MmapExtractor/MangosVmapGeometryLoader.cs` — chargement géométrie WMO/M2
4. `native/RecastBuilderDll/RecastBuilderDll.h` — header DLL (struct RecastBuildParams)
5. `native/RecastBuilderDll/RecastBuilderDll.cpp` — code DLL (BuildTile + scratch buffers)
6. `src/Core/Constants/WowConstants.cs` — constantes SubTilesPerAdt = 4

## Questions pour Sonnet

1. Le bottleneck principal est-il le P/Invoke ou le calcul Recast lui-même ?
2. Pour le Batch BuildTile : combien de sub-tiles par batch optimal (16 = 1 ADT complet ? ou 4 par batch pour équilibrer RAM/CPU) ?
3. Faut-il préfixer `rcBuildRegionMonotone` ou `rcBuildRegions` pour profiter du multi-thread interne Recast ?
4. Le portable 2 cœurs a-t-il assez de RAM pour tenir toutes les scratch buffers d'un ADT entier en mémoire simultanément (gain du batch) ?
5. Y a-t-il un moyen de mesurer la température CPU depuis .NET (pour valider que l'optimisation réduit la chauffe) ?