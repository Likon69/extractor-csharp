using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// Reads DBC database files. Provides indexer access to rows and GetString(fieldIndex).
/// Generic over row type — use a struct with the right Size= to match the file's rowSize.
/// </summary>
public sealed class DbcReader<TRow> where TRow : unmanaged
{
    private TRow[] _rows = Array.Empty<TRow>();
    private string[] _columnNames = Array.Empty<string>();
    private byte[] _stringBlock = Array.Empty<byte>();

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

        var rows = new TRow[recordCount];
        for (uint i = 0; i < recordCount; i++)
            rows[i] = reader.Read<TRow>();

        var stringBlock = reader.ReadBytes((int)stringBlockSize);

        var instance = new DbcReader<TRow>
        {
            _rows = rows,
            _columnNames = new string[fieldCount],
            _stringBlock = stringBlock
        };

        int pos = 0;
        for (uint col = 0; col < fieldCount; col++)
        {
            string name = ReadString(stringBlock, pos);
            instance._columnNames[col] = name;
            pos += name.Length + 1;
        }

        return instance;
    }

    /// <summary>Reads string from the string block at offset stored in row[fieldIndex].</summary>
    public string GetString(TRow row, int fieldIndex)
    {
        unsafe
        {
            TRow* rowPtr = &row;
            // DBC fields are each 4 bytes — NOT Unsafe.SizeOf<TRow>() which is the full row size
            int fieldOffset = fieldIndex * sizeof(uint);
            uint stringOffset = MemoryMarshal.Read<uint>(
                new ReadOnlySpan<byte>((byte*)rowPtr + fieldOffset, sizeof(uint)));

            if (stringOffset >= _stringBlock.Length)
                return string.Empty;

            return ReadString(_stringBlock, (int)stringOffset);
        }
    }

    public TRow? FindById(uint id)
    {
        foreach (var row in _rows)
            if (GetId(row) == id) return row;
        return null;
    }

    private static uint GetId(TRow row)
    {
        unsafe
        {
            TRow* ptr = &row;
            return MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(ptr, Unsafe.SizeOf<TRow>()));
        }
    }

    private static string ReadString(Span<byte> block, int offset)
    {
        if (offset < 0 || offset >= block.Length) return string.Empty;
        int end = offset;
        while (end < block.Length && block[end] != 0) end++;
        return Encoding.UTF8.GetString(block.Slice(offset, end - offset));
    }

    public int GetInt32(TRow row, int fieldIndex)
    {
        unsafe
        {
            TRow* rowPtr = &row;
            int fieldOffset = fieldIndex * sizeof(uint);
            return MemoryMarshal.Read<int>(
                new ReadOnlySpan<byte>((byte*)rowPtr + fieldOffset, sizeof(uint)));
        }
    }

    public uint GetUInt32(TRow row, int fieldIndex)
    {
        unsafe
        {
            TRow* rowPtr = &row;
            int fieldOffset = fieldIndex * sizeof(uint);
            return MemoryMarshal.Read<uint>(
                new ReadOnlySpan<byte>((byte*)rowPtr + fieldOffset, sizeof(uint)));
        }
    }
}
