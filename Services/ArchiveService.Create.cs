using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MirrorFlux.Models;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Zip;

namespace MirrorFlux.Services;

public static partial class ArchiveService
{
    /// <summary>
    /// 選択された項目から新しいアーカイブを作る。形式は拡張子で決め、ZIP は指定の圧縮レベルを使う。
    /// </summary>
    public static async Task<int> CreateArchiveFromEntriesAsync(string archiveFilePath,
        IReadOnlyList<FileEntry> entries, int zipLevel,
        Action<string, string> logAction, CancellationToken cancellationToken = default)
    {
        // 引数・形式・作成先・圧縮元を確かめる。作成先を圧縮元フォルダーの中に置くことは認めない。
        string extension = Path.GetExtension(archiveFilePath).ToLowerInvariant();
        if (zipLevel is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(zipLevel), "ZIPの圧縮レベルは0～9で指定してください。");
        if (!WritableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"新規作成に対応していない形式です: {extension}");
        if (File.Exists(archiveFilePath) || Directory.Exists(archiveFilePath))
            throw new IOException($"同名の項目が既にあります: {archiveFilePath}");
        string target = Path.GetFullPath(archiveFilePath);
        foreach (var entry in entries)
        {
            if (entry.IsParent) continue;
            if (!File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
                throw new FileNotFoundException("圧縮元が見つかりません。", entry.FullPath);
            if (entry.IsDirectory && FileOperationPaths.IsSameOrDescendant(target, entry.FullPath))
                throw new IOException("作成するアーカイブを圧縮元フォルダー内に置けません。");
        }
        // 格納するファイルとフォルダーを集める。
        var files = await CollectFilesToAddAsync(entries, "");
        var directories = CollectTarDirectories(entries, "");
        if (files.Count == 0 && directories.Count == 0)
            throw new IOException("圧縮する項目がありません。");

        // 一時ファイルに書いてから公開する。作成中に同名ファイルが現れても上書きしない。
        await PublishViaTemporaryAsync(target, async temporary =>
        {
            // ZIP はファイル名を UTF-8 で書く。
            if (extension == ".zip")
            {
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var options = new ZipWriterOptions(
                    zipLevel == 0 ? CompressionType.None : CompressionType.Deflate, zipLevel)
                {
                    ArchiveEncoding = new ArchiveEncoding { Default = System.Text.Encoding.UTF8 }
                };
                using var zip = new ZipWriter(output, options);
                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    zip.WriteDirectory(dir.Key, dir.LastWriteTime);
                }
                foreach (var item in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = await item.OpenReadAsync();
                    zip.Write(item.TargetKey, input, item.LastWriteTime);
                }
            }
            // 7z はフォルダー、ファイルの順に書く。
            else if (extension == ".7z")
            {
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new SevenZipWriter(output, new SevenZipWriterOptions());
                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteDirectory(dir.Key, dir.LastWriteTime);
                }
                foreach (var item in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = await item.OpenReadAsync();
                    writer.Write(item.TargetKey, input, item.LastWriteTime);
                }
            }
            // LZH/LHA は中身をメモリに集めてまとめて書く。空フォルダーは「.empty」ファイルで残す。
            else if (IsLhaExtension(extension))
            {
                var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = await item.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer, cancellationToken);
                    data[item.TargetKey] = buffer.ToArray();
                    times[item.TargetKey] = item.LastWriteTime;
                }
                foreach (var dir in directories.Where(dir => !files.Any(file =>
                    file.TargetKey.StartsWith(dir.Key + "/", StringComparison.OrdinalIgnoreCase))))
                    data[dir.Key + "/.empty"] = Array.Empty<byte>();
                LhaArchiveWriter.Write(temporary, data, cancellationToken, times);
            }
            // TAR は PAX 形式で書く。日時がない項目は現在時刻にする。
            else
            {
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new TarWriter(output, TarEntryFormat.Pax);
                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, dir.Key + "/")
                    {
                        ModificationTime = new DateTimeOffset(dir.LastWriteTime == DateTime.MinValue
                            ? DateTime.Now : dir.LastWriteTime)
                    });
                }
                foreach (var item in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = await item.OpenReadAsync();
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.TargetKey)
                    {
                        DataStream = input,
                        ModificationTime = new DateTimeOffset(item.LastWriteTime == DateTime.MinValue
                            ? DateTime.Now : item.LastWriteTime)
                    });
                }
            }
            return true;
        }, cancellationToken, overwrite: false);

        // 格納した項目ごとにフルパスでログを残す。
        foreach (var dir in directories)
            logAction($"{dir.SourcePath} -> {target}\\{dir.Key.Replace('/', '\\')}", "COPY");
        foreach (var item in files)
            logAction($"{item.DisplaySourcePath} -> {target}\\{item.TargetKey.Replace('/', '\\')}", "COPY");
        return files.Count + directories.Count;
    }
}
