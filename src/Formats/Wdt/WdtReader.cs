using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;

namespace MaNGOS.Extractor.Formats.Wdt;

public sealed class WdtReader
{
    private readonly IArchiveReader _archive;
    private readonly bool[] _tileExists;

    public WdtReader(IArchiveReader archive)
    {
        _archive = archive;
        _tileExists = new bool[4096];
    }

    public async Task<bool> LoadAsync(string mapName, CancellationToken ct = default)
    {
        string path = $"World\\Maps\\{mapName}\\{mapName}.wdt";

        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
            return false;

        return await Task.Run(() => ParseWdt(data), ct);
    }

    private bool ParseWdt(ReadOnlyMemory<byte> data)
    {
        var reader = new SpanReader(data);

        // WDT chunk layout (WotLK): MVER → MPHD → MAIN → [MWMO] → [MODF]
        // Parse in a generic chunk loop: read magic+size, handle known chunks.
        while (!reader.EndOfData && reader.Remaining >= 8)
        {
            uint chunkMagic = reader.ReadUInt32();
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
                return true;
            }

            // Skip any other chunk (MVER, MPHD, MWMO, MODF, …)
            if (chunkSize > 0 && reader.Remaining >= (int)chunkSize)
                reader.Skip((int)chunkSize);
            else if (chunkSize > 0)
                break; // truncated file
        }

        return false; // MAIN chunk not found
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
}