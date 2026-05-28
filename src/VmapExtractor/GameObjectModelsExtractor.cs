using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Dbc;
using MaNGOS.Extractor.Formats.M2;
using MaNGOS.Extractor.Formats.Wmo.Parsing;

namespace MaNGOS.Extractor.VmapExtractor;

public sealed class GameObjectModelsExtractor
{
    private readonly IArchiveReader _archive;
    private readonly ILogger _logger;
    private readonly string _outputDir;
    private readonly M2Parser _m2Parser;
    private readonly WmoParser _wmoParser;

    public GameObjectModelsExtractor(IArchiveReader archive, ILoggerFactory loggerFactory, string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<GameObjectModelsExtractor>();
        _outputDir = outputDir;
        _m2Parser = new M2Parser(archive);
        _wmoParser = new WmoParser(archive, loggerFactory.CreateLogger<WmoParser>());

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    public async Task<int> ExtractAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[GameObjectModels] Starting extraction...");

        string dbcPath = "DBFilesClient\\GameObjectDisplayInfo.dbc";
        if (!_archive.TryReadFile(dbcPath, out var dbcData))
        {
            // Case-insensitive search
            bool found = false;
            foreach (var candidate in new[] { "DBFilesClient\\gameobjectdisplayinfo.dbc", "dbfilesclient\\gameobjectdisplayinfo.dbc" })
            {
                if (_archive.TryReadFile(candidate, out dbcData))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                _logger.LogError("[GameObjectModels] Failed to find GameObjectDisplayInfo.dbc");
                return 0;
            }
        }

        GameObjectDisplayInfoRow[] rows;
        string[] modelPaths;
        {
            var dbcReader = DbcReader<GameObjectDisplayInfoRow>.Parse(dbcData.Span);
            rows = dbcReader.Rows.ToArray();
            modelPaths = new string[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                modelPaths[i] = dbcReader.GetString(rows[i], 1);
        }
        _logger.LogInformation("[GameObjectModels] Loaded {RowCount} rows from GameObjectDisplayInfo.dbc", rows.Length);

        string outputPath = Path.Combine(_outputDir, "GAMEOBJECT_MODELS");
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(stream);

        int count = 0;
        int failedCount = 0;

        for (int rIdx = 0; rIdx < rows.Length; rIdx++)
        {
            var row = rows[rIdx];
            ct.ThrowIfCancellationRequested();

            uint displayId = row.Id;
            string modelPath = modelPaths[rIdx];

            if (string.IsNullOrEmpty(modelPath) || modelPath.Length < 4)
                continue;

            string ext = Path.GetExtension(modelPath).ToLowerInvariant();
            if (ext == ".mdx" || ext == ".mdl")
            {
                modelPath = Path.ChangeExtension(modelPath, ".m2");
                ext = ".m2";
            }

            modelPath = modelPath.Replace('/', '\\');

            bool success = false;
            Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            if (ext == ".m2")
            {
                if (_m2Parser.TryParseBoundingMesh(modelPath, out float[] vertices, out _))
                {
                    success = true;
                    if (vertices.Length > 0)
                    {
                        for (int i = 0; i < vertices.Length / 3; i++)
                        {
                            float x = vertices[i * 3 + 0];
                            float y = vertices[i * 3 + 1];
                            float z = vertices[i * 3 + 2];
                            boundsMin.X = Math.Min(boundsMin.X, x);
                            boundsMin.Y = Math.Min(boundsMin.Y, y);
                            boundsMin.Z = Math.Min(boundsMin.Z, z);
                            boundsMax.X = Math.Max(boundsMax.X, x);
                            boundsMax.Y = Math.Max(boundsMax.Y, y);
                            boundsMax.Z = Math.Max(boundsMax.Z, z);
                        }
                    }
                    else
                    {
                        boundsMin = new Vector3();
                        boundsMax = new Vector3();
                    }
                }
            }
            else if (ext == ".wmo")
            {
                var wmoResult = await _wmoParser.ParseRootAsync(modelPath, ct);
                if (wmoResult.Success && wmoResult.Root != null)
                {
                    success = true;
                    boundsMin = new Vector3(wmoResult.Root.Header.BoundingBoxMin.X, wmoResult.Root.Header.BoundingBoxMin.Y, wmoResult.Root.Header.BoundingBoxMin.Z);
                    boundsMax = new Vector3(wmoResult.Root.Header.BoundingBoxMax.X, wmoResult.Root.Header.BoundingBoxMax.Y, wmoResult.Root.Header.BoundingBoxMax.Z);
                }
            }

            if (success)
            {
                // Format: [uint32 displayId][uint32 nameLen][char name][Vector3 boundsMin][Vector3 boundsMax]
                string nameToWrite = GetUniformName(modelPath);
                byte[] nameBytes = Encoding.ASCII.GetBytes(nameToWrite);

                writer.Write(displayId);
                writer.Write((uint)nameBytes.Length);
                writer.Write(nameBytes);
                
                writer.Write(boundsMin.X);
                writer.Write(boundsMin.Y);
                writer.Write(boundsMin.Z);

                writer.Write(boundsMax.X);
                writer.Write(boundsMax.Y);
                writer.Write(boundsMax.Z);

                count++;
            }
            else
            {
                failedCount++;
            }
        }

        _logger.LogInformation("[GameObjectModels] Extracted {Count} models. {FailedCount} failed.", count, failedCount);
        return count;
    }

    private static string GetUniformName(string path)
    {
        string lower = path.Replace('/', '\\').ToLowerInvariant();
        int slash = lower.LastIndexOf('\\');
        string file = slash >= 0 ? lower[(slash + 1)..] : lower;
        string dir = slash >= 0 ? lower[..slash] : lower;
        if (string.IsNullOrEmpty(dir))
            dir = "\\";

        byte[] digest = MD5.HashData(Encoding.ASCII.GetBytes(dir));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"{hex}-{file}";
    }
}

