using System.Runtime.InteropServices;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Dbc;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GameObjectDisplayInfoRow
{
    public uint Id;
    public uint ModelNameOffset;
    // We omit the rest of the fields since DbcReader<TRow> safely ignores trailing data.
}
