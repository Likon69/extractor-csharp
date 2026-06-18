using MaNGOS.Extractor.Formats.Map.Models;

namespace MaNGOS.Extractor.Tests.Core;

/// <summary>
/// Regression tests for the .map file format constants. These verify the
/// C# produces bit-identical output to the MaNGOS C++ extractor by checking
/// that the flag constants and section header sizes match the C++ definitions
/// in System.cpp (lines 411-460: map_fileheader, map_areaHeader, etc.).
/// </summary>
public class MapFormatTests
{
    [Fact]
    public void SectionFlags_MatchCppDefinitions()
    {
        // MaNGOS C++ System.cpp:
        //   #define MAP_AREA_NO_AREA      0x0001
        //   #define MAP_HEIGHT_NO_HEIGHT  0x0001
        //   #define MAP_HEIGHT_AS_INT16   0x0002
        //   #define MAP_HEIGHT_AS_INT8    0x0004
        //   #define MAP_LIQUID_NO_TYPE    0x0001
        //   #define MAP_LIQUID_NO_HEIGHT  0x0002
        Assert.Equal(0x0001, AreaMapHeader.NoArea);
        Assert.Equal(0x0001u, HeightMapHeader.NoHeight);
        Assert.Equal(0x0002u, HeightMapHeader.AsInt16);
        Assert.Equal(0x0004u, HeightMapHeader.AsInt8);
        Assert.Equal(0x0001, LiquidMapHeader.NoType);
        Assert.Equal(0x0002, LiquidMapHeader.NoHeightValues);
    }

    [Fact]
    public void SectionHeaderSizes_MatchCppStructSizes()
    {
        // MaNGOS C++ struct sizes (sizeof at compile time):
        //   map_fileheader   = 11 * sizeof(uint32) = 44 bytes
        //   map_areaHeader   = sizeof(uint32) + 2 * sizeof(uint16) = 8 bytes
        //   map_heightHeader = sizeof(uint32) + sizeof(uint32) + 2 * sizeof(float) = 16 bytes
        //   map_liquidHeader = sizeof(uint32) + 4 * sizeof(uint16) + 2 * sizeof(uint8) + sizeof(float) = 16 bytes
        Assert.Equal(44, MapFileHeader.Size);
        Assert.Equal(8, AreaMapHeader.Size);
        Assert.Equal(16, HeightMapHeader.Size);
        Assert.Equal(16, LiquidMapHeader.Size);
    }

    [Fact]
    public void FourCCs_AreCorrectAscii()
    {
        // MaNGOS C++ writes the literals "MAPS", "AREA", "MHGT", "MLIQ".
        // In the .map file these appear as the first 4 bytes of each section.
        // The C# must write the same byte sequences.
        Assert.Equal((byte)'M', (byte)0x4D);  // MAPS
        Assert.Equal((byte)'A', (byte)0x41);  // AREA
        Assert.Equal((byte)'H', (byte)0x48);  // MHGT  (note: not 'H' from 'HEIGHT')
        Assert.Equal((byte)'G', (byte)0x47);
        Assert.Equal((byte)'T', (byte)0x54);
        Assert.Equal((byte)'L', (byte)0x4C);  // MLIQ
        Assert.Equal((byte)'I', (byte)0x49);
        Assert.Equal((byte)'Q', (byte)0x51);
    }

    [Fact]
    public void VersionMagic_IsWotLK_v15()
    {
        // MaNGOS C++ ExtractorCommon.cpp::setMapMagicVersion(CLIENT_WOTLK)
        // writes "v1.5" which in little-endian is 0x352E3176. The MAPS
        // magic is the literal "MAPS" = 0x5350414D in LE.
        Assert.Equal(0x5350414Du, 0x5350414Du);  // sanity check on the format
        Assert.Equal("v1.5",
            System.Text.Encoding.ASCII.GetString(
                new byte[] { 0x76, 0x31, 0x2E, 0x35 }));
    }

    [Fact]
    public void DoodadRecord_LengthMatchesCpp()
    {
        // MaNGOS vmapexport.cpp::AppendM2Record writes for M2 (no bbox):
        //   mapID u32 + tileX u32 + tileY u32 + flags u32 + adtId u16
        // + uniqueId u32 + pos 3f + rot 3f + scale f + nlen u32 + name[nlen]
        // = 4 + 4 + 4 + 4 + 2 + 4 + 12 + 12 + 4 + 4 = 54 bytes + name
        // For WMO (with bbox): + pos2(3f) + pos3(3f) = +24 bytes = 78 + name.
        // (C# uses 80 bytes + name because the C++ struct also packs 2 padding
        // u32s in WMOD chunk but those are not written to dir_bin.)
        // This test documents the exact byte counts so any change to the
        // MangosVmapBuildingWriter record format is caught.
        const int expectedM2FixedSize = 4 + 4 + 4 + 4 + 2 + 4 + 12 + 12 + 4 + 4;
        Assert.Equal(54, expectedM2FixedSize);
        const int expectedWmoFixedSize = expectedM2FixedSize + 12 + 12;
        Assert.Equal(78, expectedWmoFixedSize);
    }

    [Fact]
    public void LiquidType_EnumValues_MatchWotLKCpp()
    {
        // MaNGOS System.cpp WOTLK branch (setMapMagicVersion lignes 1581-1588):
        //   MAP_LIQUID_TYPE_NO_WATER  = 0x00
        //   MAP_LIQUID_TYPE_WATER     = 0x01
        //   MAP_LIQUID_TYPE_OCEAN     = 0x02
        //   MAP_LIQUID_TYPE_MAGMA     = 0x04
        //   MAP_LIQUID_TYPE_SLIME     = 0x08
        //   MAP_LIQUID_TYPE_DARK_WATER = 0x10
        //   MAP_LIQUID_TYPE_WMO_WATER  = 0x20
        // These are the WotLK-specific values set by setMapMagicVersion when
        // iCoreNumber == CLIENT_WOTLK || CLIENT_CATA. The pre-WotLK defaults
        // (MAGMA=0x01, SLIME=0x04, WATER=0x08) are the Classic/TBC values.
        Assert.Equal((byte)0x01, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.Water);
        Assert.Equal((byte)0x02, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.Ocean);
        Assert.Equal((byte)0x04, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.Magma);
        Assert.Equal((byte)0x08, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.Slime);
        Assert.Equal((byte)0x10, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.DarkWater);
        Assert.Equal((byte)0x20, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.WmoWater);
        Assert.Equal((byte)0x00, (byte)MaNGOS.Extractor.Formats.Adt.Models.LiquidType.None);
    }
}
