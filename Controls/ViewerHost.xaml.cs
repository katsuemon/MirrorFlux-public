using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

namespace MirrorFlux.Controls;

/// <summary>
/// ビューアの入れ物。ファイルに合うビューアを選んで表示し、キー操作・ステータス・閉じる要求を中継する。
/// </summary>
public partial class ViewerHost : UserControl
{
    private IViewerControl? _currentViewer;

    public IViewerControl? CurrentViewer => _currentViewer;
    public bool IsActive => _currentViewer is not null;
    public bool IsPromptActive => _currentViewer?.IsPromptActive ?? false;
    public bool IsGraphicViewer => _currentViewer is GraphicViewerControl;
    public bool IsVideoViewer => _currentViewer is VideoViewerControl;
    public bool IsPdfViewer => _currentViewer is PdfViewerControl;
    public bool IsMusicPlayerViewer => _currentViewer is MusicPlayerControl;

    public event EventHandler? StatusChanged;
    public event EventHandler? CloseRequested;
    public event Action<int>? TextFileNavigationRequested;

    public ViewerHost()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 拡張子・内容・指定に応じたビューアでファイルを開く。開けなければ false を返す。
    /// 判定の順は、動画、サウンドビューア、PDF、画像、テキスト（HEX を含む）。
    /// </summary>
    public bool Open(
        string filePath,
        bool forceGraphicViewer = false,
        bool forceTextViewer = false,
        bool forceVgmPlayer = false,
        bool forcePdfViewer = false,
        bool forceHexViewer = false,
        FilePaneViewModel? sourcePane = null,
        bool forceVideoViewer = false,
        Models.AppSettings? settings = null)
    {
        // HEX 指定はテキストビューア指定を兼ねる。前のビューアを閉じてからファイルを確かめる。
        forceTextViewer |= forceHexViewer;
        CloseViewer();

        if (!File.Exists(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 修飾キーなしの拡張子関連付けが動画ビューアになっているかを調べる。
        IViewerControl viewer;
        settings ??= new Models.AppSettings();
        bool configuredVideo = settings.ExtBindings.FirstOrDefault(b => b.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)
            && b.Modifiers == ModifierKeys.None)?.Command == Input.AppCommand.VideoViewer;
        // 動画ビューア。指定されたか、他の指定がなく拡張子が動画ビューアに関連付けられているとき。
        if (forceVideoViewer || (!forceTextViewer && !forceGraphicViewer && !forcePdfViewer && !forceVgmPlayer && configuredVideo))
        {
            var videoViewer = new VideoViewerControl(settings.ViewerKeyBindings, settings.MasterVolumePercent);
            if (!videoViewer.Load(filePath)) { videoViewer.Close(); return false; }
            viewer = videoViewer;
        }
        // サウンドビューア。再生できる音楽ファイルのとき。指定されたのに再生できなければ失敗とする。
        else if ((forceVgmPlayer || (!forceTextViewer && !forceGraphicViewer && !forcePdfViewer)) &&
            Services.Music.MusicPlaybackService.Instance.CanOpenFullViewer(filePath))
        {
            var musicPlayer = new MusicPlayerControl(Services.Music.MusicPlaybackService.Instance, sourcePane, settings.ViewerKeyBindings);
            if (!musicPlayer.Load(filePath))
            {
                return false;
            }
            viewer = musicPlayer;
        }
        else if (forceVgmPlayer)
        {
            return false;
        }
        // PDF ビューア、画像ビューアの順に拡張子で判定し、どれでもなければテキストビューアで開く。
        else if (forcePdfViewer || (!forceTextViewer && !forceGraphicViewer && PdfViewerControl.IsSupportedExtension(ext)))
        {
            var pdfViewer = new PdfViewerControl();
            if (!pdfViewer.Load(filePath))
            {
                return false;
            }
            viewer = pdfViewer;
        }
        else if (forceGraphicViewer || (!forceTextViewer && GraphicViewerControl.IsSupportedExtension(ext)))
        {
            var graphicViewer = new GraphicViewerControl();
            if (!graphicViewer.Load(filePath))
            {
                return false;
            }
            viewer = graphicViewer;
        }
        else
        {
            var textViewer = new TextViewerControl();
            if (!textViewer.Load(filePath, forceHexViewer ? true : forceTextViewer ? false : null))
            {
                return false;
            }
            textViewer.FileNavigationRequested += direction => TextFileNavigationRequested?.Invoke(direction);
            viewer = textViewer;
        }

        // ビューアのイベントを中継し、入れ物に置き換えて表示する。
        _currentViewer = viewer;
        viewer.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        viewer.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        ContainerGrid.Children.Clear();
        if (viewer is UserControl uc)
        {
            ContainerGrid.Children.Add(uc);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>表示中のビューアを閉じて入れ物から外し、状態の変化を知らせる。</summary>
    public void CloseViewer()
    {
        if (_currentViewer is not null)
        {
            _currentViewer.Close();
            _currentViewer = null;
            ContainerGrid.Children.Clear();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HandleKeyDown(KeyEventArgs e)
    {
        return _currentViewer?.HandleKeyDown(e) ?? false;
    }

    public void ExecuteFunctionKey(int keyNumber)
    {
        _currentViewer?.ExecuteFunctionKey(keyNumber);
    }
}
