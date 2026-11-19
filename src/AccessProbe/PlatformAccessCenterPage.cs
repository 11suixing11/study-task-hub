using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

internal sealed class PlatformAccessCenterPage : WorkspacePage
{
    private sealed record PlatformRow(TextBlock Status, TextBlock LastSync, Wpf.Ui.Controls.Button Action);
    private readonly Dictionary<string, PlatformRow> rows = new(StringComparer.Ordinal);

    public PlatformAccessCenterPage(IEnumerable<PlatformSite> sites, Action<string> openPlatform)
    {
        Title = "平台接入";
        var content = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
        content.Children.Add(new TextBlock
        {
            Text = "连接你正在使用的课程平台，作业、考试和截止时间会汇总到任务首页。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        foreach (var site in sites)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MinHeight = 68,
                Padding = new Thickness(12, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8)
            };
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(154) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(102) });
            var identity = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new TextBlock { Text = site.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            var description = new TextBlock { Text = site.Status, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            identity.Children.Add(description);
            grid.Children.Add(identity);

            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var lastSync = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            lastSync.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            details.Children.Add(status);
            details.Children.Add(lastSync);
            Grid.SetColumn(details, 1);
            grid.Children.Add(details);

            var action = new Wpf.Ui.Controls.Button { Tag = site.Id, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
            action.SetResourceReference(StyleProperty, "TextButton");
            action.Click += (_, _) => openPlatform(site.Id);
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
            card.Child = grid;
            content.Children.Add(card);
            rows.Add(site.Id, new(status, lastSync, action));
        }
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void Update(IReadOnlyDictionary<string, PlatformAccessPresentation> platforms)
    {
        foreach (var (id, row) in rows)
        {
            if (!platforms.TryGetValue(id, out var access)) continue;
            row.Status.Text = access.StatusTitle;
            row.Status.ToolTip = access.Description;
            row.Status.SetResourceReference(TextBlock.ForegroundProperty, TaskDashboardWindow.AccessStatusBrush(access.State));
            row.LastSync.Text = access.LastSyncedAtUtc is { } time ? $"最近同步 {time.ToLocalTime():MM-dd HH:mm}" : "暂无同步记录";
            row.Action.Content = access.PrimaryAction;
            AutomationProperties.SetName(row.Action, $"{access.PlatformName} · {access.PrimaryAction}");
        }
    }
}
