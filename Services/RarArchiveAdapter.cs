using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Options;
using SharpCompress.Readers;

namespace MirrorFlux.Services;

/// <summary>
/// RAR を同梱の mf_rar.dll で読み、SharpCompress の IArchive として見せる。RAR 以外は SharpCompress にそのまま任せる。
/// RAR4 の分割書庫は一時ファイルへつないでから読む。
/// </summary>
internal sealed class RarArchiveAdapter : IArchive
{
    private readonly string[] paths;
    private readonly bool rar5;
    private readonly string? joined;
    private readonly List<Entry> entries = [];
    private readonly HashSet<EntryStream> streams = [];
    private bool disposed;
    public ReaderOptions ReaderOptions { get; }

    /// <summary>
    /// 先頭の署名で RAR4・RAR5 を見分け、RAR なら自前の読み取り、それ以外は SharpCompress で開く。
    /// </summary>
    internal static IArchive Open(string path, ReaderOptions options)
    {
        using var input = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8]; int length = input.Read(magic);
        bool rar = length >= 7 && magic[..7].SequenceEqual("Rar!\x1a\x07\x00"u8);
        bool rar5 = length == 8 && magic.SequenceEqual("Rar!\x1a\x07\x01\x00"u8);
        if (!rar && !rar5) return ArchiveFactory.OpenArchive(path, options);
        return new RarArchiveAdapter(path, options, rar5);
    }

    /// <summary>
    /// 分割書庫の全ボリュームを探し、項目の一覧を読み込む。失敗したらつないだ一時ファイルを消す。
    /// </summary>
    private RarArchiveAdapter(string path, ReaderOptions options, bool isRar5)
    {
        ReaderOptions = options; rar5 = isRar5;
        paths = RarVolumes.Find(path);
        try
        {
            // RAR4 の分割書庫は1つの一時ファイルにつなぐ。項目を順に読み、名前・サイズ・日時・リンク先を記録する。
            if (!rar5 && paths.Length > 1) { joined = RarVolumes.JoinRar4(paths); paths = [joined]; }
            using var handle = RarNative.Open(paths, rar5);
            int result;
            while ((result = RarNative.Next(handle)) > 0)
            {
                string key = Marshal.PtrToStringUTF8(RarNative.Name(handle)) ?? throw new InvalidDataException("RAR entry name is invalid");
                long size = RarNative.Size(handle);
                if (size < 0) throw new InvalidDataException("RAR entry size is invalid");
                DateTime? time;
                try { time = DateTimeOffset.FromUnixTimeSeconds(RarNative.Time(handle)).LocalDateTime; }
                catch (ArgumentOutOfRangeException) { time = null; }
                entries.Add(new Entry(this, entries.Count, key, size, time, RarNative.IsDirectory(handle) != 0,
                    Marshal.PtrToStringUTF8(RarNative.Link(handle))));
            }
            if (result < 0) throw RarNative.Failure(handle);
        }
        catch { if (joined != null) File.Delete(joined); throw; }
    }

    public IEnumerable<IArchiveEntry> Entries => entries;
    public IEnumerable<IVolume> Volumes => [];
    public ArchiveType Type => ArchiveType.Rar;
    // 控えめに常にソリッド扱いにし、読み取り側が項目ごとに独立したブロックだと仮定しないようにする。
    public bool IsSolid => true;
    public bool IsComplete => true;
    public bool IsEncrypted => false;
    public long TotalSize => paths.Sum(p => new FileInfo(p).Length);
    public long TotalUncompressedSize => entries.Sum(e => e.Size);
    public IReader ExtractAllEntries() => throw new NotSupportedException("Use entry streams for the RAR adapter");
    /// <summary>
    /// 開いている項目のストリームを閉じ、つないだ一時ファイルを消す。
    /// </summary>
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var stream in streams.ToArray()) stream.Dispose();
        if (joined != null) File.Delete(joined);
    }
    /// <summary>
    /// 項目の中身を読むストリームを開く。書庫を開き直して先頭からその項目まで進める。シンボリックリンクは展開しない。
    /// </summary>
    private Stream OpenEntry(Entry entry)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (entry.LinkTarget != null) throw new InvalidDataException("RAR symbolic links are not extracted");
        var handle = RarNative.Open(paths, rar5);
        try
        {
            // 一覧を読んだ後に書庫が変わっていたら、名前とサイズの食い違いで検出して中止する。
            for (int i = 0; i <= entry.Index; i++)
                if (RarNative.Next(handle) <= 0) throw RarNative.Failure(handle);
            if ((Marshal.PtrToStringUTF8(RarNative.Name(handle)) ?? "") != entry.Key || RarNative.Size(handle) != entry.Size)
                throw new IOException("RAR changed after its directory was read");
            var stream = new EntryStream(handle, entry.Size, s => streams.Remove(s)); streams.Add(stream); return stream;
        }
        catch { handle.Dispose(); throw; }
    }
    /// <summary>
    /// RAR の1項目。SharpCompress の IArchiveEntry として必要な情報を返す。
    /// </summary>
    private sealed record Entry(RarArchiveAdapter Owner, int Index, string Key, long Size, DateTime? LastModifiedTime,
        bool IsDirectory, string? LinkTarget) : IArchiveEntry
    {
        public Stream OpenEntryStream() => Owner.OpenEntry(this);
        public ValueTask<Stream> OpenEntryStreamAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenEntryStream()); }
        public IArchive Archive => Owner;
        public bool IsComplete => true;
        public CompressionType CompressionType => CompressionType.Rar;
        public DateTime? ArchivedTime => null;
        public long CompressedSize => 0;
        public long Crc => 0;
        public DateTime? CreatedTime => null;
        public bool IsEncrypted => false;
        public bool IsSplitAfter => false;
        public bool IsSolid => true;
        public int VolumeIndexFirst => 0;
        public int VolumeIndexLast => Owner.paths.Length - 1;
        public DateTime? LastAccessedTime => null;
        public int? Attrib => IsDirectory ? (int)FileAttributes.Directory : (int)FileAttributes.Normal;
        public IReaderOptions Options => Owner.ReaderOptions;
    }
    /// <summary>
    /// RAR の1項目の中身を前から順に読むだけのストリーム。読んだ長さが項目のサイズと合わなければ例外にする。
    /// </summary>
    private sealed class EntryStream(RarNative.Handle handle, long length, Action<EntryStream> closed) : Stream
    {
        private long position;
        private bool disposed;
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override unsafe int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count) throw new ArgumentException("Invalid buffer range");
            ObjectDisposedException.ThrowIf(disposed, this);
            if (count == 0) return 0;
            int n; fixed (byte* pointer = &buffer[offset]) n = RarNative.Read(handle, (IntPtr)pointer, count);
            if (n < 0) throw RarNative.Failure(handle);
            position = checked(position + n);
            if (position > length || (n == 0 && position != length)) throw new InvalidDataException("RAR entry length mismatch");
            return n;
        }
        protected override void Dispose(bool disposing)
        { if (!disposed) { disposed = true; handle.Dispose(); closed(this); } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
