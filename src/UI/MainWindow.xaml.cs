using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MaNGOS.Extractor.UI;

public partial class MainWindow : Window
{
    private TextBox? _logTextBox;

    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) => ApplyNativeTitleBarTheme();
        StateChanged += (_, _) => ApplyNativeTitleBarTheme();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeTitleBarTheme();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNativeTitleBarTheme();

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

    private void ApplyNativeTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_TEXT_COLOR = 36;

        uint darkCaption = ToColorRef((TryFindResource("PanelBackground") as SolidColorBrush)?.Color ?? Color.FromRgb(0x25, 0x25, 0x26));
        uint lightText = ToColorRef((TryFindResource("ForegroundBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xD4, 0xD4, 0xD4));

        try
        {
            int darkMode = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, Marshal.SizeOf<int>());
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref darkCaption, Marshal.SizeOf<uint>());
            DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref lightText, Marshal.SizeOf<uint>());

            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOZORDER = 0x0004;
            const uint SWP_NOACTIVATE = 0x0010;
            const uint SWP_FRAMECHANGED = 0x0020;
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch
        {
            // Non-fatal on unsupported Windows builds; keep default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    private static uint ToColorRef(Color color)
    {
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
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