using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AccessProbe;

// Business pages have no native window. The dashboard owns navigation and their lifetime.
public class WorkspacePage : UserControl, IDisposable
{
    private bool disposed;
    public string Title { get; protected set; } = "";
    public bool IsPageActive { get; private set; }
    public bool IsClosed => disposed;
    public bool CanReuse { get; protected set; } = true;
    public event EventHandler? Activated;
    public event EventHandler? Deactivated;
    public event EventHandler? Closed;
    public event Action? BackRequested;
    public event Action<WorkspacePage>? NavigationRequested;

    public void ActivatePage()
    {
        if (disposed || IsPageActive) return;
        IsPageActive = true;
        Activated?.Invoke(this, EventArgs.Empty);
    }

    public void DeactivatePage()
    {
        if (!IsPageActive) return;
        IsPageActive = false;
        Deactivated?.Invoke(this, EventArgs.Empty);
    }

    protected void RequestBack() => BackRequested?.Invoke();
    protected void ShowChildPage(WorkspacePage page)
    {
        if (disposed || !IsPageActive || NavigationRequested is null) { page.Dispose(); return; }
        NavigationRequested.Invoke(page);
    }

    protected Task<bool> ConfirmAsync(string title, string message)
    {
        if (Window.GetWindow(this) is TaskDashboardWindow dashboard)
            return dashboard.ConfirmAsync(title, message);
        return Task.FromResult(false);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        DeactivatePage();
        disposed = true;
        Closed?.Invoke(this, EventArgs.Empty);
        Activated = Deactivated = Closed = null;
        BackRequested = null;
        NavigationRequested = null;
        Content = null;
        GC.SuppressFinalize(this);
    }
}

public sealed class WorkspaceContentPage : WorkspacePage
{
    public WorkspaceContentPage(string title, UIElement content)
    {
        Title = title;
        Content = content;
    }
}

internal sealed class WorkspaceMessagePage : WorkspacePage
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> Completion => completion.Task;

    public WorkspaceMessagePage(string title, string message, string accept = "确认", string? cancel = "取消")
    {
        Title = title;
        var body = new StackPanel { Margin = new Thickness(28), MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        var actions = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        if (cancel is not null)
        {
            var no = new Wpf.Ui.Controls.Button { Content = cancel, Margin = new Thickness(0, 0, 12, 0),
                Padding = new Thickness(20, 8, 20, 8) };
            no.Click += (_, _) => { completion.TrySetResult(false); RequestBack(); };
            actions.Children.Add(no);
        }
        var yes = new Wpf.Ui.Controls.Button { Content = accept, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Padding = new Thickness(20, 8, 20, 8) };
        yes.Click += (_, _) => { completion.TrySetResult(true); RequestBack(); };
        System.Windows.Automation.AutomationProperties.SetName(yes, title + " · " + accept);
        actions.Children.Add(yes); body.Children.Add(actions);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Closed += (_, _) => completion.TrySetResult(false);
        Deactivated += (_, _) => completion.TrySetResult(false);
    }
}
