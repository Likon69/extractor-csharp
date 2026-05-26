using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaNGOS.Extractor.UI.ViewModels;

namespace MaNGOS.Extractor.UI.Controls;

public class MapGridControl : Image
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(TileGridViewModel), typeof(MapGridControl),
            new PropertyMetadata(null, OnViewModelChanged));

    public TileGridViewModel? ViewModel
    {
        get => (TileGridViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MapGridControl()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MapGridControl control && e.NewValue is TileGridViewModel vm)
        {
            control.Source = vm.Bitmap;
        }
    }
}

public class ExtractionButton : Button
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(string), typeof(ExtractionButton),
            new PropertyMetadata("Ready"));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
