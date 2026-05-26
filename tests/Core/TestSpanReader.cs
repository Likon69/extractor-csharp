using MaNGOS.Extractor.Core.Binary;

namespace MaNGOS.Extractor.Tests.Core;

public class SpanReaderTests
{
    [Fact]
    public void ReadUInt32_LittleEndian()
    {
        byte[] data = new byte[] { 0x78, 0x56, 0x34, 0x12 };
        var reader = new SpanReader(data);
        Assert.Equal(0x12345678u, reader.ReadUInt32());
    }

    [Fact]
    public void ReadInt16_LittleEndian()
    {
        byte[] data = new byte[] { 0x34, 0x12 };
        var reader = new SpanReader(data);
        Assert.Equal(0x1234, reader.ReadInt16());
    }

    [Fact]
    public void ReadFloat_Single()
    {
        byte[] data = new byte[] { 0x00, 0x00, 0x80, 0x3F };
        var reader = new SpanReader(data);
        float val = reader.ReadFloat();
        Assert.Equal(1.0f, val, 0.001f);
    }

    [Fact]
    public void ReadBytes_Count()
    {
        byte[] data = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        var reader = new SpanReader(data);
        byte[] result = reader.ReadBytes(3);
        Assert.Equal(3, result.Length);
        Assert.Equal(0x11, result[0]);
        Assert.Equal(0x33, result[2]);
    }

    [Fact]
    public void Seek_Position()
    {
        byte[] data = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var reader = new SpanReader(data);
        reader.Seek(2);
        Assert.Equal(2, reader.Position);
        Assert.Equal(0x33, reader.Peek());
    }

    [Fact]
    public void Remaining_AfterRead()
    {
        byte[] data = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var reader = new SpanReader(data);
        Assert.Equal(4, reader.Remaining);
        reader.ReadUInt32();
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Skip_Advances()
    {
        byte[] data = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        var reader = new SpanReader(data);
        reader.Skip(2);
        Assert.Equal(2, reader.Position);
        Assert.Equal(0x33, reader.Peek());
    }

    [Fact]
    public void ReadCString_NullTerminated()
    {
        byte[] data = new byte[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0 };
        var reader = new SpanReader(data);
        string result = reader.ReadCString();
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void EndOfData_TrueWhenEmpty()
    {
        byte[] data = Array.Empty<byte>();
        var reader = new SpanReader(data);
        Assert.True(reader.EndOfData);
    }
}
