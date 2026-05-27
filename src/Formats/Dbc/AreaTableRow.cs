using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// AreaTable.dbc row — minimal for area flag lookup.
/// Field 0 = ID, Field 1 = AreaName (string), Field 2 = Flags (bitmask).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 144)]
public struct AreaTableRow
{
    public uint Id;
    public uint AreaName;  // field 1: string block offset (not used)
    public uint Flags;    // field 2: area flags bitmask
}