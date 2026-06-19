using System.IO;

namespace MaNGOS.Extractor.Core.Interfaces;

/// <summary>
/// Abstraction for reading files from a WoW client data archive (MPQ).
/// </summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>
    /// Checks whether a file exists in the archive.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Attempts to read the entire contents of a file into memory.
    /// Returns true if the file was found and read successfully.
    /// </summary>
    bool TryReadFile(string path, out ReadOnlyMemory<byte> data);

    /// <summary>
    /// Returns all file paths matching the given pattern (e.g., "*.adt").
    /// </summary>
    IEnumerable<string> ListFiles(string pattern);

    /// <summary>
    /// Iterate files matching <paramref name="pattern"/> in REVERSE priority
    /// order (lowest first, highest last), invoking the callback for each
    /// file as it's discovered. Avoids materialising 50k+ SFileFind handles.
    /// </summary>
    void ForEachFileReversePriority(string pattern, Action<string> onFile);

    /// <summary>
    /// Opens a file stream for reading without loading the entire file.
    /// </summary>
    Stream? OpenFileStream(string path);

    /// <summary>
    /// Gets the archive name (e.g., "texture.mpq").</summary>
    string ArchiveName { get; }
}