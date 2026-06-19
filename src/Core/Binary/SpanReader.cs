using System.Runtime.CompilerServices;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MaNGOS.Extractor.Core.Binary;

/// <summary>
/// Fast binary parsing utilities using Span&lt;byte&gt; and MemoryMarshal.
/// Tracks absolute position from the original buffer start.
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public SpanReader(byte[] data)
    {
        _buffer = data;
        _position = 0;
    }

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _buffer = data;
        _position = 0;
    }

    public SpanReader(ReadOnlyMemory<byte> memory) : this(memory.Span) { }

    /// <summary>Remaining bytes from current position.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>Whether all data has been consumed.</summary>
    public readonly bool EndOfData => _position >= _buffer.Length;

    /// <summary>Current absolute position from buffer start.</summary>
    public int Position => _position;

    /// <summary>Peeks the next byte without advancing.</summary>
    public readonly byte Peek() => _buffer[_position];

    /// <summary>Skips bytes forward from current position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count) => _position += count;

    /// <summary>Moves to absolute offset from buffer start.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Seek(int offset) => _position = Math.Clamp(offset, 0, _buffer.Length);

    /// <summary>Reads a single byte and advances.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte() => _buffer[_position++];

    /// <summary>Reads an unsigned 16-bit integer (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16() => ReadPrimitive<ushort>();

    /// <summary>Reads an unsigned 32-bit integer (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32() => ReadPrimitive<uint>();

    /// <summary>Reads a signed 32-bit integer (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32() => ReadPrimitive<int>();

    /// <summary>Reads a signed 16-bit integer (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16() => ReadPrimitive<short>();

    /// <summary>Reads an unsigned 64-bit integer (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64() => ReadPrimitive<ulong>();

    /// <summary>Reads a 32-bit float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFloat() => ReadPrimitive<float>();

    /// <summary>Reads a 64-bit double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble() => ReadPrimitive<double>();

    /// <summary>Reads a FourCC (uint32 interpreted as ASCII).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadFourCC() => ReadPrimitive<uint>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T ReadPrimitive<T>() where T : unmanaged
    {
        int sizeOf = Unsafe.SizeOf<T>();
        // Guard against reading past the end of the buffer (corrupt WMO/M2
        // files with truncated chunks). Without this check, MemoryMarshal.Read
        // throws ArgumentOutOfRangeException with parameter 'length'. Return
        // default(T) so the caller can detect the truncation and bail out.
        if (_position < 0 || _position > _buffer.Length - sizeOf)
            throw new EndOfStreamException(
                $"SpanReader: tried to read {sizeOf} bytes at position {_position}, buffer length {_buffer.Length}");
        T value = MemoryMarshal.Read<T>(_buffer.Slice(_position, sizeOf));
        _position += sizeOf;
        return value;
    }

    /// <summary>Reads exactly n bytes as a new array. Clamps to remaining buffer
    /// so corrupt chunk sizes don't throw ArgumentOutOfRangeException — returns
    /// whatever is actually available (callers should validate the chunk
    /// size separately if they need exact size).</summary>
    public byte[] ReadBytes(int count)
    {
        if (count < 0) count = 0;
        int available = _buffer.Length - _position;
        if (count > available) count = available;
        var result = _buffer.Slice(_position, count).ToArray();
        _position += count;
        return result;
    }

    /// <summary>Reads a null-terminated ASCII string.</summary>
    public string ReadCString(int maxLength = 256)
    {
        int start = _position;
        int end = start;

        while (end < _buffer.Length && _buffer[end] != 0 && end - start < maxLength)
            end++;

        int len = end - start;
        _position = end + 1 < _buffer.Length ? end + 1 : _buffer.Length;
        return len > 0 ? Encoding.ASCII.GetString(_buffer.Slice(start, len)) : string.Empty;
    }

    /// <summary>Reads a fixed-length ASCII string.</summary>
    public string ReadFixedString(int length)
    {
        int end = _position + length;
        while (end > _position && end <= _buffer.Length && (_buffer[end - 1] == 0 || _buffer[end - 1] == ' '))
            end--;

        int len = end - _position;
        _position += length;
        return len > 0 ? Encoding.ASCII.GetString(_buffer.Slice(_position - length, len)) : string.Empty;
    }

    /// <summary>Reads raw bytes without copying.</summary>
    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        var result = _buffer.Slice(_position, count);
        _position += count;
        return result;
    }

    /// <summary>Reads raw memory without copying.</summary>
    public ReadOnlyMemory<byte> ReadMemory(int count) => ReadSpan(count).ToArray();

    /// <summary>Reads a struct T from the current position.</summary>
    public T Read<T>() where T : unmanaged => ReadPrimitive<T>();

    /// <summary>Peeks a struct T from the current position without advancing.</summary>
    public readonly T Peek<T>() where T : unmanaged => MemoryMarshal.Read<T>(_buffer.Slice(_position));

    /// <summary>Creates a sub-reader starting at current position.</summary>
    public readonly SpanReader SubReader(int length) => new(_buffer.Slice(_position, length));

    /// <summary>Aligns to the next 4-byte boundary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Align4() => _position += (_position % 4 != 0) ? (4 - _position % 4) : 0;

    /// <summary>Aligns to the next 8-byte boundary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Align8() => _position += (_position % 8 != 0) ? (8 - _position % 8) : 0;
}