using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GameCrashKit.App.Rendering;
using GameCrashKit.Core.Exporters;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;

namespace GameCrashKit.App.ViewModels;

public sealed class SharePreviewViewModel : ViewModelBase
{
    private readonly DiagnosisReport _report;
    private readonly ScrubContext _scrubContext;

    private bool _hideAppName;
    private bool _hideExactTimestamp;
    private bool _includeScrubbedErrorExcerpt;
    private string _markdownText = string.Empty;
    private string _privacyStatusText = string.Empty;
    private byte[]? _currentPngBytes;
    private BitmapImage? _previewImage;
    private string _pngDimensionsText = string.Empty;
    private bool _hasPngRenderError;
    private string _pngRenderErrorMessage = string.Empty;
    private string _actionFeedbackText = string.Empty;

    public SharePreviewViewModel(DiagnosisReport report, ScrubContext? scrubContext = null)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _scrubContext = scrubContext ?? new ScrubContext(
            Environment.UserName,
            Environment.MachineName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        CopyMarkdownCommand = new RelayCommand(ExecuteCopyMarkdown);
        RetryPngRenderCommand = new RelayCommand(RebuildOutputs);

        RebuildOutputs();
    }

    public bool HideAppName
    {
        get => _hideAppName;
        set
        {
            if (SetProperty(ref _hideAppName, value))
            {
                RebuildOutputs();
            }
        }
    }

    public bool HideExactTimestamp
    {
        get => _hideExactTimestamp;
        set
        {
            if (SetProperty(ref _hideExactTimestamp, value))
            {
                RebuildOutputs();
            }
        }
    }

    public bool CanIncludeExcerpt =>
        _report.SelectedIncident?.ConfirmedEngineLog?.RawErrorExcerptLines is { Count: > 0 };

    public bool IncludeScrubbedErrorExcerpt
    {
        get => _includeScrubbedErrorExcerpt;
        set
        {
            if (SetProperty(ref _includeScrubbedErrorExcerpt, value))
            {
                RebuildOutputs();
            }
        }
    }

    public string MarkdownText
    {
        get => _markdownText;
        private set => SetProperty(ref _markdownText, value);
    }

    public string PrivacyStatusText
    {
        get => _privacyStatusText;
        private set => SetProperty(ref _privacyStatusText, value);
    }

    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public string PngDimensionsText
    {
        get => _pngDimensionsText;
        private set => SetProperty(ref _pngDimensionsText, value);
    }

    public bool HasPngRenderError
    {
        get => _hasPngRenderError;
        private set => SetProperty(ref _hasPngRenderError, value);
    }

    public string PngRenderErrorMessage
    {
        get => _pngRenderErrorMessage;
        private set => SetProperty(ref _pngRenderErrorMessage, value);
    }

    public string ActionFeedbackText
    {
        get => _actionFeedbackText;
        set => SetProperty(ref _actionFeedbackText, value);
    }

    public bool HasValidPng => _currentPngBytes is { Length: > 0 } && !HasPngRenderError;

    public RelayCommand CopyMarkdownCommand { get; }
    public RelayCommand RetryPngRenderCommand { get; }

    public byte[]? GetCurrentPngBytes() => _currentPngBytes;

    public bool SavePngToPath(string filePath, out string? errorMessage)
    {
        errorMessage = null;
        if (_currentPngBytes is null || _currentPngBytes.Length == 0)
        {
            errorMessage = "当前没有可保存的 PNG 图像，请先重试渲染或使用 Markdown 复制。";
            ActionFeedbackText = errorMessage;
            return false;
        }

        try
        {
            File.WriteAllBytes(filePath, _currentPngBytes);
            ActionFeedbackText = $"已保存 PNG 急诊单：{Path.GetFileName(filePath)}";
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"保存 PNG 失败：{ex.Message}";
            ActionFeedbackText = errorMessage;
            return false;
        }
    }

    public bool SaveMarkdownToPath(string filePath, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            File.WriteAllText(filePath, MarkdownText);
            ActionFeedbackText = $"已保存 Markdown 报告：{Path.GetFileName(filePath)}";
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"保存 Markdown 失败：{ex.Message}";
            ActionFeedbackText = errorMessage;
            return false;
        }
    }

    private void RebuildOutputs()
    {
        var options = new ShareOptions(
            HideAppName: HideAppName,
            HideExactTimestamp: HideExactTimestamp,
            IncludeScrubbedErrorExcerpt: IncludeScrubbedErrorExcerpt && CanIncludeExcerpt);

        var model = ShareReportProjector.Project(_report, options, _scrubContext);
        PrivacyStatusText = model.PrivacyBadgeText;
        MarkdownText = MarkdownReportExporter.Render(model);

        try
        {
            var rendered = EmergencyCardPngRenderer.RenderToPngBytes(model);
            _currentPngBytes = rendered.PngBytes;
            PreviewImage = EmergencyCardPngRenderer.CreateFrozenBitmapImage(rendered.PngBytes);
            var kb = rendered.PngBytes.Length / 1024.0;
            PngDimensionsText = string.Format(
                CultureInfo.InvariantCulture,
                "{0} × {1} px · 96 DPI · {2:F1} KB",
                rendered.PixelWidth,
                rendered.PixelHeight,
                kb);
            HasPngRenderError = false;
            PngRenderErrorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _currentPngBytes = null;
            PreviewImage = null;
            PngDimensionsText = string.Empty;
            HasPngRenderError = true;
            PngRenderErrorMessage = $"PNG 渲染失败：{ex.Message}（Markdown 报告仍可正常复制）";
        }

        OnPropertyChanged(nameof(HasValidPng));
    }

    private void ExecuteCopyMarkdown()
    {
        try
        {
            Clipboard.SetText(MarkdownText);
            ActionFeedbackText = "已将脱敏 Markdown 报告复制到剪贴板（内容与上方预览逐字一致）。";
        }
        catch (Exception ex)
        {
            ActionFeedbackText = $"复制剪贴板失败：{ex.Message}";
        }
    }
}
