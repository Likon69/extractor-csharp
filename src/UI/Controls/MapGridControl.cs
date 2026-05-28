using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MaNGOS.Extractor.UI.ViewModels;

namespace MaNGOS.Extractor.UI.Controls;

/// <summary>
/// A UserControl that wraps the 640×640 tile-grid bitmap with a header
/// (current map name) and a legend footer (colour per phase).
/// </summary>
public sealed class MapGridControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(TileGridViewModel),
            typeof(MapGridControl), new PropertyMetadata(null, OnViewModelChanged));

    private readonly System.Windows.Controls.Image _mapImage;
    private readonly TextBlock _mapLabel;
    private readonly TextBlock _statsLabel;

    public TileGridViewModel? ViewModel
    {
        get => (TileGridViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MapGridControl()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Header ──────────────────────────────────────────────────────────
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x2A)),
            Padding    = new Thickness(8, 5, 8, 5)
        };
        _mapLabel = new TextBlock
        {
            Foreground  = new SolidColorBrush(Color.FromRgb(0x8A, 0xAA, 0xFF)),
            FontFamily  = new FontFamily("Consolas"),
            FontSize    = 12,
            FontWeight  = FontWeights.Bold,
            Text        = "— No map loaded —"
        };
        header.Child = _mapLabel;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ── Map image ────────────────────────────────────────────────────────
        var imageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x14))
        };
        _mapImage = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_mapImage, BitmapScalingMode.NearestNeighbor);
        imageHost.Child = _mapImage;
        Grid.SetRow(imageHost, 1);
        root.Children.Add(imageHost);

        // ── Footer legend ────────────────────────────────────────────────────
        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x2A)),
            Padding    = new Thickness(8, 5, 8, 5)
        };

        _statsLabel = new TextBlock
        {
            Foreground          = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily          = new FontFamily("Consolas"),
            FontSize            = 10,
            VerticalAlignment   = VerticalAlignment.Center
        };

        var legend = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        legend.Children.Add(LegendChip(Color.FromRgb(0x1B, 0x2A, 0x3B), "Pending"));
        legend.Children.Add(LegendChip(Color.FromRgb(0xCC, 0x88, 0x00), "Building"));
        legend.Children.Add(LegendChip(Color.FromRgb(0x17, 0x68, 0xAC), "Map"));
        legend.Children.Add(LegendChip(Color.FromRgb(0x6A, 0x1F, 0xB5), "Vmap"));
        legend.Children.Add(LegendChip(Color.FromRgb(0xB8, 0x96, 0x0C), "Road"));
        legend.Children.Add(LegendChip(Color.FromRgb(0x1A, 0x7F, 0x37), "Mmap"));
        legend.Children.Add(LegendChip(Color.FromRgb(0xD6, 0x2F, 0x2F), "Failed"));

        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_statsLabel, Dock.Right);
        dock.Children.Add(_statsLabel);
        dock.Children.Add(legend);
        footer.Child = dock;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private static UIElement LegendChip(Color color, string label)
    {
        var panel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Margin            = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new Rectangle
        {
            Width             = 10,
            Height            = 10,
            RadiusX           = 2,
            RadiusY           = 2,
            Fill              = new SolidColorBrush(color),
            Margin            = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily        = new FontFamily("Consolas"),
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MapGridControl ctrl) return;

        if (e.OldValue is TileGridViewModel old)
            old.PropertyChanged -= ctrl.OnVmPropertyChanged;

        if (e.NewValue is TileGridViewModel vm)
        {
            ctrl._mapImage.Source  = vm.Bitmap;
            ctrl._mapLabel.Text    = vm.CurrentMapLabel;
            ctrl._statsLabel.Text  = vm.StatsLabel;
            vm.PropertyChanged    += ctrl.OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TileGridViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(TileGridViewModel.CurrentMapLabel):
                _mapLabel.Text   = vm.CurrentMapLabel; break;
            case nameof(TileGridViewModel.StatsLabel):
                _statsLabel.Text = vm.StatsLabel;      break;
        }
    }
}

