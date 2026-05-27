using System.IO;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Interfaces;

namespace MaNGOS.Extractor.Formats.Mpq;

/// <summary>
/// Manages a collection of MPQ archives with lookup priority.
/// Archives are searched in order until a file is found (first-match wins).
/// </summary>
public sealed class MpqArchiveCollection : IArchiveReader
{
    private readonly List<MpqArchive> _archives;
    private readonly ILogger _logger;
    private readonly string _wowDirectory;

    /// <inheritdoc />
    public string ArchiveName => "ArchiveCollection";

    /// <summary>
    /// Creates a collection by discovering MPQ files in a WoW directory.
    /// </summary>
    public static MpqArchiveCollection FromWoWDirectory(string wowDir, string locale, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<MpqArchiveCollection>();
        var collection = new MpqArchiveCollection(wowDir, logger);
        collection.DiscoverAndOpenArchives(locale, loggerFactory);
        return collection;
    }

    private MpqArchiveCollection(string wowDir, ILogger logger)
    {
        _wowDirectory = wowDir;
        _archives = new List<MpqArchive>();
        _logger = logger;
    }

    private void DiscoverAndOpenArchives(string locale, ILoggerFactory loggerFactory)
    {
        var dataDir   = Path.Combine(_wowDirectory, "Data");
        var localeDir = Path.Combine(dataDir, locale); // e.g. Data/enUS

        var paths = new List<string>();

        // WotLK 3.3.5a layout: patches first (highest priority), then base, then locale
        // Locale patches
        AddIfExists(paths, localeDir, $"patch-{locale}-3.MPQ");
        AddIfExists(paths, localeDir, $"patch-{locale}-2.MPQ");
        AddIfExists(paths, localeDir, $"patch-{locale}.MPQ");

        // Root patches
        AddIfExists(paths, dataDir, "patch-3.MPQ");
        AddIfExists(paths, dataDir, "patch-2.MPQ");
        AddIfExists(paths, dataDir, "patch.MPQ");

        // Expansion and base archives
        AddIfExists(paths, dataDir, "lichking.MPQ");
        AddIfExists(paths, dataDir, "expansion.MPQ");
        AddIfExists(paths, dataDir, "common-2.MPQ");
        AddIfExists(paths, dataDir, "common.MPQ");

        // Locale files
        AddIfExists(paths, localeDir, $"base-{locale}.MPQ");
        AddIfExists(paths, localeDir, $"backup-{locale}.MPQ");
        AddIfExists(paths, localeDir, $"lichking-locale-{locale}.MPQ");
        AddIfExists(paths, localeDir, $"expansion-locale-{locale}.MPQ");
        AddIfExists(paths, localeDir, $"locale-{locale}.MPQ");

        // Safety net for clients with additional locale/root MPQs not listed above.
        // These are appended at lower priority to preserve patch-first resolution.
        AddAllMpqs(paths, localeDir);
        AddAllMpqs(paths, dataDir);

        if (paths.Count == 0)
            throw new MpqException($"No MPQ archives found. Checked: {dataDir} and {localeDir}. " +
                $"Verify WoW client path and locale ({locale}).");

        var logger = loggerFactory.CreateLogger<MpqArchive>();
        int failedCount = 0;

        foreach (var path in paths)
        {
            try
            {
                var archive = new MpqArchive(path, logger);
                _archives.Add(archive);
            }
            catch (Exception ex) when (ex is MpqException or DllNotFoundException or System.DllNotFoundException)
            {
                failedCount++;
                _logger.LogWarning(ex, "Failed to open archive: {Path}", path);
            }
        }

        _logger.LogInformation("Loaded {Count} MPQ archives ({Failed} failed)", _archives.Count, failedCount);

        if (_archives.Count == 0)
            throw new MpqException($"{paths.Count} MPQ files found but 0 could be opened. " +
                $"Check StormLib.dll is in the executable directory.");
    }

    private static void AddIfExists(List<string> list, string baseDir, string relative)
    {
        string fullPath = Path.Combine(baseDir, relative);
        if (File.Exists(fullPath) && !list.Contains(fullPath))
            list.Add(fullPath);
    }

    private static void AddAllMpqs(List<string> list, string baseDir)
    {
        if (!Directory.Exists(baseDir))
            return;

        foreach (var fullPath in Directory.EnumerateFiles(baseDir, "*.mpq", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (!list.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                list.Add(fullPath);
        }
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        foreach (var candidate in GetPathCandidates(path))
        {
            foreach (var archive in _archives)
            {
                if (archive.FileExists(candidate))
                    return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public bool TryReadFile(string path, out ReadOnlyMemory<byte> data)
    {
        foreach (var candidate in GetPathCandidates(path))
        {
            foreach (var archive in _archives)
            {
                if (archive.TryReadFile(candidate, out data))
                    return true;
            }
        }

        data = default;
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<string> ListFiles(string pattern)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var archive in _archives)
        {
            foreach (var file in archive.ListFiles(pattern))
            {
                if (seen.Add(file))
                    yield return file;
            }
        }
    }

    /// <inheritdoc />
    public Stream? OpenFileStream(string path)
    {
        foreach (var candidate in GetPathCandidates(path))
        {
            foreach (var archive in _archives)
            {
                var stream = archive.OpenFileStream(candidate);
                if (stream != null)
                    return stream;
            }
        }
        return null;
    }

    private static IEnumerable<string> GetPathCandidates(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                candidates.Add(value);
        }

        Add(path.Trim());

        var normalized = path.Trim().Replace('/', '\\');
        while (normalized.Contains("\\\\", StringComparison.Ordinal))
            normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);

        Add(normalized);
        Add(normalized.TrimStart('\\'));
        Add(normalized.Replace('\\', '/'));
        Add(normalized.TrimStart('\\').Replace('\\', '/'));

        foreach (var c in candidates)
            yield return c;
    }

    /// <inheritdoc />
    public int ArchiveCount => _archives.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
        _archives.Clear();
    }
}