using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MaNGOS.Extractor.UI;

public partial class MainWindow : Window
{
    private TextBox? _logTextBox;

    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logTextBox = LogTextBox;
        if (DataContext is ViewModels.MainViewModel vm)
        {
            // Restore window position/size saved from previous session.
            if (!double.IsNaN(vm.WindowLeft))   Left   = vm.WindowLeft;
            if (!double.IsNaN(vm.WindowTop))    Top    = vm.WindowTop;
            if (vm.WindowWidth  > 200)          Width  = vm.WindowWidth;
            if (vm.WindowHeight > 200)          Height = vm.WindowHeight;

            vm.LogMessages.CollectionChanged += (_, _) =>
                Dispatcher.InvokeAsync(() =>
                {
                    if (_logTextBox == null) return;
                    var sb = new StringBuilder();
                    foreach (var msg in vm.LogMessages)
                        sb.AppendLine(msg.Text);
                    _logTextBox.Text = sb.ToString();
                    _logTextBox.ScrollToEnd();
                });
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            // Capture current window geometry before saving.
            vm.WindowLeft   = Left;
            vm.WindowTop    = Top;
            vm.WindowWidth  = Width;
            vm.WindowHeight = Height;
            vm.SaveConfig();
        }
    }
}