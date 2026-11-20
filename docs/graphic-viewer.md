# 画像ビューア

画像ファイルを全画面で表示し、拡大・縮小・回転・左右反転・背景の切替をしながら、同じフォルダーの画像を順にめくって見られる。
通常のフォルダーとアーカイブの中の両方が対象。PDF互換で保存した Adobe Illustrator（.ai）も表示できる。

## 対応形式

| 形式 | 拡張子 | 読み込み |
| :--- | :--- | :--- |
| PNG・JPEG・BMP・GIF・ICO・TIFF・JPEG XR | .png / .jpg / .jpeg / .bmp / .gif / .ico / .tif / .tiff / .wdp / .jxr | Windows の機能 |
| WebP・AVIF・PSD・DDS・EXR | .webp / .avif / .psd / .dds / .exr | 内蔵の画像ライブラリ |
| HEIC・HEIF | .heic / .heif | Windows にインストールされた対応コーデック |
| Adobe Illustrator | .ai | PDF互換の部分を画像にして表示（後述） |

- HEIC/HEIF は、Windows に対応コーデック（HEIF 画像拡張機能など）がないと表示できない。そのときは案内を表示する。MirrorFlux からコーデックをインストールすることはない。
- 読み込めない画像は、ビューア内に「画像を読み込めませんでした」と理由を表示する。
- 音楽プレイヤーのアルバム画像も同じ形式に対応する。

## 起動と拡張子設定

ファイル一覧で `G`（`&graphicviewer;`）を押すと、カーソル位置のファイルを画像ビューアで開く。
Enterは `[extensions]` セクションの設定に従い、標準では次の拡張子を画像ビューアで開く。

.ai / .png / .jpg / .jpeg / .bmp / .gif / .ico / .webp / .heic / .heif / .psd / .tif / .tiff

.avif / .dds / .exr / .wdp / .jxr は表示に対応しているが、標準ではEnterに割り当てていない。`G` で開くか、`[extensions]` セクションに追加する。
追加・変更したINIはアプリの再起動で反映される。

```ini
[extensions]
.png  ---- &graphicviewer;
.avif ---- &graphicviewer;
# 標準拡張子の割り当てを無効にする例:
.gif  ---- &none;
```

アーカイブの中の画像も開ける。同じフォルダーの他の画像は先に取り出しておくので、すぐに次の画像へめくれる。

## 操作

| 操作 | キー | Fキーの命令 |
| :--- | :--- | :--- |
| 操作一覧の表示 | F1 | &help; |
| ウィンドウに合わせる ⇔ 等倍 | F2 / ダブルクリック | &fit; |
| ウィンドウに合わせる | 0 | |
| 等倍 | 1 | |
| 前の画像 | F3 / ← / ↑ / BackSpace / P | &previous; |
| 次の画像 | F4 / → / ↓ / Space / N | &next; |
| 最初・最後の画像 | Home / End | |
| 拡大・縮小 | PageUp・PageDown / + ・ - / マウスホイール | |
| 表示位置の移動 | Shift+矢印 / 左ドラッグ | |
| Photoshopで開く | F5 | &photoshop; |
| 左右反転 | F6 / H | &flip; |
| 90度右に回転 | F7 / R | &rotate; |
| 背景の切替（黒 → 市松模様 → 白） | F8 / B | &background; |
| 再読み込み | F9 | &reload; |
| Windowsの関連付けで開く | F10 | &associated; |
| 閉じる | F12 / Enter / Esc / Q / V | &close; |

- F1〜F12 と Shift+F1〜F12 は `[keys.graphic]` セクションで変更できる。初期状態では Shift 付きも通常と同じ命令で、F11 は割り当てなし。ファンクションキーの表示とクリックも同じ設定に従う。表示名は `%gvfn01`〜`%gvfn12`（`%gvfn11` はない）。
- Fキー以外のキー（矢印・Space・数字など）は固定で、INIでは変更できない。
- 前後の画像は、同じフォルダーの対応形式のファイルを名前の自然順（1, 2, 10 の順）に並べてめくる。最後の次は最初に戻る。
- 拡大・縮小は1回で1.25倍ずつ、5%〜3200%の範囲。マウスホイールではカーソル位置、キーでは画面中央を基準にする。ウィンドウに合わせる表示では、小さい画像を等倍より大きくしない。
- 回転・反転・背景は次の画像に移っても保つ。ウィンドウに合わせた表示のまま回転すると、回転後の向きで合わせ直す。
- Photoshopは、レジストリに登録されたインストール先から起動する。見つからなければメッセージを表示する。

```ini
[keys.graphic]
>KEY_F5  ---- &associated;
>KEY_F11 ---- &background;
>KEY_F5  -s-- &photoshop;
```

## 画面の表示

下部のステータスに、ファイル名・形式と縦横のピクセル数・ファイルサイズ・倍率（`[Fit: 50%]` または `[100%]`）を表示する。
右側にはフォルダー内の位置を `Img 3/10  [30%]` のように表示する。AIファイルでは `Artboard 1/2` も表示する。

## Adobe Illustrator（.ai）

PDF互換で保存した `.ai` を画像ビューアで表示する。Illustrator や Ghostscript のインストールは不要。通常フォルダーとアーカイブ内の両方が対象。
`mf.ini` に `.ai` の設定がなければ、Enter で画像ビューアが開く。

- F3/F4・左右キーでアートボードを順に移動する。最後／最初からさらに進むと次／前の画像ファイルへ移動し、前のAIに戻ると最後のアートボードを表示する。フォルダーにAIが1つだけならアートボード内で循環する。
- F9の再読み込みは先頭のアートボードに戻る。

- 表示するのは、ファイルに保存された PDF 側のアートボード。アートボードの外のオブジェクト、Illustrator 専用の編集データ、レイヤーの切り替えには対応しない。
- 古い PostScript 形式や、PDF互換なしで保存した AI は表示できない。Illustrator の保存設定「PDF互換ファイルを作成」を有効にするよう案内する。PDF 側に互換なしの説明ページしかないファイルは、その説明ページが表示される。
- 背景は白いページとして表示する。縦横とも最大 4096 ピクセルの画像にするので、大きく拡大すると粗くなる。

参考: [Adobe公式・Illustrator AI形式での保存](https://helpx.adobe.com/illustrator/desktop/save-and-export/export-files-to-different-formats/save-artwork-in-illustrator-ai-format.html)

