using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using MirrorFlux.Models;
namespace MirrorFlux.Services;

/// <summary>
/// テキストビューアーの構文ハイライトを決めて適用する。色は INI のテキストビューアー用の色に置き換える。
/// </summary>
public static class ViewerSyntaxService
{
    /// <summary>
    /// ファイルに使う構文の名前を決める。規則、ファイル名の先頭、拡張子の順に調べ、「auto」なら中身の先頭から JSON・XML・INI を推定する。
    /// </summary>
    public static string Resolve(string path, string sample, AppSettings settings)
    {
        // 規則・長い接頭辞・拡張子の割り当ての順に構文を決める。
        var rule = settings.TVSyntaxRules.FirstOrDefault(candidate => candidate.Matches(path));
        string extension = Path.GetExtension(path).TrimStart('.');
        string fileName = Path.GetFileName(path);
        var prefix = settings.TVSyntaxPrefixes.OrderByDescending(pair => pair.Key.Length).FirstOrDefault(pair =>
            pair.Key.Length > 0 && fileName.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase));
        string syntax = (rule?.Syntax ?? prefix.Value ?? settings.TVSyntaxMappings.GetValueOrDefault(extension, "none")).ToLowerInvariant();
        if (syntax != "auto") return syntax;
        // 「auto」は中身の最初の文字で判断する。
        string text = sample.TrimStart('\ufeff', ' ', '\t', '\r', '\n');
        if (text.StartsWith('{') || Regex.IsMatch(text, @"^\[\s*(?:[""\[\{0-9-]|true\b|false\b|null\b|\])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return "json";
        if (text.StartsWith('<')) return "xml";
        return "ini";
    }
    /// <summary>
    /// エディターに構文ハイライトを設定する。利用者が用意した .xshd があればそれを使い、なければ内蔵の定義の色を INI の色に置き換えて使う。
    /// </summary>
    public static void Apply(TextEditor editor, string path, AppSettings? settings = null)
    {
        // ハイライトが無効、または構文名が不正なら何もしない。判定には先頭 8192 文字を使う。
        settings ??= new SettingsStore().Load().Settings;
        editor.SyntaxHighlighting = null;
        if (!settings.TVHighlight) return;
        string syntax = Resolve(path, editor.Document.GetText(0, Math.Min(8192, editor.Document.TextLength)), settings);
        if (syntax == "none" || !Regex.IsMatch(syntax, "^[a-z0-9_-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return;
        try
        {
            // 構文フォルダーに利用者の定義があればそれを読む。DTD は禁止し、大きさも制限する。
            string directory = Environment.ExpandEnvironmentVariables(settings.TVSyntaxPath);
            if (!Path.IsPathRooted(directory)) directory = Path.Combine(SettingsStore.AppDirectory, directory);
            string custom = Path.Combine(directory, syntax + ".xshd");
            if (File.Exists(custom))
            {
                using var reader = XmlReader.Create(custom, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
                editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance); return;
            }
            // 内蔵の定義を探す。INI とシナリオはアプリ、それ以外は AvalonEdit の埋め込みリソースにある。
            string? resource = syntax switch { "scenario" => "scenario.xshd", "ini" => "ini.xshd", "json" => "Json.xshd", "xml" => "XML-Mode.xshd", "csharp" => "CSharp-Mode.xshd", "cpp" or "c" => "CPP-Mode.xshd", _ => null };
            if (resource == null) return;
            var assembly = syntax is "ini" or "scenario" ? typeof(ViewerSyntaxService).Assembly : typeof(TextEditor).Assembly;
            string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + resource, StringComparison.Ordinal));
            if (name == null) return;
            using var stream = assembly.GetManifestResourceStream(name)!;
            var document = XDocument.Load(stream);
            // 定義の各色を、色名からコメント・文字列・数値・セクション・キー・キーワードのどれかに当てはめて INI の色にする。
            // 記号と本文の色、読めない色は既定の文字色にする。
            foreach (var node in document.Descendants().Where(n => n.Name.LocalName == "Color"))
            {
                string colorName = ((string?)node.Attribute("name") ?? "").ToLowerInvariant();
                string value = syntax == "scenario" && colorName == "voice" ? settings.TVColorScenarioVoice :
                    syntax == "scenario" && colorName == "speaker" ? settings.TVColorKey :
                    syntax == "scenario" && colorName == "rubyreading" ? settings.TVColorSection :
                    colorName.Contains("comment") ? settings.TVColorComment : colorName.Contains("string") || colorName == "char" ? settings.TVColorString :
                    colorName.Contains("number") ? settings.TVColorNumber : colorName.Contains("section") ? settings.TVColorSection :
                    colorName.Contains("key") && !colorName.Contains("keyword") || colorName.Contains("field") || colorName.Contains("attribute") ? settings.TVColorKey : settings.TVColorKeyword;
                if (colorName.Contains("punctuation") || colorName.Contains("text")) value = "#C4C8DA";
                try { _ = ColorConverter.ConvertFromString(value); } catch (FormatException) { value = "#C4C8DA"; }
                node.SetAttributeValue("foreground", value);
            }
            using var xml = document.CreateReader(); editor.SyntaxHighlighting = HighlightingLoader.Load(xml, HighlightingManager.Instance);
        }
        // 任意で置く定義が壊れていても、ハイライトなしでファイルは表示できるようにする。
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or HighlightingDefinitionInvalidException or ArgumentException)
        { editor.SyntaxHighlighting = null; }
    }
}
