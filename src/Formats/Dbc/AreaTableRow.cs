using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// AreaTable.dbc row prefix for WoW 3.3.5a.
/// Field 0 = ID, field 4 = AREA_FLAG_* bitmask, field 28 = faction group mask.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 116)]
public struct AreaTableRow
{
    public uint Id;
}

/// <summary>
/// Leading fields for LiquidType.dbc access.
/// Field 0 = ID, field 3 = SoundBank.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct LiquidTypeRow
{
    public uint Id;
}
