using System.IO;
using System.Windows;
using System.Windows.Controls;
using AccessProbe;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TaskDashboardHarness;

internal static class ConnectionUxChecks
{
    public static async Task VerifyAsync(TaskDashboardWindow dashboard, string runRoot, Action<bool, string> check)
    {
        var feedback = new ConnectionReport("chaoxing") { AccountVerified = true, CatalogCovered = true, Courses = 2, CatalogOnly = true };
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var choices = 0; var reopened = 0; var reloaded = 0; var retried = 0;
        ConnectionStatusPanel? panel = null;
        panel = new(async () => { calls++; await source.Task; panel!.Show(feedback); }, () => choices++,
            retryTasks: () => { retried++; return Task.CompletedTask; },
            recoverLogin: reopen => { if (reopen) reopened++; else reloaded++; return Task.CompletedTask; });
        var body = (StackPanel)panel.Child;
        var title = body.Children.OfType<TextBlock>().First();
        var buttons = body.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        var inspect = body.Children.OfType<Expander>().Single();
        var detail = (TextBlock)inspect.Content;
        var checkButton = buttons[0]; var chooseButton = buttons[1];
        bool OnlyPrimary(Button expected) => buttons.Where(button => button.Visibility == Visibility.Visible && button
            is Wpf.Ui.Controls.Button { Appearance: Wpf.Ui.Controls.ControlAppearance.Primary }).SequenceEqual([expected]);
        check(chooseButton.Visibility == Visibility.Collapsed && title.Text.Contains("等待官网确认"), "fresh connection has no premature success or course action");
        checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(calls == 1 && !checkButton.IsEnabled && title.Text.Contains("正在确认"), "manual check shows immediate progress and disables duplicate actions");
        checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(calls == 1, "rapid repeated check clicks start one validation");
        source.SetResult(); await Task.Delay(50);
        check(title.Text == "已接入，待选择课程" && checkButton.Visibility == Visibility.Visible && checkButton.IsEnabled
            && Equals(checkButton.Content, "确认课程并同步") && chooseButton.Visibility == Visibility.Collapsed
            && OnlyPrimary(checkButton),
            "verified result offers only course confirmation as its main action");
        checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(choices == 2, "explicit course action remains repeatable");
        panel.ShowPageFailure("certificate_failed", "合成官网证书失败");
        check(title.Text == "已接入，待选择课程" && body.Children.OfType<TextBlock>().Any(text => text.Visibility == Visibility.Visible && text.Text.Contains("证书")),
            "page failure explains the browser problem without erasing verified account result");
        panel.SetRunning(true);
        check(title.Text == "已接入，待选择课程" && !checkButton.IsEnabled, "automatic checks preserve an unresolved page failure and disable actions");
        panel.SetRunning(false);
        check(Equals(checkButton.Content, "重新打开登录页") && OnlyPrimary(checkButton), "page load failure presents one browser recovery action");
        checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(reopened == 1 && calls == 1, "reopening the browser does not start a metadata check");
        panel.ClearPageFailure();
        panel.Show(feedback with { LoginRequired = true });
        check(Equals(checkButton.Content, "刷新登录页") && OnlyPrimary(checkButton), "login-required result offers a single refresh action");
        checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(reloaded == 1 && calls == 1, "login refresh calls the existing browser recovery path");
        panel.Show(feedback with { Courses = 0 });
        check(Equals(checkButton.Content, "重试读取课程") && OnlyPrimary(checkButton) && chooseButton.Visibility == Visibility.Collapsed,
            "empty catalog supports rereading courses without offering an empty course list");
        panel.Show(feedback with { CatalogCovered = false, ProblemOutcomes = ["network_error"] });
        check(Equals(checkButton.Content, "重试读取课程") && OnlyPrimary(checkButton) && chooseButton.Visibility == Visibility.Visible,
            "partial catalog keeps course confirmation as a secondary action");
        panel.Show(feedback with { CatalogOnly = false, SelectionConfirmed = true, SelectedCourses = 1 });
        check(Equals(checkButton.Content, "重试同步") && OnlyPrimary(checkButton), "incomplete task sync offers a single task retry");
        checkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(retried == 1 && calls == 1, "retry sync reads tasks rather than only refreshing the catalog");
        panel.Show(feedback, true);
        check(title.Text.StartsWith("上次检查 · ") && detail.Text.Contains("检查时间"), "reopened panel identifies a stored result as a previous check");
        panel.Reset();
        check(title.Text.Contains("等待官网确认") && chooseButton.Visibility == Visibility.Collapsed && detail.Text.Contains("尚未完成本次"),
            "clearing login resets status, details and the next action");
        var failedPanel = new ConnectionStatusPanel(() => Task.FromException(new IOException("Synthetic connection failure")), () => { });
        var failedBody = (StackPanel)failedPanel.Child;
        failedBody.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(25);
        check(failedBody.Children.OfType<TextBlock>().First().Text == "本次接入检查未完成", "unexpected check failure has a visible terminal result");
        check(ConnectionPageFailures.Outcome(CoreWebView2WebErrorStatus.Timeout) == "timeout"
            && ConnectionPageFailures.Outcome(CoreWebView2WebErrorStatus.CertificateExpired) == "certificate_failed"
            && ConnectionPageFailures.Outcome(CoreWebView2WebErrorStatus.Unknown, 403) == "forbidden"
            && ConnectionPageFailures.Outcome(CoreWebView2WebErrorStatus.Unknown, 429) == "rate_limited",
            "WebView timeout, certificate, permission and throttling map to actual causes");

        var store = new TaskStore(Path.Combine(runRoot, "connection-ux.db")); store.SaveSettings(new(false, false));
        using var chaoxing = new ChaoxingSyncService(store, runRoot, new WebView2());
        using var rain = new RainSyncService(store, runRoot, RainPlatform.Rain, new WebView2());
        using var mooc = new MoocSyncService(store, runRoot, new WebView2());
        using var xuetang = new XuetangSyncService(store, runRoot, new WebView2());
        using var pta = new MetadataSyncService(MetadataPlatform.Pta, store, runRoot, new WebView2());
        dashboard.AttachSync(chaoxing); dashboard.AttachRainSync(rain); dashboard.AttachMoocSync(mooc);
        dashboard.AttachXuetangSync(xuetang); dashboard.AttachMetadataSync(pta);
        foreach (var request in new Action[] { chaoxing.RequestCourseSelection, rain.RequestCourseSelection, mooc.RequestCourseSelection,
            xuetang.RequestCourseSelection, pta.RequestCourseSelection })
        {
            request(); await Task.Delay(25);
            dashboard.UpdateLayout();
            var first = (CourseSelectionPage)dashboard.ActivePage!;
            check(first.IsVisible && Application.Current.Windows.Count == 1,
                "explicit selection opens the platform page in the main window (route=" + dashboard.ActivePageKey
                + ", visible=" + first.IsVisible + ", windows=" + Application.Current.Windows.Count + ")");
            dashboard.NavigateBack(); await Task.Delay(25);
            request(); await Task.Delay(25);
            await dashboard.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            dashboard.UpdateLayout();
            var second = (CourseSelectionPage)dashboard.ActivePage!;
            check(second.IsVisible && second == first && !second.IsClosed,
                $"platform page is reused after returning to tasks (route={dashboard.ActivePageKey}, visible={second.IsVisible}, reused={second == first}, closed={second.IsClosed})");
            dashboard.ShowDashboard();
        }
    }
}
