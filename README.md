# MaNGOS Unified Extractor

C# (.NET 8) implementation of MaNGOS data extractors for WoW WotLK 3.3.5a (build 12340).

## Extractors

| Phase | Output | Description |
|-------|--------|-------------|
| **Map** | `{mapId:000}{x:00}{y:00}.map` | 17x17 heightmap, area IDs, liquids |
| **Vmap** | `vmap{mapId:000}{x:00}{y:00}.vmo` | WMO/M2 visibility data |
| **Road** | `{mapId:000}{x:00}{y:00}.road` | 256 bytes road flags |
| **Mmap** | `{mapId}/` + `.mmtile` | 4x4 sub-tile Recast navmeshes |

## Quick Start

```batch
# Full build (C# + native DLL)
build.bat

# Or step by step:
dotnet build MaNGOS.Extractor.sln -c Release
native\build-dll.bat
```

## Project Structure

```
extractor-csharp/
├── src/                        # Main C# application
│   ├── Core/                   # Constants, Binary I/O, Models
│   ├── Formats/                # ADT, DBC, MPQ, VMAP, WMO, WDT
│   ├── MapExtractor/           # Terrain extraction
│   ├── MmapExtractor/          # Navmesh extraction (Recast)
│   ├── RoadExtractor/          # Road detection
│   ├── VmapExtractor/          # VMAP extraction
│   └── UI/                     # WPF UI
├── native/
│   └── RecastBuilderDll/       # C++ Recast wrapper (requires MSVC)
├── tests/                      # Unit tests (xUnit)
└── build.bat                   # Build script
```

## Requirements

- .NET 8 SDK
- Visual Studio 2022 with C++ workload (for native DLL only)
- WoW 3.3.5a client data files

## Configuration

Edit `ExtractorConfig.json` or use the built-in UI to set:
- `WowClientPath`: Path to WoW installation
- `OutputPath`: Extraction output directory
- `Maps`: Array of map IDs to process
- `Phases`: Array of extraction phases to run

## Testing

```bash
dotnet test MaNGOS.Extractor.sln
```

## Native DLL Build

The `RecastBuilderDll.dll` wraps Recast 1.6.0 + Detour for MMAP generation:

```batch
cd native\RecastBuilderDll
msbuild RecastBuilderDll.vcxproj /p:Configuration=Release /p:Platform=x64
```

Output: `native\RecastBuilderDll\bin\Release\RecastBuilderDll.dll`

## Architecture Notes

- **MPQ handling**: StormLib.dll via P/Invoke
- **Recast navmesh**: Native C++ DLL called from C#
- **Tile processing**: 9 ADTs loaded per tile (center + 8 neighbors)
- **Sub-tiles**: 4x4 MMAP per ADT tile = 16 sub-tiles total
- **Format**: MaNGOS Navigation.dll compatible

## License

GPL-2.0 (same as MaNGOS)