using System.Diagnostics;
using System.IO;
using MirrorFlux.Input;
using MirrorFlux.Services;

namespace MirrorFlux.ViewModels;

/// <summary>
/// メイン画面のビューモデルのうち、外部アプリの起動を受け持つ部分。
/// </summary>
public sealed partial class MainViewModel
{
    internal Action<ProcessStartInfo> ExternalProgramLauncher { get; set; } = request => Process.Start(request);

    /// <summary>
    /// INIに書かれた外部実行の設定を解釈し、マークしたファイルを引数にして外部アプリを起動する。
    /// </summary>
    private async Task ExecuteExternalProgramAsync(string? text)
    {
        // 設定を解釈できなければ案内だけ出して終わる。
        if (!ExternalCommandTemplate.TryParse(text ?? "", out var template))
        {
            StatusMessage = "外部実行の設定を確認してください。";
            return;
        }
        // 操作中のパネルのフォルダーと、引数で使う場合だけマークしたファイルを集める。
        var pane = ActivePane;
        string directory = pane.CurrentPath;
        string[] marked = template!.Arguments.Any(ExternalCommandTemplate.IsMarkedFiles)
            ? pane.Entries.Where(e => e.IsMarked && !e.IsDirectory && !e.IsParent).Select(e => e.FullPath).ToArray()
            : [];
        await RunWithLoadingAsync("外部アプリの起動準備中...", async () =>
        {
            try
            {
                // 実行ファイルの相対パスはアプリのフォルダーを基準に解決する。
                string executable = Environment.ExpandEnvironmentVariables(template.Executable);
                if (!Path.IsPathFullyQualified(executable) && (executable.Contains('\\') || executable.Contains('/')))
                    executable = Path.GetFullPath(executable, SettingsStore.AppDirectory);
                // アーカイブ内などで作業フォルダーが実在しないときは、アーカイブのあるフォルダーかアプリのフォルダーを使う。
                if (!Directory.Exists(directory))
                    directory = ArchiveService.TrySplitArchivePath(directory, out var archive, out _)
                        ? Path.GetDirectoryName(archive)! : SettingsStore.AppDirectory;
                var request = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
                // アーカイブ内のファイルは一時ファイルへ展開してから渡す。
                var paths = new List<string>();
                foreach (string path in marked)
                {
                    string resolved = path;
                    if (await Task.Run(() => ArchiveService.IsArchiveEntry(path)))
                        resolved = await ArchiveService.ExtractEntryToTempFileAsync(path) ?? throw new IOException("展開できませんでした: " + path);
                    if (!File.Exists(resolved)) throw new FileNotFoundException("マークしたファイルが見つかりません。", path);
                    paths.Add(Path.GetFullPath(resolved));
                }
                // 引数を組み立て、マークファイルの指定はファイルごとの引数に展開する。
                foreach (string argument in template.Arguments)
                {
                    if (ExternalCommandTemplate.IsMarkedFiles(argument))
                    {
                        foreach (string path in paths) request.ArgumentList.Add(path);
                    }
                    else request.ArgumentList.Add(Environment.ExpandEnvironmentVariables(argument));
                }
                // 起動してログに残す。
                ExternalProgramLauncher(request);
                AddLog($"外部実行: {executable} (マークファイル {marked.Length}件)", "ACT");
                StatusMessage = $"外部実行: {executable}";
            }
            catch (Exception ex)
            {
                // 失敗は理由をログへ残す。
                AddLog($"外部実行失敗: {template.Executable} ({ex.Message})", "ERR");
                StatusMessage = $"外部実行できませんでした: {template.Executable}";
            }
        });
    }
}
