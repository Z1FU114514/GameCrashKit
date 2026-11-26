using System.Windows;
using System.Windows.Input;
using GameCrashKit.App.Themes;
using GameCrashKit.App.ViewModels;
using Microsoft.Win32;

namespace GameCrashKit.App.Views;

public partial class SharePreviewWindow : Window
{
    private readonly SharePreviewViewModel _viewModel;

    public SharePreviewWindow(SharePreviewViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBarHelper.Apply(this);
        ClampToWorkArea();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        InputBindings.Add(new KeyBinding(new RelayCommand(Close), new KeyGesture(Key.Escape)));
    }

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width > 0 && Width > workArea.Width - 20)
        {
            Width = Math.Max(MinWidth, workArea.Width - 20);
        }

        if (workArea.Height > 0 && Height > workArea.Height - 20)
        {
            Height = Math.Max(MinHeight, workArea.Height - 20);
        }
    }

    private void SavePngButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 PNG 急诊单",
            Filter = "PNG 图像 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"GameCrashKit_急诊单_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog(this) == true &&
            !_viewModel.SavePngToPath(dialog.FileName, out var err) &&
            err is not null)
        {
            MessageBox.Show(this, err, "保存 PNG 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存脱敏 Markdown 报告",
            Filter = "Markdown 文档 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            DefaultExt = ".md",
            FileName = $"GameCrashKit_报告_{DateTime.Now:yyyyMMdd_HHmmss}.md"
        };

        if (dialog.ShowDialog(this) == true &&
            !_viewModel.SaveMarkdownToPath(dialog.FileName, out var err) &&
            err is not null)
        {
            MessageBox.Show(this, err, "保存 Markdown 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
