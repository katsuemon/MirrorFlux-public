# テキスト／HEXビューア

テキストファイルを表示する。同じ画面で HEX（16進ダンプ）表示に切り替えられる。読み取り専用で、ファイルは書き換えない。
構文の色分け、検索、文字コードの切り替え、更新されたファイルの自動再読み込み、全体を見渡すミニマップを備える。

## 開き方

ファイル一覧で、標準では次のように開く。

- **V**（`&textviewer;`）: テキスト表示で開く。
- **Shift+V**（`&hexviewer;`）: HEX 表示で開く。
- **Enter**: `.txt`・`.ini`・`.c`・`.cpp`・`.h`・`.doc`・`.man` など、`[extensions]` セクションでテキストビューアに割り当てた拡張子を開く。

```ini
[extensions]
.cfg ---- &textviewer;
```

アーカイブの中のファイルは、一時フォルダーへ取り出して表示する。

## 画面

- **本文**: 折り返しは初期状態で有効。行番号を表示できる。カーソル位置の1文字は色を反転して表示する。
- **改行の印**: 行末に `CR`・`LF`（CRLF は上下に2段）の小さな印を表示する。コピーしたときは元の改行になる。
- **URL**: `http://`・`https://`・`ftp://`・`www.` で始まる文字列を色付けする。Ctrl+クリックで開く。
- **ミニマップ**: 右端に全体の縮図、今見ている範囲、検索で見つかった位置を表示する。クリック・ドラッグで移動できる。
- **ステータス**: 下部に行・桁、カーソル位置の文字・Unicode のコードポイント・今の文字コードでのバイト列（例 `文字:「日」 U+65E5 UTF-8:E6 97 A5`）、色分けの状態（`Syntax : ON` / `OFF`）を表示する。空白・TAB・改行は名前で、ファイルの終わりは `EOF` と表示する。
- **Markdown**: 小さい `.md` ファイルは見出しや箇条書きを整えた表示で開く。M で元のテキストと切り替える。

## 操作（テキスト表示）

| 操作 | キー | Fキーの命令 |
| :--- | :--- | :--- |
| 操作一覧の表示 | F1 | &help; |
| 検索欄を開く（Esc で閉じる） | F2 / Ctrl+F / / | &search; |
| 次・前の一致へ | F3 / F4、検索欄で Enter / Shift+Enter | &findnext; / &findprevious; |
| 文字コードの切替 | F5 / C | &encoding; |
| HEX 表示へ | F6 / H | &hex; |
| 行へ移動 | F7 / Ctrl+G / G | &jump; |
| 行番号の表示 | F8 / N | &linenumbers; |
| 再読み込み | F9 | &reload; |
| 折り返しの切替 | F10 / W | &wrap; |
| ミニマップの表示 | F11 | &minimap; |
| 閉じる | F12 | &close; |
| ファイルの末尾へ追従 | Shift+F2 / T | &follow; |
| 正規表現・大文字小文字の区別 | Shift+F3 / Shift+F4 | &regex; / &matchcase; |
| 前・次のファイルを開く | Shift+F5 / Shift+F6 | &previousfile; / &nextfile; |
| 選択をコピー | Shift+F8 | &copy; |
| 色分けの切替 | Shift+F10 | &syntax; |
| Windows の関連付けで開く | E | |
| 文書の先頭・末尾 | Home / End | |
| 範囲選択 | ドラッグ / Shift+移動キー | |
| 全選択・コピー | Ctrl+A / Ctrl+C（選択がなければ今の行） | |

- F1〜F12 と Shift+F1〜F12 は `[keys.text]` セクション（HEX 表示は `[keys.hex]` セクション）で変更できる。ファンクションキーの表示名は `%vfn01`〜`%vfn12`。
- Shift を押している間は、画面下部のファンクションキーの表示が Shift 付きの機能に変わる。押したままクリックしても同じ機能になる。
- 前・次のファイルは、操作中のパネルの並び順でフォルダーを飛ばしてめくる。閉じると、最後に見ていたファイルにファイル一覧のカーソルが移る。
- 検索は正規表現と大文字小文字の区別を切り替えられ、一致した件数を表示する。検索中は Markdown の整えた表示から元のテキストに戻る。
- E で開いた外部アプリで保存すると、自動再読み込みで表示も更新される。アーカイブの中のファイルは、取り出したものを外部アプリに渡す。

## HEX 表示

F1〜F4・F9・F11・F12 はテキスト表示と同じ。違うキーは次のとおり。

