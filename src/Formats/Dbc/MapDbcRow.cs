using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// Map.dbc row size = 140 bytes (Map 3.3.5a).
/// Field 0 = ID, Fields 1+ are offsets into string block.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 140)]
public struct MapDbcRow
{
    public uint Id;
    public uint InternalName; // field 1: "Azeroth", "Kalimdor", etc.
    public uint MapType;      // field 2
    // remaining fields omitted (we only need Id + InternalName)
}
