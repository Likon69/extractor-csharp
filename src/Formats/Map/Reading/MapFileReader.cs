using System.IO;
using System.Runtime.InteropServices;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Map.Reading;

/// <summary>
/// Reader for the MaNGOS .map file format produced by the map-extractor
/// (mangostwo-server/src/tools/Extractor_projects/map-extractor/System.cpp).
///
/// This is the on-disk format the mmap-extractor reads instead of re-parsing
/// the raw ADT — same as the C++ MapBuilder pipeline (MapBuilder::buildTile
/// → m_terrainBuilder->loadMap).
///
/// On-disk layout:
///
///   44-byte header:
///     uint32 mapMagic        = "MAPS"  (0x5350414D little-endian)
///     uint32 versionMagic    = "v1.5"  (WotLK; set by setMapMagicVersion)
///     uint32 buildMagic      = client build number
///     uint32 areaMapOffset
///     uint32 areaMapSize
///     uint32 heightMapOffset
///     uint32 heightMapSize
///     uint32 liquidMapOffset (0 if no liquid)
///     uint32 liquidMapSize
///     uint32 holesOffset
///     uint32 holesSize
///
///   AREA section (at areaMapOffset):
///     uint32 fourcc          = "AREA"
///     uint16 flags           (MAP_AREA_NO_AREA = 0x0001)
///     uint16 gridArea        (only if NO_AREA: the area id used for all 256 cells)
///     if not NO_AREA: 256 × uint16 areaFlags[16][16]
///
///   MHGT section (at heightMapOffset):
///     uint32 fourcc          = "MHGT" (0x4D484754 LE in some versions; the
///                                       C++ writes the fourcc via *(uint32*)
///                                       const so it follows host endianness —
///                                       on x86/x64 little-endian: 0x5447484D
///                                       → "MHGT" reading left-to-right)
///     uint32 flags           (NO_HEIGHT=0x01, AS_INT16=0x02, AS_INT8=0x04)
///     float  gridHeight      (min height)
///     float  gridMaxHeight   (max height)
///     if not NO_HEIGHT:
///       if AS_INT8:  129×129 uint8 + 128×128 uint8
///       elif AS_INT16: 129×129 uint16 + 128×128 uint16
///       else: 129×129 float + 128×128 float
///
///   MLIQ section (at liquidMapOffset, optional):
///     uint32 fourcc          = "MLIQ"
///     uint16 flags           (NO_TYPE=0x0001, NO_HEIGHT=0x0002)
///     uint16 liquidType      (if NO_TYPE: the type for all 256 cells)
///     uint8  offsetX, offsetY, width, height
///     float  liquidLevel
///     if not NO_TYPE: 256 × uint16 liquid_entry + 256 × uint8 liquid_flags
///     if not NO_HEIGHT: width × height floats
///
///   Holes section (at holesOffset):
///     256 × uint16 holes[16][16]
/// </summary>
public static class MapFileReader
{
    public const int V9Side = 129;          // 16 chunks × 8 cells + 1 shared edge per side
    public const int V8Side = 128;          // 16 chunks × 8 inner vertices
    public const int ChunksPerTileSide = 16;
    public const int TotalChunks = ChunksPerTileSide * ChunksPerTileSide;
    public const int V9FloatCount = V9Side * V9Side;
    public const int V8FloatCount = V8Side * V8Side;

    public sealed class TileData
    {
        public float[] V9Heights { get; } = new float[V9FloatCount];
        public float[] V8Heights { get; } = new float[V8FloatCount];
        public ushort[] AreaFlags { get; } = new ushort[TotalChunks];
        public ushort[] Holes { get; } = new ushort[TotalChunks];
        public uint BuildMagic { get; set; }
        public float MinHeight { get; set; }
        public float MaxHeight { get; set; }
        public bool HasHeight { get; set; }
        public LiquidData? Liquid { get; set; }
    }

