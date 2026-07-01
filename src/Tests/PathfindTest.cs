using System;
using System.IO;
using System.Runtime.InteropServices;
using MaNGOS.Extractor.MmapExtractor.Recast;

namespace MaNGOS.Extractor.Tests;

/// <summary>
/// Standalone pathfinding test: load one or two .mmtile files and run
/// TestPathfinding / TestPathfindingTwoFiles. Compares our C# tile with
/// the MaNGOS reference tile.
/// </summary>
public static class PathfindTest
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  PathfindTest <tile> [sx sy sz ex ey ez]");
            Console.WriteLine("  PathfindTest 2 <tile1> <tile2> [sx sy sz ex ey ez]");
            return 1;
        }

        bool twoFiles = args[0] == "2";
        int idx = twoFiles ? 1 : 0;
        if (twoFiles && args.Length < 3)
        {
            Console.WriteLine("Usage: PathfindTest 2 <tile1> <tile2> [sx sy sz ex ey ez]");
            return 1;
        }

        string tilePath = args[idx];
        if (!File.Exists(tilePath))
        {
            Console.WriteLine($"Tile not found: {tilePath}");
            return 1;
        }
        byte[] data = File.ReadAllBytes(tilePath);
        Console.WriteLine($"[1] {tilePath}: {data.Length} bytes");

        byte[]? data2 = null;
        if (twoFiles)
        {
            string tilePath2 = args[idx + 1];
            if (!File.Exists(tilePath2))
            {
                Console.WriteLine($"Tile not found: {tilePath2}");
                return 1;
            }
            data2 = File.ReadAllBytes(tilePath2);
            Console.WriteLine($"[2] {tilePath2}: {data2.Length} bytes");
        }

        int coordStart = idx + (twoFiles ? 2 : 1);
        float sx = args.Length > coordStart + 0 ? float.Parse(args[coordStart + 0], System.Globalization.CultureInfo.InvariantCulture) : -1750f;
        float sy = args.Length > coordStart + 1 ? float.Parse(args[coordStart + 1], System.Globalization.CultureInfo.InvariantCulture) : -1450f;
        float sz = args.Length > coordStart + 2 ? float.Parse(args[coordStart + 2], System.Globalization.CultureInfo.InvariantCulture) : 1400f;
        float ex = args.Length > coordStart + 3 ? float.Parse(args[coordStart + 3], System.Globalization.CultureInfo.InvariantCulture) : -1700f;
        float ey = args.Length > coordStart + 4 ? float.Parse(args[coordStart + 4], System.Globalization.CultureInfo.InvariantCulture) : -1400f;
        float ez = args.Length > coordStart + 5 ? float.Parse(args[coordStart + 5], System.Globalization.CultureInfo.InvariantCulture) : 1400f;

        Console.WriteLine($"Pathfinding ({sx},{sy},{sz}) -> ({ex},{ey},{ez})");

        int maxPts = 256;
        var pathBuf = new float[maxPts * 3];
        int npts = 0;
        unsafe
        {
            fixed (byte* pData = data)
            fixed (float* pOut = pathBuf)
            {
                if (twoFiles)
                {
                    fixed (byte* pData2 = data2)
                    {
                        npts = RecastNative.TestPathfindingTwoFiles(pData, data.Length, pData2, data2!.Length,
                            sx, sy, sz, ex, ey, ez, pOut, maxPts);
                    }
                }
                else
                {
                    npts = RecastNative.TestPathfinding(pData, data.Length, sx, sy, sz, ex, ey, ez, pOut, maxPts);
                }
            }
        }

        if (npts < 0)
        {
            Console.WriteLine($"FAILED code {npts}");
            return 1;
        }
        if (npts == 0)
        {
            Console.WriteLine("NO PATH (start/end not on navmesh)");
            return 1;
        }

        Console.WriteLine($"PATH ({npts} points):");
        for (int i = 0; i < npts; i++)
        {
            float x = pathBuf[i*3+0];
            float y = pathBuf[i*3+1];
            float z = pathBuf[i*3+2];
            Console.WriteLine($"  [{i,3}] = ({x,10:F3}, {y,10:F3}, {z,10:F3})");
        }
        return 0;
    }
}