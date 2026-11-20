using System;
using System.Text.RegularExpressions;
using System.Windows.Input;
using MirrorFlux.Models;

namespace MirrorFlux.Input;

/// <summary>
/// INIのキー割り当て行と拡張子関連付け行を読み書きする。
/// 書式例：
///   >KEY_C     ----    &filecopy;
///   >KEY_F5    ----    &filecopy;
///   >KEY_C     -s--    &filecopy;
///   >KEY_A     -s-c    &exit;
///   >KEY_F     -ac-    &exit;
/// </summary>
public static class IniKeyParser
{
    /// <summary>
    /// 「>キー名 修飾キー &amp;コマンド;」の行。
    /// </summary>
    private static readonly Regex LineRegex = new(
        @"^>(KEY_[A-Za-z0-9_\-\\]+)\s+([sSacCtT\-_]{1,8})\s+&([A-Za-z0-9_\-]+);?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 「>キー名 修飾キー 外部プログラムのコマンド行」の行。
    /// </summary>
    private static readonly Regex ExternalLineRegex = new(
        @"^>(KEY_[A-Za-z0-9_\-\\]+)\s+([sSacCtT\-_]{1,8})\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// キー割り当ての1行を読む。「&amp;」で始まらない3列目は外部プログラムのコマンド行として扱う。
    /// </summary>
    public static bool TryParseLine(string line, out KeyBindingItem? item)
    {
        // 「>」で始まる行だけを対象にする。
        item = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        string trimmed = line.Trim();
        if (!trimmed.StartsWith('>')) return false;

        // 外部プログラムの行として読めればそれで決める。
        var external = ExternalLineRegex.Match(trimmed);
        if (external.Success && !external.Groups[3].Value.StartsWith('&') &&
            TryParseKey(external.Groups[1].Value, out Key externalKey) &&
            ExternalCommandTemplate.TryParse(external.Groups[3].Value, out var template))
        {
            item = new KeyBindingItem(externalKey, ParseModifiers(external.Groups[2].Value),
                AppCommand.ExternalProgram, "", template!.Text);
            return true;
        }

        // 「#」以降のコメントを除く。
        int commentIdx = trimmed.IndexOf('#');
        if (commentIdx >= 0)
        {
            trimmed = trimmed.Substring(0, commentIdx).Trim();
        }

        // キー名・修飾キー・コマンド名を取り出し、どれかが読めなければ受け付けない。
        var match = LineRegex.Match(trimmed);
        if (!match.Success) return false;

        string rawKeyName = match.Groups[1].Value;
        string rawFlags = match.Groups[2].Value;
        string rawCmd = match.Groups[3].Value;

        if (!TryParseKey(rawKeyName, out Key key)) return false;

        ModifierKeys modifiers = ParseModifiers(rawFlags);

        if (!TryParseCommand(rawCmd, out AppCommand command)) return false;

        item = new KeyBindingItem(key, modifiers, command, rawCmd.ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// キー名（例：KEY_C、KEY_F5、KEY_ENTER）をWPFのKeyに変える。
    /// </summary>
    public static bool TryParseKey(string rawKeyName, out Key key)
    {
        // 先頭の「KEY_」を外して大文字にそろえる。
        key = Key.None;
        if (string.IsNullOrWhiteSpace(rawKeyName)) return false;

        string name = rawKeyName;
        if (name.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(4);
        }

        name = name.ToUpperInvariant();

        // 記号や別名で書けるキー。HELPはEnd、CLRはHome、ROLLUPはPageUpとして扱う。
        switch (name)
        {
            case "-":
            case "MINUS":
            case "OEMMINUS":
            case "SUBTRACT":
                key = Key.OemMinus;
                return true;
            case "\\":
            case "YEN":
            case "BACKSLASH":
            case "OEMBACKSLASH":
            case "OEM5":
            case "OEMPIPE":
                key = Key.OemBackslash;
                return true;
            case "ESC":
            case "ESCAPE":
                key = Key.Escape;
                return true;
            case "TAB":
                key = Key.Tab;
                return true;
            case "ENTER":
            case "RETURN":
            case "CR":
                key = Key.Return;
                return true;
            case "BS":
            case "BACKSPACE":
                key = Key.Back;
                return true;
            case "SPACE":
            case "SPC":
                key = Key.Space;
                return true;
            case "UP":
                key = Key.Up;
                return true;
            case "DOWN":
                key = Key.Down;
                return true;
            case "LEFT":
                key = Key.Left;
                return true;
            case "RIGHT":
                key = Key.Right;
                return true;
            case "INS":
            case "INSERT":
                key = Key.Insert;
                return true;
            case "DEL":
            case "DELETE":
                key = Key.Delete;
                return true;
            case "HOME":
            case "CLR":
                key = Key.Home;
                return true;
            case "END":
            case "HELP":
                key = Key.End;
                return true;
            case "PAGEUP":
            case "PGUP":
            case "ROLLUP":
                key = Key.PageUp;
                return true;
            case "PAGEDOWN":
            case "PGDN":
            case "ROLLDOWN":
                key = Key.PageDown;
                return true;
        }

        // 1文字の数字は数字キーとして扱う。
        if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
        {
            key = Key.D0 + (name[0] - '0');
            return true;
        }

        // A～Z、F1～F24などはKeyの名前として読む。
        if (Enum.TryParse<Key>(name, true, out var parsedKey))
        {
            key = parsedKey;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 修飾キーの欄（-sac、-s--、-s-c、-ac- など。順不同）をModifierKeysに変える。tは受け付けるが修飾キーとしては使わない。
    /// </summary>
    public static ModifierKeys ParseModifiers(string rawFlags)
    {
        if (string.IsNullOrWhiteSpace(rawFlags)) return ModifierKeys.None;

        ModifierKeys mods = ModifierKeys.None;
        foreach (char c in rawFlags)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 's':
                    mods |= ModifierKeys.Shift;
                    break;
                case 'a':
                    mods |= ModifierKeys.Alt;
                    break;
                case 'c':
                    mods |= ModifierKeys.Control;
                    break;
                case 't':
                    break;
            }
        }
        return mods;
    }

    /// <summary>
    /// コマンド名（&amp;filecopy;、&amp;exit; など）をAppCommandに変える。同じコマンドに複数の別名を認める。
    /// </summary>
    public static bool TryParseCommand(string rawCmd, out AppCommand command)
    {
        // 前後の「&amp;」「;」を外し、小文字にそろえる。
        command = AppCommand.None;
        if (string.IsNullOrWhiteSpace(rawCmd)) return false;

        string normalized = rawCmd.Trim().Trim('&', ';').ToLowerInvariant();

        // 名前と別名からコマンドを決める。incrimentalsearch はINIで使われている綴りなのでそのまま受け付ける。
        switch (normalized)
        {
            case "none": command = AppCommand.None; return true;
            case "help": command = AppCommand.Help; return true;
            case "mark": command = AppCommand.ToggleMark; return true;
            case "markall": command = AppCommand.MarkAll; return true;
            case "markclear": command = AppCommand.MarkClear; return true;
            case "incrimentalsearch":
            case "incrementalsearch": command = AppCommand.IncrementalSearch; return true;
            case "archive": command = AppCommand.Archive; return true;
            case "view": command = AppCommand.View; return true;
            case "refresh": command = AppCommand.Refresh; return true;
            case "filerename":
                command = AppCommand.FileRename;
                return true;
            case "textviewer":
            case "textview":
            case "txtview":
            case "viewer":
                command = AppCommand.TextViewer;
                return true;
            case "hexviewer":
                command = AppCommand.HexViewer;
                return true;
            case "videoviewer":
                command = AppCommand.VideoViewer;
                return true;
            case "graphicviewer":
            case "imageviewer":
            case "cgviewer":
            case "graphicview":
            case "imgview":
                command = AppCommand.GraphicViewer;
                return true;
            case "vgmplayer":
            case "vgmview":
            case "vgmviewer":
            case "musicplayer":
                command = AppCommand.VgmPlayer;
                return true;
            case "miniplayer":
            case "vgmplay":
            case "miniplay":
            case "bgmplay":
                command = AppCommand.MiniPlayer;
                return true;
            case "extexrc":
            case "extexec":
            case "extrun":
            case "ext":
                command = AppCommand.ExtExec;
                return true;
            case "extexecwin":
            case "extexrcwin":
            case "extwin":
                command = AppCommand.ExtExecWindows;
                return true;
            case "filecopy":
            case "copy":
                command = AppCommand.FileCopy;
                return true;
            case "filecopycheck":
            case "copycheck":
            case "cpcheck":
                command = AppCommand.FileCopyCheck;
                return true;
            case "filemove":
            case "move":
            case "mv":
                command = AppCommand.FileMove;
                return true;
            case "filemovecheck":
            case "movecheck":
            case "mvcheck":
                command = AppCommand.FileMoveCheck;
                return true;
            case "exit":
            case "quit":
                command = AppCommand.Exit;
                return true;
            case "drive":
            case "changedrive":
            case "change-drive":
            case "change-drive-menu":
            case "drivemenu":
            case "drive_menu":
                command = AppCommand.DriveMenu;
                return true;
            case "mkdir":
            case "makedir":
            case "md":
            case "createdir":
                command = AppCommand.MakeDir;
                return true;
            case "delete":
            case "del":
            case "remove":
            case "rm":
            case "filedelete":
                command = AppCommand.Delete;
                return true;
            case "sort":
            case "filesort":
            case "sortmenu":
                command = AppCommand.Sort;
                return true;
            case "pdfviewer":
            case "pdfview":
            case "pdf":
                command = AppCommand.PdfViewer;
                return true;
            case "syncpath":
            case "samepath":
            case "copypath":
            case "syncdir":
            case "samedir":
            case "syncpane":
            case "panesync":
            case "sync":
                command = AppCommand.SyncPanePath;
                return true;
            case "jumplist":
                command = AppCommand.JumpList;
                return true;
            case "chdir":
            case "cd":
            case "jump":
            case "path":
            case "changedir":
            case "changedirectory":
                command = AppCommand.ChangeDirectory;
                return true;
            case "opcancel":
            case "operationcancel":
                command = AppCommand.OperationCancel;
                return true;
            case "loglevel":
            case "log_level":
            case "log":
                command = AppCommand.LogLevel;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// キー割り当てをINIの1行に書き出す。
    /// </summary>
    public static string FormatLine(KeyBindingItem item)
    {
        // キー名を決める。数字・Enter・Esc・BS・マイナス・「\」は読み込みと対になる短い名前にする。
        string keyName = "KEY_" + item.Key.ToString().ToUpperInvariant();
        if (item.Key >= Key.D0 && item.Key <= Key.D9)
        {
            keyName = "KEY_" + (item.Key - Key.D0);
        }
        else if (item.Key == Key.Return)
        {
            keyName = "KEY_ENTER";
        }
        else if (item.Key == Key.Escape)
        {
            keyName = "KEY_ESC";
        }
        else if (item.Key == Key.Back)
        {
            keyName = "KEY_BS";
        }
        else if (item.Key == Key.OemMinus || item.Key == Key.Subtract)
        {
            keyName = "KEY_-";
        }
        else if (item.Key == Key.OemBackslash || item.Key == Key.Oem5 || item.Key == Key.OemPipe)
        {
            keyName = "KEY_\\";
        }

        // 修飾キーの欄を「-sac」の順で作る。
        char s = item.Modifiers.HasFlag(ModifierKeys.Shift) ? 's' : '-';
        char a = item.Modifiers.HasFlag(ModifierKeys.Alt) ? 'a' : '-';
        char c = item.Modifiers.HasFlag(ModifierKeys.Control) ? 'c' : '-';
        string flags = $"-{s}{a}{c}";

        // 外部プログラムはコマンド行をそのまま書く。
        if (item.Command == AppCommand.ExternalProgram)
            return $">{keyName,-10}\t{flags}\t{item.ExternalCommand};";

        // コマンド名は、代表名が決まっているものを除き読み込んだときの名前を使う。名前がなければ代表名を使う。
        string cmdName = item.Command switch
        {
            AppCommand.ToggleMark => "mark",
            AppCommand.Delete => "filedelete",
            AppCommand.ExtExec => "extexec",
            AppCommand.ExtExecWindows => "extexecwin",
            _ => item.RawCommandName
        };
        if (string.IsNullOrWhiteSpace(cmdName))
        {
            cmdName = item.Command switch
            {
                AppCommand.FileRename => "filerename",
                AppCommand.TextViewer => "textviewer",
                AppCommand.HexViewer => "hexviewer",
                AppCommand.GraphicViewer => "graphicviewer",
                AppCommand.VgmPlayer => "vgmplayer",
                AppCommand.MiniPlayer => "miniplayer",
                AppCommand.ExtExec => "extexec",
                AppCommand.ExtExecWindows => "extexecwin",
                AppCommand.FileCopy => "filecopy",
                AppCommand.FileCopyCheck => "filecopycheck",
                AppCommand.FileMove => "filemove",
                AppCommand.FileMoveCheck => "filemovecheck",
                AppCommand.Exit => "exit",
                AppCommand.DriveMenu => "drive",
                AppCommand.MakeDir => "mkdir",
                AppCommand.ToggleMark => "mark",
                AppCommand.MarkAll => "markall",
                AppCommand.MarkClear => "markclear",
                AppCommand.IncrementalSearch => "incrimentalsearch",
                AppCommand.Archive => "archive",
            AppCommand.Delete => "filedelete",
                AppCommand.Sort => "sort",
                AppCommand.PdfViewer => "pdfviewer",
                AppCommand.SyncPanePath => "samepath",
                AppCommand.ChangeDirectory => "chdir",
                AppCommand.JumpList => "jumplist",
                AppCommand.OperationCancel => "opcancel",
                _ => item.Command.ToString().ToLowerInvariant()
            };
        }

        return $">{keyName,-10}\t{flags}\t&{cmdName};";
    }

    /// <summary>
    /// 拡張子関連付けの行（.txt ---- &amp;textviewer;）を読む。修飾キーの欄は省略できる。
    /// </summary>
    public static bool TryParseExtBinding(string line, out ExtBindingItem? item)
    {
        // 「.」で始まり、拡張子とコマンドの2列以上ある行だけを対象にする。
        item = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        string trimmed = line.Trim();
        if (!trimmed.StartsWith('.')) return false;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;

        string ext = tokens[0].ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;

        // 後ろから「&amp;」で始まるか「;」で終わる列を探してコマンドとする。なければ最後の列を使う。
        string? rawCmd = null;
        for (int i = tokens.Length - 1; i >= 1; i--)
        {
            if (tokens[i].StartsWith('&') || tokens[i].EndsWith(';'))
            {
                rawCmd = tokens[i];
                break;
            }
        }

        if (rawCmd is null)
        {
            rawCmd = tokens[^1];
        }

        // コマンド名が読めれば、3列以上のときだけ2列目を修飾キーとして記録する。
        string cleanCmd = rawCmd.Trim().Trim('&', ';');
        if (!TryParseCommand(cleanCmd, out var cmd))
        {
            return false;
        }

        item = new ExtBindingItem(ext, cmd, cleanCmd.ToLowerInvariant(), tokens.Length >= 3 ? ParseModifiers(tokens[1]) : ModifierKeys.None);
        return true;
    }

    /// <summary>
    /// 拡張子関連付けをINIの1行に書き出す。
    /// </summary>
    public static string FormatExtBinding(ExtBindingItem item)
    {
        char s = item.Modifiers.HasFlag(ModifierKeys.Shift) ? 's' : '-';
        char a = item.Modifiers.HasFlag(ModifierKeys.Alt) ? 'a' : '-';
        char c = item.Modifiers.HasFlag(ModifierKeys.Control) ? 'c' : '-';
        string name = item.Command switch
        {
            AppCommand.ToggleMark => "mark",
            AppCommand.Delete => "filedelete",
            AppCommand.ExtExec => "extexec",
            AppCommand.ExtExecWindows => "extexecwin",
            _ => item.RawCommandName
        };
        return $"{item.Extension,-10}\t-{s}{a}{c}\t&{name};";
    }
}
