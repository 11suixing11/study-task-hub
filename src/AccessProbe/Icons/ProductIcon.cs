using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

/// <summary>A scalable WPF UI icon that follows the owning button's foreground and disabled state.</summary>
public sealed class ProductIcon : Wpf.Ui.Controls.IconElement
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key), typeof(string), typeof(ProductIcon),
        new FrameworkPropertyMetadata("document", FrameworkPropertyMetadataOptions.AffectsRender, OnArtworkChanged));
    public static readonly DependencyProperty TileProperty = DependencyProperty.Register(nameof(Tile), typeof(bool), typeof(ProductIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnArtworkChanged));
    private Image? artwork;

    static ProductIcon()
    {
        ForegroundProperty.OverrideMetadata(typeof(ProductIcon),
            new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public ProductIcon()
    {
        Width = Height = 18;
        Focusable = false;
        IsHitTestVisible = false;
    }

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public bool Tile { get => (bool)GetValue(TileProperty); set => SetValue(TileProperty, value); }

    protected override UIElement InitializeChildren()
    {
        artwork = new Image { Stretch = Stretch.Uniform, Focusable = false, IsHitTestVisible = false };
        UpdateArtwork();
        return artwork;
    }

    protected override void OnForegroundChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnForegroundChanged(args);
        UpdateArtwork();
    }

    private static void OnArtworkChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((ProductIcon)owner).UpdateArtwork();

    private void UpdateArtwork()
    {
        if (artwork is null) return;
        if (Tile && !SystemParameters.HighContrast)
            artwork.Source = ProductIcons.Tile(Key);
        else
            artwork.Source = new DrawingImage(ProductIcons.CreateDrawing(Key, Tile, Foreground ?? SystemColors.ControlTextBrush, SystemParameters.HighContrast));
    }
}
