namespace MirrorFlux;

/// <summary>
/// アプリケーション全体の起動と終了を扱う。例外の記録と一時ファイルの後始末を行う。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// crash.logへ例外内容を追記する。EXEと同じフォルダーに書けない場合は一時フォルダーへ書く。記録の失敗で例外を重ねない。
    /// </summary>
    internal static void WriteCrashLog(string text)
    {
        // 時刻付きの記録を作り、書ける場所を順に試す。
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}";
        foreach (string directory in new[] { Services.SettingsStore.AppDirectory, System.IO.Path.GetTempPath() })
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "crash.log"), entry); return; }
            catch { }
        }
    }

    /// <summary>
    /// 文字コードの登録、未処理例外の記録、一時展開ファイルの後始末を設定する。
    /// </summary>
    public App()
    {
        // Shift_JISなどのコードページを使えるようにする。
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }
        catch { }

        // 未処理例外をcrash.logへ記録する。UIスレッドの例外は内容も表示する。
        DispatcherUnhandledException += (s, e) =>
        {
            WriteCrashLog(e.Exception.ToString());
            System.Windows.MessageBox.Show(e.Exception.ToString(), "Crash Error");
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            WriteCrashLog(e.ExceptionObject?.ToString() ?? "Unknown error");
        };

        // 起動時と終了時に、アーカイブから一時展開したファイルを削除する。終了時は音楽再生も止める。
        try { Services.ArchiveService.CleanupTempFiles(); } catch { }
        Exit += (_, _) =>
        {
            Services.Music.MusicPlaybackService.Instance.Dispose();
            try { Services.ArchiveService.CleanupTempFiles(); } catch { }
        };
    }
}

