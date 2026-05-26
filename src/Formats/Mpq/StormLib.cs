using System.IO;
using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Mpq;

/// <summary>
/// P/Invoke bindings for StormLib.dll (Windows x64).
/// Provides low-level access to MPQ archive operations.
/// </summary>
public static class StormLib
{
    private const string DllName = "StormLib.dll";

    // Archive Operations

    /// <summary>Opens an MPQ archive.</summary>
    /// <param name="path">Full path to the .mpq file.</param>
    /// <param name="priority">Priority hint for file lookup.</param>
    /// <param name="flags">Open flags (e.g., MPQ_OPEN_NO_LISTFILE).</param>
    /// <param name="handle">Output archive handle on success.</param>
    /// <returns>True if archive opened successfully.</returns>
    [DllImport(DllName, EntryPoint = "SFileOpenArchiveW", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern bool SFileOpenArchive(
        string path,
        uint priority,
        uint flags,
        out IntPtr handle);

    /// <summary>Closes an MPQ archive handle.</summary>
    [DllImport(DllName, EntryPoint = "SFileCloseArchive", CallingConvention = CallingConvention.StdCall)]
    public static extern bool SFileCloseArchive(IntPtr archive);

    /// <summary>Opens a file within an archive for reading.</summary>
    /// <param name="archive">Archive handle from SFileOpenArchive.</param>
    /// <param name="fileName">File path inside the archive.</param>
    /// <param name="scope">Search scope flags.</param>
    /// <param name="fileHandle">Output file handle on success.</param>
    /// <returns>True if file opened successfully.</returns>
    [DllImport(DllName, EntryPoint = "SFileOpenFileEx", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SFileOpenFileEx(
        IntPtr archive,
        string fileName,
        uint scope,
        out IntPtr fileHandle);

    /// <summary>Closes a file handle opened by SFileOpenFileEx.</summary>
    [DllImport(DllName, EntryPoint = "SFileCloseFile", CallingConvention = CallingConvention.StdCall)]
    public static extern bool SFileCloseFile(IntPtr fileHandle);

    /// <summary>Retrieves the size of an opened file.</summary>
    /// <param name="fileHandle">File handle from SFileOpenFileEx.</param>
    /// <param name="fileSizeHigh">High 32 bits of file size (for files > 4GB).</param>
    /// <returns>Low 32 bits of file size.</returns>
    [DllImport(DllName, EntryPoint = "SFileGetFileSize", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SFileGetFileSize(IntPtr fileHandle, out uint fileSizeHigh);

    /// <summary>Reads data from an opened file.</summary>
    /// <param name="fileHandle">File handle from SFileOpenFileEx.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="toRead">Number of bytes to read.</param>
    /// <param name="read">Actual bytes read (output).</param>
    /// <param name="ov">Overlapped structure (pass IntPtr.Zero for synchronous read).</param>
    /// <returns>True if read succeeded.</returns>
    [DllImport(DllName, EntryPoint = "SFileReadFile", CallingConvention = CallingConvention.StdCall)]
    public static extern bool SFileReadFile(
        IntPtr fileHandle,
        byte[] buffer,
        uint toRead,
        out uint read,
        IntPtr ov);

    /// <summary>Sets the file position within an opened file.</summary>
    [DllImport(DllName, EntryPoint = "SFileSetFilePointer", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SFileSetFilePointer(
        IntPtr fileHandle,
        int distance,
        IntPtr distanceHigh,
        SeekOrigin origin);

    // File Enumeration

    /// <summary>Starts enumeration of files in an archive or directory.</summary>
    /// <param name="archive">Archive handle from SFileOpenArchive.</param>
    /// <param name="searchMask">Search pattern (e.g., "*.adt" or null for all).</param>
    /// <param name="findData">Output buffer for file information.</param>
    /// <param name="reserved">Reserved (must be IntPtr.Zero).</param>
    /// <param name="findHandle">Output find handle for subsequent calls (close with SFileFindClose).</param>
    /// <returns>True if enumeration started or file found.</returns>
    [DllImport(DllName, EntryPoint = "SFileFindFirstFileW", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern bool SFileFindFirstFile(
        IntPtr archive,
        [MarshalAs(UnmanagedType.LPWStr)] string? searchMask,
        [Out] out SFileFindData findData,
        IntPtr reserved,
        out IntPtr findHandle);

    /// <summary>Continues file enumeration started by SFileFindFirstFile.</summary>
    /// <param name="findHandle">Find handle from SFileFindFirstFile.</param>
    /// <param name="findData">Output buffer for file information.</param>
    /// <returns>True if another file was found.</returns>
    [DllImport(DllName, EntryPoint = "SFileFindNextFile", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern bool SFileFindNextFile(IntPtr findHandle, out SFileFindData findData);

    /// <summary>Closes a file find handle.</summary>
    [DllImport(DllName, EntryPoint = "SFileFindClose", CallingConvention = CallingConvention.StdCall)]
    public static extern bool SFileFindClose(IntPtr findHandle);

    /// <summary>Enumerates all files matching a pattern within an archive.</summary>
    /// <param name="archive">Archive handle.</param>
    /// <param name="searchMask">Search pattern.</param>
    /// <param name="callback">Callback for each found file.</param>
    /// <param name="userData">User data passed to callback.</param>
    /// <returns>True if enumeration completed.</returns>
    [DllImport(DllName, EntryPoint = "SFileFindFile", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SFileFindFile(
        IntPtr archive,
        string searchMask,
        IntPtr callback,
        IntPtr userData);

    /// <summary>Checks if a file exists within an archive.</summary>
    [DllImport(DllName, EntryPoint = "SFileHasFile", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SFileHasFile(IntPtr archive, string fileName);

    /// <summary>Gets the locale of a file in the archive.</summary>
    /// <param name="archive">Archive handle.</param>
    /// <param name="fileName">File name in archive.</param>
    /// <param name="fileLocale">Output locale code.</param>
    /// <returns>True if file found.</returns>
    [DllImport(DllName, EntryPoint = "SFileGetFileLocale", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SFileGetFileLocale(
        IntPtr archive,
        string fileName,
        out uint fileLocale);

    // Archive Information

    /// <summary>Gets the number of files in an archive.</summary>
    [DllImport(DllName, EntryPoint = "SFileGetArchiveName", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SFileGetArchiveName(IntPtr archive, [Out] char[] nameBuffer, int bufferLength);

    /// <summary>Retrieves archive info (open count, etc.).</summary>
    [DllImport(DllName, EntryPoint = "SFileGetBasePath", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern bool SFileGetBasePath(IntPtr archive, [Out] char[] basePath, int bufferLength);

    // Open Flags

    /// <summary>Flags for SFileOpenArchive.</summary>
    public static class OpenArchiveFlags
    {
        public const uint NoListfile = 0x00100000;  // Don't load internal listfile
        public const uint NoAttributes = 0x00200000; // Don't load file attributes
        public const uint MpoListfile = 0x00400000; // Listfile only in MPQ archive
        public const uint ForceMpqHeader = 0x00800000; // Detect archive type by signature
        public const uint ReadOnly = 0x01000000; // Open archive in read-only mode
        public const uint ShareWrite = 0x02000000; // Allow other processes to open for write
        public const uint ForceListfile = 0x04000000; // Force reading listfile from archive
    }

    /// <summary>Flags for SFileOpenFileEx scope parameter.</summary>
    public static class OpenFileFlags
    {
        public const uint FromMpq = 0x00000000;     // Search in MPQ archive
        public const uint LocalFile = 0x10000000;   // Search on local file system
        public const uint PatchFile = 0x20000000;   // Search in patch files
    }

    // Seek Origins

    public const int SeekBegin = 0;
    public const int SeekCurrent = 1;
    public const int SeekEnd = 2;
}

/// <summary>
/// Data structure filled by SFileFindFirstFile and SFileFindNextFile.
/// Note: StormLib stores some values as 64-bit even on 32-bit builds for alignment.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SFileFindData
{
    public const int MaxFileNameLength = 260;

    /// <summary>File name (null-terminated Unicode string).</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxFileNameLength)]
    public string FileName;

    /// <summary>Size of the file in bytes.</summary>
    public ulong FileSize;

    /// <summary>File flags (compressed, encrypted, etc.).</summary>
    public uint FileFlags;

    /// <summary>File attributes (Win32 FILE_ATTRIBUTE_* flags).</summary>
    public uint CompSize; // Compressed size in bytes

    /// <summary>Creation time.</summary>
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;

    /// <summary>Last access time.</summary>
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;

    /// <summary>Last write time.</summary>
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;

    /// <summary>CRC32 of the file (if present).</summary>
    public uint FileCRC;

    /// <summary>Locale identifier (e.g., 0x00000409 for enUS).</summary>
    public uint Locale;

    /// <summary>Sector size (for compressed files).</summary>
    public uint SectorSize;

    /// <summary>Hash table entry (internal).</summary>
    public uint HashIndex;

    /// <summary>Block table entry (internal).</summary>
    public uint BlockIndex;

    // Unused padding to match StormLib's internal layout
    private uint _reserved0;
    private uint _reserved1;
}