| 操作 | キー | Fキーの命令 |
| :--- | :--- | :--- |
| 文字コードを切り替えてテキスト表示へ | F5 | &encoding; |
| テキスト表示へ | F6 | &text; |
| アドレスへ移動 | F7 | &jump; |
| 末尾へ追従 | Shift+F2 | &follow; |
| バイト列で検索の切替 | Shift+F5 | &bytesearch; |
| 1行のバイト数（8 / 16 / 32） | Shift+F6 | &rowwidth; |
| 数値のバイト順（Endian） | Shift+F7 | &endian; |
| 16進のバイト列でコピー・文字列でコピー | Shift+F8 / Shift+F9 | &copybytes; / &copytext; |

- アドレスは `0x1234`・`$1234`・`1234`（16進）、`d:1234`（10進）で指定できる。
- バイト列で検索するときは `89 50 ?? 47` の形で書く。`??` は任意の1バイト。切り替えていないときは、今の文字コードでの文字列として検索する。
- 16進の列と文字の列のどちらからでも範囲を選べ、両方が連動して光る。Ctrl+C は選んだ側の形式、Ctrl+Shift+C は16進のバイト列でコピーする。右クリックからも形式を選んでコピーできる。
- 下部に、選んだ位置から読んだ 8 / 16 / 32 / 64 ビットの整数を表示する。

## 文字コード

開いたときに、BOM・UTF-8・Shift_JIS・EUC-JP を自動で判定する。判定が合わないときは F5 で切り替える。

## ファイルの更新

表示中のファイルが外で更新されると、自動で読み直す。読んでいる位置はそのまま。追記・切り詰め・同じ名前のファイルへの置き換えにも対応する。ログファイルなどを追うときは Shift+F2 で末尾へ追従する。

## 大きなファイル

大きなファイルも全体を読み込んで、全体を検索できる。ファイル全体をメモリーに読むため、扱える大きさは PC のメモリー次第。2GB 以上のファイルは開かない。

## 構文の色分け

INI・JSON・XML・C#・C/C++ の色分けを内蔵している。拡張子ごとの構文は `mf.ini` で決める。

```ini
# 色分けを使う 1 / 使わない 0
%tvHighlight 1
# 独自の構文ファイル（.xshd）を置くフォルダー
%tvSyntaxPath syntax
# 拡張子ごとの構文（auto は中身から JSON / XML / INI を選ぶ、none は色分けしない）
%tvSyntax_ini ini
%tvSyntax_cfg auto
%tvSyntax_json json
%tvSyntax_xml xml
%tvSyntax_cs csharp
%tvSyntax_txt none
# 色
%tvColorComment #808CA8
%tvColorKeyword #B3A2FF
%tvColorString #E8B887
%tvColorNumber #99C8F0
%tvColorKey #85CDE0
%tvColorSection #D0B2F5
%tvColorUrl #69C9FF
```

- `syntax` フォルダーに `<構文名>.xshd` を置くと、内蔵の定義より優先する（例 `syntax/ini.xshd`）。独自の名前なら `%tvSyntax_abc custom` と `syntax/custom.xshd` を組み合わせる。XSHD の中の色は、その定義のものを使う。読めない XSHD は色分けせずに表示する。
- XSHD の書き方は [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) を参照。

### フォルダーやファイル名で構文を選ぶ

拡張子より先に、パスやファイル名の先頭で構文を決められる。

```ini
# パスのパターン（* は同じ階層の任意の文字列、? は1文字）。上から順に調べ、最初に一致したものを使う
%tvSyntaxRule "D:\Scenario\*.txt" scenario
# ファイル名の先頭（大文字と小文字は区別しない）。複数書ける
%tvSyntaxPrefix "reSG" scenario
```

優先順位は、パスのパターン、最も長く一致したファイル名の先頭、拡張子の順。`none` を指定すると、その範囲だけ色分けしない。

### シナリオの色分け（scenario）

ゲームのシナリオ用の構文。音声番号（黄色）、行頭の話者（水色・太字）、`//` のコメント、`｛親字｝［読み］` のルビの読みを色分けする。話者は `〔話者〕` と `＠話者名「台詞」` の両方に対応する。音声番号の色は `%tvColorScenarioVoice` で変えられる。

### 検索の色

検索で一致した部分と、今の一致の色は `%theme_...` の行で変えられる。範囲選択はファイル一覧のマークと同じ色になる。

```ini
%theme_SearchBackground #FFD54F
%theme_SearchForeground #101018
%theme_SearchCurrentBackground #FF8C42
%theme_SearchCurrentForeground #101018
```
