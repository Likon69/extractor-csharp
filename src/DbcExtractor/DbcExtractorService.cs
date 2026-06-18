using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Interfaces;

namespace MaNGOS.Extractor.DbcExtractor;

/// <summary>
/// Extracts DBC/DB2 client database files from the MPQ archives to disk.
///
/// Mirrors MaNGOS C++ map-extractor/System.cpp::ExtractDBCFiles:
///   - For each MPQ, list all *.dbc and *.db2 files via SFileFindFirstFile/Next.
///   - Dedup across archives (patches override base).
///   - Write to &lt;output&gt;/dbc/&lt;basename&gt; stripping the "DBFilesClient\" prefix.
///   - Also extract component.wow-&lt;locale&gt;.txt (WotLK build marker).
/// </summary>
public sealed class DbcExtractorService
{
    private const string DbcPrefix = "DBFilesClient\\";

    private readonly IArchiveReader _archive;
    private readonly ILogger _logger;
    private readonly string _outputDir;

    public DbcExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<DbcExtractorService>();
        _outputDir = outputDir;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
        _logger.LogInformation("[Dbc] Output directory: {OutputDir}", outputDir);
    }

    /// <summary>
    /// Extracts all DBC/DB2 files plus the locale build-marker file.
    /// Returns the total number of files written.
    /// </summary>
    public async Task<int> ExtractAsync(string locale, CancellationToken ct = default)
    {
        _logger.LogInformation("[Dbc] Starting DBC/DB2 extraction (locale={Locale})", locale);

        return await Task.Run(() =>
        {
            int count = 0;

            // 1) DBC files (247 in WotLK enUS, e.g. Map.dbc, AreaTable.dbc, LiquidType.dbc)
            count += ExtractPattern("*.dbc");

            // 2) DB2 files (Cataclysm+; usually 0 in WotLK 3.3.5a)
            count += ExtractPattern("*.db2");

            // 3) Build marker (WotLK writes component.wow-<locale>.txt at MPQ root)
            string markerName = $"component.wow-{locale}.txt";
            if (_archive.FileExists(markerName))
            {
                if (WriteFromArchive(markerName, Path.Combine(_outputDir, markerName)))
                    count++;
            }

            _logger.LogInformation("[Dbc] Extracted {Count} DBC/DB2 files to {OutputDir}", count, _outputDir);
            return count;
        }, ct);
    }

    private int ExtractPattern(string pattern)
    {
        int written = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mpqPath in _archive.ListFiles(pattern))
        {
            if (!seen.Add(mpqPath))
                continue; // already extracted from a higher-priority archive

            // Strip the "DBFilesClient\" prefix to match MaNGOS C++ output layout.
            // e.g. "DBFilesClient\Map.dbc" -> "Map.dbc"
            string baseName = StripDbcPrefix(mpqPath);
            if (string.IsNullOrEmpty(baseName))
                continue;

            string destPath = Path.Combine(_outputDir, baseName);
            if (WriteFromArchive(mpqPath, destPath))
                written++;
        }

        return written;
    }

    /// <summary>
    /// Reads the file from the MPQ archive and writes it to disk.
    /// Returns true on success. Skips files that already exist on disk with
    /// matching size (idempotent re-run).
    /// </summary>
    private bool WriteFromArchive(string mpqPath, string destPath)
    {
        try
        {
            // Ensure parent directory exists (defensive; should always be _outputDir here)
            string? parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            if (!_archive.TryReadFile(mpqPath, out ReadOnlyMemory<byte> data))
            {
                _logger.LogWarning("[Dbc] Failed to read {Path} from MPQ", mpqPath);
                return false;
            }

            // Skip rewrite if size matches (avoids touching mtime on re-runs)
            if (File.Exists(destPath))
            {
                var existing = new FileInfo(destPath);
                if (existing.Length == data.Length)
                    return true;
            }

            File.WriteAllBytes(destPath, data.Span);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Dbc] Failed to write {Dest}", destPath);
            return false;
        }
    }

    private static string StripDbcPrefix(string mpqPath)
    {
        // StormLib returns paths with backslashes on Windows. Accept both for safety.
        if (mpqPath.StartsWith(DbcPrefix, StringComparison.OrdinalIgnoreCase))
            return mpqPath.Substring(DbcPrefix.Length);
        if (mpqPath.StartsWith("DBFilesClient/", StringComparison.OrdinalIgnoreCase))
            return mpqPath.Substring("DBFilesClient/".Length);
        return mpqPath;
    }
}
