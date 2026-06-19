using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Interfaces;

namespace MaNGOS.Extractor.Formats.Mpq;

public sealed class MpqArchive : IArchiveReader
{
    private IntPtr _handle;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<byte>> _cache;
    private readonly int _maxCacheEntries = 256;
    private bool _disposed;

    public string ArchiveName { get; }

    public MpqArchive(string path, ILogger<MpqArchive> logger)
    {
        if (!File.Exists(path))
            throw new MpqException($"Archive not found: {path}");

        _logger = logger;
        _cache = new ConcurrentDictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        ArchiveName = Path.GetFileName(path);

        if (!StormLib.SFileOpenArchive(path, 0, StormLib.OpenArchiveFlags.ReadOnly, out IntPtr handle))
        {
            int error = StormLib.GetLastErrorCode();
            throw new MpqException($"Failed to open archive '{ArchiveName}' (Win32={error})");
        }

        _handle = handle;
        _logger.LogDebug("Opened MPQ archive: {ArchiveName}", ArchiveName);
    }

    internal MpqArchive(string path, IntPtr handle, ILogger<MpqArchive>? logger)
    {
        _handle = handle;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MpqArchive>.Instance;
        _cache = new ConcurrentDictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        ArchiveName = Path.GetFileName(path);
    }

    public bool FileExists(string path)
    {
        ThrowIfDisposed();
        return StormLib.SFileHasFile(_handle, path);
    }

    public bool TryReadFile(string path, out ReadOnlyMemory<byte> data)
    {
        ThrowIfDisposed();

        if (_cache.TryGetValue(path, out data))
            return true;

        if (!StormLib.SFileOpenFileEx(_handle, path, StormLib.OpenFileFlags.FromMpq, out IntPtr fileHandle))
        {
            data = default;
            return false;
        }

        try
        {
            uint size = StormLib.SFileGetFileSize(fileHandle, out uint _);

            if (size == 0xFFFFFFFF)
            {
                data = default;
                return false;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                if (!StormLib.SFileReadFile(fileHandle, buffer, size, out uint read, IntPtr.Zero))
                {
                    data = default;
                    return false;
                }

                byte[] copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, (int)read);
                data = copy;

                if (read < 1024 * 1024)
                {
                    if (_cache.Count >= _maxCacheEntries)
                    {
                        // Evict oldest entries (FIFO approximation)
                        var keysToRemove = _cache.Keys.Take(_cache.Count / 2).ToList();
                        foreach (var key in keysToRemove)
                            _cache.TryRemove(key, out _);
                    }
                    _cache.TryAdd(path, data);
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            StormLib.SFileCloseFile(fileHandle);
        }
    }

    public IEnumerable<string> ListFiles(string pattern)
    {
        ThrowIfDisposed();

        IntPtr findHandle = StormLib.SFileFindFirstFile(_handle, pattern, out SFileFindData findData, IntPtr.Zero);
        if (findHandle == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            do
            {
                if (!string.IsNullOrEmpty(findData.FileName))
                    yield return findData.FileName;
            }
            while (StormLib.SFileFindNextFile(findHandle, out findData));
        }
        finally
        {
            StormLib.SFileFindClose(findHandle);
        }
    }

    public void ForEachFileReversePriority(string pattern, Action<string> onFile)
    {
        // Single archive: priority order is trivial. Just iterate this archive.
        foreach (var file in ListFiles(pattern))
            onFile(file);
    }

    public Stream? OpenFileStream(string path)
    {
        ThrowIfDisposed();

        if (!StormLib.SFileOpenFileEx(_handle, path, StormLib.OpenFileFlags.FromMpq, out IntPtr fileHandle))
        {
            return null;
        }

        return new MpqFileStream(fileHandle);
    }

    public void ClearCache() => _cache.Clear();

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MpqArchive));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();

        if (_handle != IntPtr.Zero)
        {
            StormLib.SFileCloseArchive(_handle);
            _handle = IntPtr.Zero;
        }

        _logger.LogDebug("Closed MPQ archive: {ArchiveName}", ArchiveName);
    }

    private sealed class MpqFileStream : Stream
    {
        private readonly IntPtr _fileHandle;
        private long _position;
        private readonly long _length;

        public MpqFileStream(IntPtr fileHandle)
        {
            _fileHandle = fileHandle;
            _position = 0;
            uint size = StormLib.SFileGetFileSize(fileHandle, out uint sizeHigh);
            _length = sizeHigh > 0 ? long.MaxValue : size;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var localBuffer = new byte[count];
            if (StormLib.SFileReadFile(_fileHandle, localBuffer, (uint)count, out uint read, IntPtr.Zero))
            {
                _position += read;
                Array.Copy(localBuffer, 0, buffer, offset, read);
                return (int)read;
            }
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            int pos = origin switch
            {
                SeekOrigin.Begin => (int)offset,
                SeekOrigin.Current => (int)(_position + offset),
                SeekOrigin.End => (int)(_length + offset),
                _ => 0
            };

            StormLib.SFileSetFilePointer(_fileHandle, pos, IntPtr.Zero, SeekOrigin.Begin);
            _position = pos;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_fileHandle != IntPtr.Zero)
            {
                StormLib.SFileCloseFile(_fileHandle);
            }
        }
    }
}

public sealed class MpqException : Exception
{
    public MpqException(string message) : base(message) { }
    public MpqException(string message, Exception inner) : base(message, inner) { }
}
