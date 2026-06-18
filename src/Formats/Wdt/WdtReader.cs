using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Formats.Adt.Models;

namespace MaNGOS.Extractor.Formats.Wdt;

public sealed class WdtReader
{
    private readonly IArchiveReader _archive;
    private readonly bool[] _tileExists;

    /// <summary>
    /// WMO names from the WDT-level MWMO chunk (MaNGOS wdtfile.cpp:86-101).
    /// These are the WMO root files placed in the global worldspawn tile
    /// (MaNGOS uses tileX=65, tileY=65 for them — see vmapexport.cpp).
    /// </summary>
    public string[] WorldspawnWmoNames { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// WMO placements from the WDT-level MODF chunk (MaNGOS wdtfile.cpp:104-122).
    /// Each entry is a full MODF record (64 bytes), the same layout as the
    /// ADT-level MODF entries.
    /// </summary>
    public AdtModf[] WorldspawnWmoPlacements { get; private set; } = Array.Empty<AdtModf>();

    public WdtReader(IArchiveReader archive)
    {
        _archive = archive;
        _tileExists = new bool[4096];
    }

    public async Task<bool> LoadAsync(string mapName, CancellationToken ct = default)
    {
        Array.Clear(_tileExists, 0, _tileExists.Length);
        WorldspawnWmoNames = Array.Empty<string>();
        WorldspawnWmoPlacements = Array.Empty<AdtModf>();

        string path = $"World\\Maps\\{mapName}\\{mapName}.wdt";

        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
            return false;

        return await Task.Run(() => ParseWdt(data), ct);
    }

    private bool ParseWdt(ReadOnlyMemory<byte> data)
    {
        var reader = new SpanReader(data);

        // WDT chunk layout (WotLK): MVER → MPHD → MAIN → [MWMO] → [MODF].
        // Note: WDT is the one format where chunk magics are stored in REVERSE
        // byte order on disk (MaNGOS wdtfile.cpp::flipcc) — so a MAIN chunk
        // appears as the bytes 'N','I','A','M' = 0x4D41494E in LE, and the
        // C++ flips each 4-byte magic back with flipcc() before comparing
        // against the literal "MAIN". The C# does the same inline by calling
        // ReverseChunkMagic after each ReadUInt32.
        bool foundMain = false;
        while (!reader.EndOfData && reader.Remaining >= 8)
        {
            uint chunkMagic = ReverseChunkMagic(reader.ReadUInt32());
            uint chunkSize  = reader.ReadUInt32();

            if (chunkMagic == MagicBytes.Main)
            {
                // 64×64 = 4096 entries of 8 bytes each (flags + asyncId)
                for (int i = 0; i < 4096 && reader.Remaining >= 8; i++)
                {
                    uint flags   = reader.ReadUInt32();
                    uint asyncId = reader.ReadUInt32();
                    _tileExists[i] = (flags & 1) != 0;
                }
                foundMain = true;
                continue;
            }

            if (chunkMagic == MagicBytes.Mwmo)
            {
                // Concatenated null-terminated WMO filenames, exactly the
                // same layout as the ADT-level MWMO.
                var mwmoData = reader.ReadBytes((int)chunkSize);
                var names = new List<string>();
                int pos = 0;
                while (pos < mwmoData.Length)
                {
                    int end = Array.IndexOf(mwmoData, (byte)0, pos);
                    if (end < 0) end = mwmoData.Length;
                    if (end > pos)
                    {
                        names.Add(System.Text.Encoding.ASCII.GetString(mwmoData, pos, end - pos));
                    }
                    pos = end + 1;
                }
                WorldspawnWmoNames = names.ToArray();
                continue;
            }

            if (chunkMagic == MagicBytes.Modf)
            {
                // MODF entries: 64 bytes each (same layout as ADT MODF).
                // See MaNGOS wdtfile.cpp:104-122.
                int nWmo = (int)chunkSize / 64;
                var placements = new AdtModf[nWmo];
                for (int i = 0; i < nWmo; i++)
                {
                    placements[i] = new AdtModf
                    {
                        NameId = reader.ReadUInt32(),
                        UniqueId = reader.ReadUInt32(),
                        PositionX = reader.ReadFloat(),
                        PositionY = reader.ReadFloat(),
                        PositionZ = reader.ReadFloat(),
                        RotationX = reader.ReadFloat(),
                        RotationY = reader.ReadFloat(),
                        RotationZ = reader.ReadFloat(),
                        LowerBoundsX = reader.ReadFloat(),
                        LowerBoundsY = reader.ReadFloat(),
                        LowerBoundsZ = reader.ReadFloat(),
                        UpperBoundsX = reader.ReadFloat(),
                        UpperBoundsY = reader.ReadFloat(),
                        UpperBoundsZ = reader.ReadFloat(),
                        Flags = reader.ReadUInt16(),
                        DoodadSet = reader.ReadUInt16(),
                        NameSet = reader.ReadUInt16(),
                        Scale = reader.ReadUInt16()
                    };
                }
                WorldspawnWmoPlacements = placements;
                continue;
            }

            // Skip any other chunk (MVER, MPHD, …)
            if (chunkSize > 0 && reader.Remaining >= (int)chunkSize)
                reader.Skip((int)chunkSize);
            else if (chunkSize > 0)
                break; // truncated file
        }

        return foundMain; // MAIN chunk is the only required chunk
    }

    public bool HasTile(int tileX, int tileY)
    {
        int index = tileY * 64 + tileX;
        return index >= 0 && index < 4096 && _tileExists[index];
    }

    public List<(int X, int Y)> GetExistingTiles()
    {
        var tiles = new List<(int, int)>(4096);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (HasTile(x, y))
                    tiles.Add((x, y));
            }
        }
        return tiles;
    }

    /// <summary>
    /// WDT files store chunk magics with their 4 bytes reversed (MaNGOS
    /// wdtfile.cpp::flipcc reverses them on read). This swaps the 4 bytes of
    /// <paramref name="reversed"/> back to the canonical "ABCD" order so it
    /// can be compared against the normal MagicBytes constants (MAIN, MWMO,
    /// MODF, …).
    /// </summary>
    private static uint ReverseChunkMagic(uint reversed)
    {
        return ((reversed & 0x000000FFu) << 24)
             | ((reversed & 0x0000FF00u) << 8)
             | ((reversed & 0x00FF0000u) >> 8)
             | ((reversed & 0xFF000000u) >> 24);
    }
}