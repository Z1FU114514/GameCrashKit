using System.Windows;
using System.Windows.Input;
using GameCrashKit.App.Themes;
using GameCrashKit.App.ViewModels;
using GameCrashKit.App.Views;
using Microsoft.Win32;

namespace GameCrashKit.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBarHelper.Apply(this);
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        InputBindings.Add(new KeyBinding(_viewModel.StartScanCommand, new KeyGesture(Key.F5)));
        InputBindings.Add(new KeyBinding(_viewModel.CancelScanCommand, new KeyGesture(Key.Escape)));
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenManualLogPicker), new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenSharePreview), new KeyGesture(Key.E, ModifierKeys.Control)));

        Closed += (_, _) => _viewModel.Dispose();
    }

    private void SelectManualLogButton_Click(object sender, RoutedEventArgs e)
    {
        OpenManualLogPicker();
    }

    private void OpenSharePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSharePreview();
    }

    private void OpenSharePreview()
    {
        if (_viewModel.CurrentReport is null)
        {
            return;
        }

        var vm = new SharePreviewViewModel(_viewModel.CurrentReport);
        var win = new SharePreviewWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void OpenManualLogPicker()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择单个游戏崩溃日志（仅读取末尾 2 MiB，纯本地只读）",
            Filter = "日志文件 (*.log;*.txt;*.xml)|*.log;*.txt;*.xml|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true,
            DereferenceLinks = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = _viewModel.AttachManualLogFileAsync(dialog.FileName);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length == 1)
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length == 1)
        {
            _ = _viewModel.AttachManualLogFileAsync(files[0]);
        }
    }
}