    /// <summary>
    /// Liquid (water/lava/slime) data from the MLIQ section of a .map file.
    /// Mirrors MaNGOS C++ TerrainBuilder::loadMap() — the mmap-extractor uses
    /// <see cref="Flags"/> to decide NAV_WATER vs NAV_LAVA and <see cref="Heights"/>
    /// (or <see cref="LiquidLevel"/> when <see cref="HasHeightValues"/> is false)
    /// to position the liquid surface.
    ///
    /// WotLK MAP_LIQUID_TYPE_* flag values (stored in <see cref="Flags"/>):
    ///   0x00 = no water, 0x01 = Water, 0x02 = Ocean, 0x04 = Magma, 0x08 = Slime, 0x10 = DarkWater
    /// </summary>
    public sealed class LiquidData
    {
        public ushort LiquidType { get; set; }   // global type when header has NO_TYPE flag
        public byte OffsetX { get; set; }
        public byte OffsetY { get; set; }
        public byte Width { get; set; }
        public byte Height { get; set; }
        public float LiquidLevel { get; set; }
        public bool HasType { get; set; }        // !NO_TYPE
        public bool HasHeightValues { get; set; } // !NO_HEIGHT
        /// <summary>256 per-chunk flags (MAP_LIQUID_TYPE_* bitfield). Only valid when <see cref="HasType"/> is true; otherwise use <see cref="LiquidType"/> for all cells.</summary>
        public byte[] Flags { get; set; } = new byte[TotalChunks];
        /// <summary>Per-vertex heights, (Width+1)*(Height+1) floats. Only valid when <see cref="HasHeightValues"/> is true; otherwise use <see cref="LiquidLevel"/>.</summary>
        public float[]? Heights { get; set; }

        public bool HasAnyLiquid
        {
            get
            {
                if (!HasType) return LiquidType != 0;
                for (int i = 0; i < Flags.Length; i++)
                    if (Flags[i] != 0) return true;
                return false;
            }
        }
    }

