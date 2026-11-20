======================================================================
  MirrorFlux (MF)
  キーボードで操作する Windows 向け2画面ファイラー
======================================================================

MirrorFlux は、キーボードだけで素早く操作できる2画面ファイラーです。
ファイル操作、アーカイブの閲覧と編集、テキスト・画像・PDF・動画の表示、
ゲーム音楽の再生を1つのアプリで行えます。


----------------------------------------------------------------------
 1. 動作環境
----------------------------------------------------------------------

  Windows 10 / Windows 11（x64）
  .NET ランタイムは mf.exe に含まれています。インストールは不要です。


----------------------------------------------------------------------
 2. 使い方
----------------------------------------------------------------------

  (1) この ZIP を好きなフォルダーに展開します。
  (2) mf.exe を起動します。
  (3) F1 キーでキー操作の一覧を表示します。

  設定は mf.exe と同じフォルダーの mf.ini に保存されます。
  レジストリは使いません。キーの割り当てや色も mf.ini で変更できます。


----------------------------------------------------------------------
 3. 主な機能
----------------------------------------------------------------------

  ・2画面のファイル一覧と、確認付きのコピー・移動・削除
  ・ZIP / LZH / 7Z / RAR / TAR などをフォルダーのように開く
    （ZIP / LZH / 7Z / TAR は中身の追加・削除にも対応）
  ・テキスト・画像・PDF・動画のビューア
  ・VGM / S98 / MDX / NSF / SPC などのゲーム音楽の再生
  ・NanoDrive 6 / 7 / 8 での実機再生
    （mf.ini の [GME] セクションで有効にします）


----------------------------------------------------------------------
 4. 詳しいマニュアル
----------------------------------------------------------------------

  キー操作・設定・各ビューア・NanoDrive の使い方などの詳しい説明は、
  次のページにあります。

    日本語  https://github.com/katsuemon/MirrorFlux-public/blob/main/README.md
    English https://github.com/katsuemon/MirrorFlux-public/blob/main/README.en.md


----------------------------------------------------------------------
 5. ROM などの外部データ
----------------------------------------------------------------------

  ROM・波形データは含めていません。必要な場合はご自身で用意してください。
  ファイル名と置き場所は EXTERNAL-DATA.txt を参照してください。


----------------------------------------------------------------------
 6. 免責事項
----------------------------------------------------------------------

  ・MirrorFlux は無保証で提供しています。作者は、要望・質問・不具合報告
    に対応する義務を負いません。
  ・MirrorFlux を使ったこと、または使えなかったことによって生じたいかなる
    損害・不利益（データの消失や機器の不調を含みます）についても、作者は
    責任を負いません。ファイル操作や NanoDrive での再生は、ご自身の判断と
    責任で行ってください。


----------------------------------------------------------------------
 7. ライセンス
----------------------------------------------------------------------

  MirrorFlux は GPL-3.0-or-later で公開しています。

    LICENSE.txt              GPL-3.0 の全文
    COPYRIGHT.txt            著作権と適用範囲
    THIRD_PARTY_NOTICES.md   同梱している第三者の部品とそのライセンス
    licenses\                第三者の部品のライセンス文と通知

  ソースコード: https://github.com/katsuemon/MirrorFlux-public


----------------------------------------------------------------------
 8. 謝辞
----------------------------------------------------------------------

  MirrorFlux の制作は mint、mmdsp、fmdsp、hoot、vgmplay などから
  多大な影響を受けており、これらを生み育ててきた皆さまに
  深い敬意と感謝を表します。


======================================================================
  MirrorFlux (MF) - English
  A keyboard-driven two-pane file manager for Windows
======================================================================

MirrorFlux is a two-pane file manager you can drive quickly with the
keyboard alone. File operations, browsing and editing archives, viewing
text, images, PDFs and videos, and playing game music all happen in one
app.


----------------------------------------------------------------------
 1. Requirements
----------------------------------------------------------------------

  Windows 10 / Windows 11 (x64)
  The .NET runtime is built into mf.exe. No installation is needed.


----------------------------------------------------------------------
 2. Getting started
----------------------------------------------------------------------

  (1) Unzip this archive into any folder.
  (2) Run mf.exe.
  (3) Press F1 to see the list of keys.

  Settings are saved in mf.ini in the same folder as mf.exe. The
  registry is not used. Keys and colors can also be changed in mf.ini.


----------------------------------------------------------------------
 3. Main features
----------------------------------------------------------------------

  - Two-pane file lists; copy, move and delete with confirmation
  - Open ZIP / LZH / 7Z / RAR / TAR and more like folders
    (ZIP / LZH / 7Z / TAR also support adding and deleting entries)
  - Viewers for text, images, PDFs and videos
  - Game music playback: VGM / S98 / MDX / NSF / SPC and more
  - Playback on real NanoDrive 6 / 7 / 8 hardware
    (enable it in the [GME] section of mf.ini)


----------------------------------------------------------------------
 4. Full manual
----------------------------------------------------------------------

  Keys, settings, each viewer, NanoDrive and more are described in
  detail here:

    English https://github.com/katsuemon/MirrorFlux-public/blob/main/README.en.md
    日本語  https://github.com/katsuemon/MirrorFlux-public/blob/main/README.md


----------------------------------------------------------------------
 5. ROMs and other external data
----------------------------------------------------------------------

  No ROM or waveform data is included. If you need any, provide your
  own. See EXTERNAL-DATA.txt for file names and where to put them.


----------------------------------------------------------------------
 6. Disclaimer
----------------------------------------------------------------------

  - MirrorFlux is provided without any warranty. The author has no
    obligation to respond to requests, questions or bug reports.
  - The author is not liable for any damage or loss arising from
    using, or being unable to use, MirrorFlux (including lost data or
    hardware trouble). File operations and NanoDrive playback are at
    your own risk.


----------------------------------------------------------------------
 7. License
----------------------------------------------------------------------

  MirrorFlux is released under GPL-3.0-or-later.

    LICENSE.txt              Full text of GPL-3.0
    COPYRIGHT.txt            Copyright and scope
    THIRD_PARTY_NOTICES.md   Bundled third-party components and their licenses
    licenses\                License texts and notices of those components

  Source code: https://github.com/katsuemon/MirrorFlux-public

----------------------------------------------------------------------
 8. Acknowledgements
----------------------------------------------------------------------

  MirrorFlux owes much to mint, mmdsp, fmdsp, hoot, vgmplay, and
  others, and is made with deep respect and gratitude for their
  creators and communities.
