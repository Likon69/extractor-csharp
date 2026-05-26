using System.IO;
using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Core.Models;

[StructLayout(LayoutKind.Sequential)]
public struct GoSpawn
{
    public uint MapId;
    public uint DisplayId;
    public float PosX;
    public float PosY;
    public float PosZ;
    public float Rot0;
    public float Rot1;
    public float Rot2;
    public float Rot3;
    public float Scale;
}

public static class GoSpawnsReader
{
    public const uint Magic = 0x47505753; // 'GPSW'
    public const uint Version = 1;
    public const int EntrySize = 40;

    public static GoSpawn[] Read(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<GoSpawn>();

        Span<byte> data = File.ReadAllBytes(path);

        if (data.Length < 12)
            return Array.Empty<GoSpawn>();

        ref var header = ref MemoryMarshal.AsRef<GpswHeader>(data);

        if (header.Magic != Magic || header.Version != Version)
            return Array.Empty<GoSpawn>();

        int count = (int)header.TotalCount;
        int expectedSize = 12 + count * EntrySize;
        if (data.Length < expectedSize)
            count = (data.Length - 12) / EntrySize;

        var spawns = new GoSpawn[count];
        for (int i = 0; i < count; i++)
        {
            spawns[i] = MemoryMarshal.AsRef<GoSpawn>(data.Slice(12 + i * EntrySize));
        }
        return spawns;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpswHeader
    {
        public uint Magic;
        public uint Version;
        public uint TotalCount;
    }
}