    public static TileData? Read(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);
            return Read(br);
        }
        catch (Exception ex)
        {
            // Surface the real error so callers (mmap service) can log it
            // instead of silently dropping the tile. The mmap service logs
            // its own warning when Read returns null, but the inner
            // exception is what tells us WHY (bad magic, truncated MLIQ,
            // seek past EOF, etc.).
            throw new InvalidDataException($"MapFileReader.Read failed for {filePath}: {ex.Message}", ex);
        }
    }

    public static TileData? Read(BinaryReader br)
    {
        // ---- Header (44 bytes) ----
        if (br.BaseStream.Length < 44) return null;
        uint mapMagic = br.ReadUInt32();
        if (mapMagic != 0x5350414D) return null; // "MAPS" read as little-endian uint32
        uint versionMagic = br.ReadUInt32();
        if (versionMagic != MagicBytes.MapMagicWotlk)
        {
            // Non-WotLK .map (TBC/Cata) — not supported in this C# port, skip.
            return null;
        }
        uint buildMagic = br.ReadUInt32();
        uint areaMapOffset = br.ReadUInt32();
        uint areaMapSize   = br.ReadUInt32();
        uint heightMapOffset = br.ReadUInt32();
        uint heightMapSize   = br.ReadUInt32();
        uint liquidMapOffset = br.ReadUInt32();
        uint liquidMapSize   = br.ReadUInt32();
        uint holesOffset     = br.ReadUInt32();
        uint holesSize       = br.ReadUInt32();

        var data = new TileData { BuildMagic = buildMagic };

        // ---- AREA section ----
        if (areaMapOffset != 0 && areaMapSize >= 8)
        {
            br.BaseStream.Seek(areaMapOffset, SeekOrigin.Begin);
            uint fourcc = br.ReadUInt32();
            ushort flags = br.ReadUInt16();
            ushort gridArea = br.ReadUInt16();
            const ushort MAP_AREA_NO_AREA = 0x0001;
            if ((flags & MAP_AREA_NO_AREA) == 0)
            {
                for (int i = 0; i < TotalChunks; i++)
                    data.AreaFlags[i] = br.ReadUInt16();
            }
            else
            {
                for (int i = 0; i < TotalChunks; i++)
                    data.AreaFlags[i] = gridArea;
            }
        }

        // ---- MHGT section ----
        if (heightMapOffset != 0 && heightMapSize >= 16)
        {
            br.BaseStream.Seek(heightMapOffset, SeekOrigin.Begin);
            uint fourcc = br.ReadUInt32();
            uint flags = br.ReadUInt32();
            float minH = br.ReadSingle();
            float maxH = br.ReadSingle();
            data.MinHeight = minH;
            data.MaxHeight = maxH;

            const uint NO_HEIGHT  = 0x0001;
            const uint AS_INT16   = 0x0002;
            const uint AS_INT8    = 0x0004;
            data.HasHeight = (flags & NO_HEIGHT) == 0;

            if (data.HasHeight)
            {
                if ((flags & AS_INT8) != 0)
                {
                    float step = (maxH - minH) / 255f;
                    for (int i = 0; i < V9FloatCount; i++)
                        data.V9Heights[i] = minH + br.ReadByte() * step;
                    for (int i = 0; i < V8FloatCount; i++)
                        data.V8Heights[i] = minH + br.ReadByte() * step;
                }
                else if ((flags & AS_INT16) != 0)
                {
                    float step = (maxH - minH) / 65535f;
                    for (int i = 0; i < V9FloatCount; i++)
                        data.V9Heights[i] = minH + br.ReadUInt16() * step;
                    for (int i = 0; i < V8FloatCount; i++)
                        data.V8Heights[i] = minH + br.ReadUInt16() * step;
                }
                else
                {
                    for (int i = 0; i < V9FloatCount; i++)
                        data.V9Heights[i] = br.ReadSingle();
                    for (int i = 0; i < V8FloatCount; i++)
                        data.V8Heights[i] = br.ReadSingle();
                }
            }
        }

        // ---- MLIQ section (liquid / water / lava) ----
        // Mirrors MaNGOS C++ TerrainBuilder::loadMap() in Movemap-Generator/TerrainBuilder.cpp.
        // The mmap-extractor needs the per-chunk flags (to pick NAV_WATER vs NAV_LAVA) and
        // the per-vertex heights (or the global liquidLevel when NO_HEIGHT is set).
        if (liquidMapOffset != 0 && liquidMapSize >= 16)
        {
            br.BaseStream.Seek(liquidMapOffset, SeekOrigin.Begin);
            uint fourcc = br.ReadUInt32();
            ushort liqFlags = br.ReadUInt16();
            ushort liquidType = br.ReadUInt16();
            byte offX = br.ReadByte();
            byte offY = br.ReadByte();
            byte w = br.ReadByte();
            byte h = br.ReadByte();
            float level = br.ReadSingle();

            const ushort MLIQ_NO_TYPE = 0x0001;
            const ushort MLIQ_NO_HEIGHT = 0x0002;

            var liq = new LiquidData
            {
                LiquidType = liquidType,
                OffsetX = offX,
                OffsetY = offY,
                Width = w,
                Height = h,
                LiquidLevel = level,
                HasType = (liqFlags & MLIQ_NO_TYPE) == 0,
                HasHeightValues = (liqFlags & MLIQ_NO_HEIGHT) == 0,
            };

            if (liq.HasType)
            {
                // C++ System.cpp writes 256×uint16 liquid_entry THEN 256×uint8 liquid_flags.
                // The mmap only needs the flags (MAP_LIQUID_TYPE_* bitfield per chunk), so we
                // skip the entry array (the LiquidType.dbc row ID is not consumed by the mmap).
                for (int i = 0; i < TotalChunks; i++) br.ReadUInt16();
                for (int i = 0; i < TotalChunks; i++) liq.Flags[i] = br.ReadByte();
            }
            if (liq.HasHeightValues && w > 0 && h > 0)
            {
                // C++ System.cpp writes w*h floats (NOT (w+1)*(h+1)) — the compact
                // liquid_height rectangle, not the full 129×129 V9 vertex grid.
                int count = w * h;
                liq.Heights = new float[count];
                for (int i = 0; i < count; i++) liq.Heights[i] = br.ReadSingle();
            }

            data.Liquid = liq;
        }

        // ---- Holes section (we don't currently use this in the mmap service,
        // but expose it for parity with the on-disk format) ----
        if (holesOffset != 0 && holesSize >= TotalChunks * 2)
        {
            br.BaseStream.Seek(holesOffset, SeekOrigin.Begin);
            for (int i = 0; i < TotalChunks; i++)
                data.Holes[i] = br.ReadUInt16();
        }

        return data;
    }
}
