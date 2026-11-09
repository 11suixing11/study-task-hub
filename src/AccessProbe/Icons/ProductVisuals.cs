using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AccessProbe;

/// <summary>Shared presentation for product dialogs; actions and data stay with the owning window.</summary>
internal static class ProductVisuals
{
    public static void SetButtonIcon(Button button, string key)
    {
        if (button is not Wpf.Ui.Controls.Button productButton) return;
        var icon = new ProductIcon { Key = key };
        icon.SetBinding(Wpf.Ui.Controls.IconElement.ForegroundProperty,
            new Binding(nameof(Control.Foreground)) { Source = button });
        productButton.Icon = icon;
        button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ContentControl.Content)) { Source = button });
    }

    public static FrameworkElement Heading(string title, string subtitle, string key, bool tile = true)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new ProductIcon { Key = key, Tile = tile, Width = 38, Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Wpf.Ui.Controls.IconElement.ForegroundProperty, "PrimaryBrush");
        row.Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        labels.Children.Add(detail);
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        return row;
    }

    public static Border Section(string title, string key, Panel body)
    {
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 14),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var content = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var icon = new ProductIcon { Key = key, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0) };
        icon.SetResourceReference(Wpf.Ui.Controls.IconElement.ForegroundProperty, "PrimaryBrush");
        heading.Children.Add(icon);
        heading.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(heading);
        content.Children.Add(body);
        card.Child = content;
        return card;
    }
}
