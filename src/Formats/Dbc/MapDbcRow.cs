using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// Leading fields from Map.dbc.
/// Field 0 = ID, Field 1 = InternalName (string block offset), Field 2 = MapType.
/// Extra fields differ by client build and are intentionally omitted.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct MapDbcRow
{
    public uint Id;
    public uint InternalName; // field 1: "Azeroth", "Kalimdor", etc.
    public uint MapType;      // field 2
    // remaining fields omitted (we only need Id + InternalName)
}
