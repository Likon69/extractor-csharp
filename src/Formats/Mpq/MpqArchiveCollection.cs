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
        var dataDir = Path.Combine(_wowDirectory, "Data");
        var patchDir = Path.Combine(dataDir, "patch");

        var paths = new List<string>();

        // Priority: locales > expansion > lichking > common > patches
        AddIfExists(paths, dataDir, $"locales/{locale}.MPQ");
        AddIfExists(paths, dataDir, $"locales/{locale}-export.MPQ");
        AddIfExists(paths, dataDir, "expansion.MPQ");
        AddIfExists(paths, dataDir, "lichking.MPQ");
        AddIfExists(paths, dataDir, "common.MPQ");
        AddIfExists(paths, dataDir, "common-2.MPQ");

        // Patches (lowest priority)
        AddIfExists(paths, patchDir, "patch.MPQ");
        AddIfExists(paths, patchDir, "patch-2.MPQ");
        AddIfExists(paths, patchDir, "patch-3.MPQ");
        AddIfExists(paths, dataDir, $"patch-{locale}.MPQ");

        if (paths.Count == 0)
            throw new MpqException($"No MPQ archives found in: {dataDir}");

        var logger = loggerFactory.CreateLogger<MpqArchive>();

        foreach (var path in paths)
        {
            try
            {
                var archive = new MpqArchive(path, logger);
                _archives.Add(archive);
                _logger.LogDebug("Opened archive: {Name}", archive.ArchiveName);
            }
            catch (MpqException ex)
            {
                _logger.LogWarning(ex, "Failed to open archive: {Path}", path);
            }
        }

        _logger.LogInformation("Loaded {Count} MPQ archives", _archives.Count);
    }

    private static void AddIfExists(List<string> list, string baseDir, string relative)
    {
        string fullPath = Path.Combine(baseDir, relative);
        if (File.Exists(fullPath) && !list.Contains(fullPath))
            list.Add(fullPath);
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        foreach (var archive in _archives)
        {
            if (archive.FileExists(path))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public bool TryReadFile(string path, out ReadOnlyMemory<byte> data)
    {
        foreach (var archive in _archives)
        {
            if (archive.TryReadFile(path, out data))
                return true;
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
        foreach (var archive in _archives)
        {
            var stream = archive.OpenFileStream(path);
            if (stream != null)
                return stream;
        }
        return null;
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