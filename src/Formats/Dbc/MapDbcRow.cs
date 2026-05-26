using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// Map.dbc row size = 504 bytes (WotLK 3.3.5a build 12340 — 126 fields × 4 bytes).
/// Field 0 = ID, Field 1 = InternalName (string block offset), Field 2 = MapType.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 504)]
public struct MapDbcRow
{
    public uint Id;
    public uint InternalName; // field 1: "Azeroth", "Kalimdor", etc.
    public uint MapType;      // field 2
    // remaining fields omitted (we only need Id + InternalName)
}
