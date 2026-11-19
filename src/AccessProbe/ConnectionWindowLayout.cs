using System.Windows;
using System.Windows.Controls;

namespace AccessProbe;

internal static class ConnectionWindowLayout
{
    public static Grid Create(FrameworkElement guide, FrameworkElement browser)
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer
        {
            Content = guide, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(scroll, "接入引导与检查详情");
        root.Children.Add(scroll); Grid.SetColumn(browser, 2); root.Children.Add(browser);
        return root;
    }
}
