using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// Leading fields for AreaTable.dbc / LiquidType.dbc access.
/// Field 0 = ID, field 3 is read by callers for flags/sound bank.
/// Extra fields differ by client build and are intentionally omitted.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct AreaTableRow
{
    public uint Id;
    public uint AreaName;  // field 1: string block offset (not used)
    public uint Flags;    // field 2: area flags bitmask
}