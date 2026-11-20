using System.Diagnostics;
using System.IO;
using System.Text;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 実行ファイルと同じフォルダーの INI（mf.ini）を読み書きする。終了時は利用者の編集を保ったまま、実行中に変わった値だけを書き戻す。
/// </summary>
public sealed class SettingsStore
{
    public const string SettingsFileName = "mf.ini";
    private readonly string _settingsPath;
    private string? _loadedText;
    private Dictionary<string, string> _loadedRuntime = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 実行ファイルが実際に置かれているフォルダー。単一ファイル配布（PublishSingleFile）で一時フォルダーへ展開されて動くときも、展開先ではなく EXE の場所を返す。
    /// </summary>
    public static string AppDirectory
    {
        get
        {
            // プロセスのパス、メインモジュール、BaseDirectory の順に試す。
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    return dir;
                }
            }

            try
            {
                var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainModule))
                {
                    var dir = Path.GetDirectoryName(mainModule);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        return dir;
                    }
                }
            }
            catch
            {
            }

            return AppContext.BaseDirectory;
        }
    }

    public SettingsStore()
        : this(GetDefaultSettingsPath())
    {
    }

    private static string GetDefaultSettingsPath() => Path.Combine(AppDirectory, SettingsFileName);

    public SettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    /// <summary>
    /// INI を読み込み、設定・保存できるか・警告を返す。終了時の書き戻しに使うため、読んだ内容と実行中に変わる値の初期値を覚える。
    /// </summary>
    public (AppSettings Settings, bool CanSave, string? Warning) Load()
    {
        var settings = new AppSettings();
        string? warning = null;

        // INI がなければ既定値で始め、書き込めるかだけを確かめる。
        if (!File.Exists(_settingsPath))
        {
            _loadedText = null;
            _loadedRuntime = IniDocument.RuntimeValues(settings);
            var canWriteNew = CheckWritable(out var writeWarningNew);
            return (settings, canWriteNew, writeWarningNew);
        }

        // 読めなければ既定値で起動する。
        try
        {
            _loadedText = ReadSettingsText(_settingsPath);
            settings = SettingsIniReader.Parse(_loadedText);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = $"設定ファイルを読み込めませんでした。初期値で起動します: {exception.Message}";
        }

        _loadedRuntime = IniDocument.RuntimeValues(settings);
        var canSave = CheckWritable(out var writeWarning);
        warning ??= writeWarning;
        return (settings, canSave, warning);
    }

    /// <summary>
    /// 設定ファイルを文字列として読む。UTF-8（BOM あり・なし）と UTF-16（BOM あり）はそのまま読み、
    /// UTF-8 として不正なバイト列は Shift_JIS（CP932）として読む。保存は常に UTF-8 なので、
    /// Shift_JIS で保存された設定の日本語を置換文字で壊さないようにする。
    /// </summary>
    internal static string ReadSettingsText(string path)
    {
        // BOM があれば、それに従って読む。
        byte[] bytes = File.ReadAllBytes(path);
        bool hasBom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)) ||
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hasBom)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        // BOM がなければ厳密な UTF-8 として読み、だめなら CP932 で読む。
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(932).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.UTF8.GetString(bytes); }
        }
    }

    public static void ApplyTheme(AppSettings settings) => SettingsTheme.Apply(settings);

    /// <summary>
    /// 終了時に、実行中に変わった値（パネルのパスなど）を INI へ書き戻す。利用者の編集は残し、一時ファイルを書いてから置き換える。
    /// </summary>
    public bool TrySaveRuntimeState(AppSettings settings, out string? error)
    {
        error = null;
        // %saveState が 0 なら終了時に INI を書き換えない。左右のフォルダーなどは次回も INI に書かれた値で始まる。
        if (!settings.SaveState) return true;
        string? temporaryPath = null;
        try
        {
            // 起動後に INI が消されていたら保存しない。最初からなければ新しく書く。
            if (!File.Exists(_settingsPath))
            {
                if (_loadedText != null) { error = "設定ファイルが削除されたため、終了時の保存を中止しました。"; return false; }
                return TrySave(settings, out error);
            }
            // 今の INI に変わった値を反映する。変化がなければ書かない。
            string current = ReadSettingsText(_settingsPath);
            string merged = IniDocument.MergeRuntime(_loadedText ?? "", current, _loadedRuntime, settings);
            if (merged == current) return true;
            // 一時ファイルに UTF-8 で書き、その間に INI が変わっていなければ置き換える。
            temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!, $".mf-state-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, merged, new UTF8Encoding(false));
            if (ReadSettingsText(_settingsPath) != current)
            {
                error = "設定ファイルが更新されたため、終了時の保存を中止しました。";
                return false;
            }
            File.Move(temporaryPath, _settingsPath, true);
            _loadedText = merged;
            _loadedRuntime = IniDocument.RuntimeValues(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"状態を保存できません: {ex.Message}";
            return false;
        }
        finally
        {
            if (temporaryPath != null)
                try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 設定全体を新しい INI として書く。一時ファイルに書いてから置き換え、失敗したら一時ファイルを消す。
    /// </summary>
    public bool TrySave(AppSettings settings, out string? error)
    {
        error = null;
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!,
            $".mf-{Guid.NewGuid():N}.tmp");

        try
        {
            string text = SettingsIniWriter.Serialize(settings);
            File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
            File.Move(temporaryPath, _settingsPath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"設定を実行ファイルと同じフォルダーに保存できません: {exception.Message}";
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// INI のフォルダーに試しにファイルを作り、書き込めるか確かめる。
    /// </summary>
    private bool CheckWritable(out string? warning)
    {
        warning = null;
        var probePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!, $".mf-write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = "設定保存先に書き込みできません。初期値で起動し、設定は保存しません。";
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
