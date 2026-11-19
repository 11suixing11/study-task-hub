using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using AccessProbe;

namespace TaskDashboardHarness;
internal static class OnboardingChecks
{
    public static async Task<bool> RunAsync(TaskDashboardWindow dashboard, TaskStore store, string root)
    {
        var checks = new List<string>(); string? failure = null;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(dashboard.FindName("OpenAccessCenterButton") is Button { IsVisible: true }, "Home exposes a visible platform connection entry");
            var search = (TextBox)dashboard.FindName("SearchBox"); search.Text = "离线";
            ((Button)dashboard.FindName("OnboardingActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dashboard.UpdateLayout();
            Check(dashboard.ActivePageKey == "access-center", "Connection entry opens the access center in the same workspace");
            Check(Application.Current.Windows.Count == 1, "Access center uses one window");
            var page = dashboard.ActivePage!;
            var actions = Visuals<Button>(page).Where(b => b.Tag is string s && CourseRecordingRules.SupportsDesktopAutomatic(s)).ToArray();
            Check(actions.Length == 8, "Access center exposes exactly the eight supported automatic platforms");
            var requested = new List<string>(); dashboard.PlatformConnectionRequested += requested.Add;
            actions.Single(b => Equals(b.Tag, "pta")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(requested.SequenceEqual(["pta"]), "Unconnected PTA goes directly to official login, without an intermediate management page");
            dashboard.PlatformConnectionRequested -= requested.Add;
            foreach (var size in new[] { (1320d,860d), (1100d,700d) })
            {
                dashboard.Width=size.Item1; dashboard.Height=size.Item2;
                await dashboard.Dispatcher.InvokeAsync(dashboard.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var scroll = Visuals<ScrollViewer>(page).First();
                Check(scroll.ExtentWidth <= scroll.ViewportWidth+1, "Access center never scrolls horizontally at " + size);
                Capture(dashboard, Path.Combine(root, "access-center-"+size.Item1+".png"));
            }
            dashboard.NavigateBack(); dashboard.UpdateLayout();
            Check(dashboard.ActivePage is null && search.Text == "离线", "Back returns to tasks with the search preserved");
            ((Button)dashboard.FindName("OnboardingSecondaryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(dashboard.FindName("OnboardingBanner") is Border { Visibility: Visibility.Collapsed }, "User can dismiss the summary");
            ((Button)dashboard.FindName("OpenAccessCenterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(dashboard.ActivePageKey == "access-center", "The sidebar keeps platform access available after dismissal");
            dashboard.NavigateBack(); dashboard.UpdateLayout();
            search.Clear(); dashboard.Width=1320; dashboard.Height=860;
            Capture(dashboard, Path.Combine(root,"dashboard.png"));
        }
        catch(Exception e) { failure=e.ToString(); }
        File.WriteAllText(Path.Combine(root,"onboarding-checks.json"), JsonSerializer.Serialize(new { outcome=failure is null?"passed":"failed", checks=checks.Count, labels=checks, failure, data="synthetic_only" }, new JsonSerializerOptions{WriteIndented=true}));
        return failure is null;
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject obj) where T:DependencyObject
    {
        if(obj is T match) yield return match;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++) foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(obj,i))) yield return child;
    }
    private static void Capture(Window window,string path)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;
        var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
}
