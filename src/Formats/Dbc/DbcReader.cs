using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Dbc;

public sealed class DbcReader<TRow> where TRow : unmanaged
{
    private TRow[] _rows = Array.Empty<TRow>();
    private string[] _columnNames = Array.Empty<string>();

    public int RowCount => _rows.Length;
    public int ColumnCount => _columnNames.Length;
    public ReadOnlySpan<TRow> Rows => _rows;
    public ReadOnlySpan<string> ColumnNames => _columnNames;

    public static DbcReader<TRow> Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);

        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.DbcMagic)
            throw new InvalidDataException($"Invalid DBC magic: 0x{magic:X}");

        uint recordCount = reader.ReadUInt32();
        uint fieldCount = reader.ReadUInt32();
        uint rowSize = reader.ReadUInt32();
        uint stringBlockSize = reader.ReadUInt32();

        int expectedRowSize = Unsafe.SizeOf<TRow>();
        if (rowSize != expectedRowSize)
            throw new InvalidDataException($"Row size mismatch: expected {expectedRowSize}, got {rowSize}");

        int dataSize = (int)(recordCount * rowSize);
        var rows = new TRow[recordCount];
        for (uint i = 0; i < recordCount; i++)
        {
            rows[i] = reader.Read<TRow>();
        }

        var stringBlock = reader.ReadSpan((int)stringBlockSize);

        var reader2 = new DbcReader<TRow>
        {
            _rows = rows,
            _columnNames = new string[fieldCount]
        };

        int pos = 0;
        for (uint col = 0; col < fieldCount; col++)
        {
            string name = ReadString(stringBlock, pos);
            reader2._columnNames[col] = name;
            pos += name.Length + 1;
        }

        return reader2;
    }

    public TRow? FindById(uint id)
    {
        foreach (var row in _rows)
        {
            if (GetId(row) == id)
                return row;
        }
        return null;
    }

    private static uint GetId(TRow row)
    {
        unsafe
        {
            TRow* ptr = &row;
            var bytes = new ReadOnlySpan<byte>(ptr, Unsafe.SizeOf<TRow>());
            return MemoryMarshal.Read<uint>(bytes);
        }
    }

    private static string ReadString(ReadOnlySpan<byte> block, int offset)
    {
        if (offset >= block.Length)
            return string.Empty;

        int end = offset;
        while (end < block.Length && block[end] != 0)
            end++;

        return Encoding.UTF8.GetString(block.Slice(offset, end - offset));
    }
}
