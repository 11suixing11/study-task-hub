using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AccessProbe;

// Persistent plans are evaluated while this process is running. Closing the dashboard hides it to the tray.
public sealed class ReminderService : IDisposable
{
    private readonly TaskStore store;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer;
    private readonly Action showWindow;
    private bool disposed;
    private bool ticking;
    private int storeGeneration;
    public event Action<string>? StatusChanged;

    public ReminderService(TaskStore store, Action showWindow, Action exit)
    {
        this.store = store; this.showWindow = showWindow;
        store.Changed += StoreChanged;
        tray = new Forms.NotifyIcon { Icon = SystemIcons.Information, Text = "学习任务聚合 · 提醒运行中", Visible = true };
        tray.DoubleClick += (_, _) => showWindow();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开任务清单", null, (_, _) => showWindow());
        menu.Items.Add("退出（停止提醒）", null, (_, _) => exit());
        tray.ContextMenuStrip = menu;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => Tick(); timer.Start();
    }

    private void StoreChanged() => Interlocked.Increment(ref storeGeneration);

    public bool SendTest()
    {
        if (disposed || !store.LoadSettings().NotificationsEnabled) return false;
        try
        {
            tray.ShowBalloonTip(8000, "学习任务聚合 · 测试提醒", "提醒服务已运行。实际显示取决于 Windows 通知设置。", Forms.ToolTipIcon.Info);
            StatusChanged?.Invoke("已向 Windows 请求测试提醒，请查看桌面通知。");
            return true;
        }
        catch { StatusChanged?.Invoke("NOTIFY001：系统提醒请求失败，任务清单仍可查看。"); return false; }
    }

    public async void Tick()
    {
        if (disposed || ticking) return;
        ticking = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var generation = Volatile.Read(ref storeGeneration);
            var due = await Task.Run(() => store.TakeDue(now));
            if (due.Count == 0) return;
            if (disposed || generation != Volatile.Read(ref storeGeneration))
            {
                await Task.Run(() => store.ReleaseReminderClaims(due.Select(item => item.Plan.Id).ToArray()));
                return;
            }
            var first = due[0];
            var title = first.Task.Title.Length > 70 ? first.Task.Title[..70] + "…" : first.Task.Title;
            var body = due.Count == 1
                ? PlatformCatalog.Name(first.Task.PlatformId) + " · " + title + "\n" + ReminderPlanner.KindName(first.Plan.Kind)
                : due.Count + " 条学习任务需要查看。\n" + title;
            if (first.Plan.Kind == ReminderKind.Overdue) body += "\n可在清单中复制延期申请。";
            var success = false;
            try { tray.ShowBalloonTip(10000, "学习任务提醒", body, Forms.ToolTipIcon.Info); success = true; }
            catch { }
            await Task.Run(() => store.RecordNotificationRequest(due.Select(item => item.Plan.Id).ToArray(), success, DateTimeOffset.UtcNow));
            if (disposed) return;
            StatusChanged?.Invoke(success
                ? "已向 Windows 请求 " + due.Count + " 条提醒；显示情况受系统通知设置影响。"
                : "NOTIFY001：系统提醒请求失败，将稍后重试。");
        }
        catch { if (!disposed) StatusChanged?.Invoke("NOTIFY002：提醒计划读取失败，请查看本地数据库状态。"); }
        finally { ticking = false; }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        store.Changed -= StoreChanged;
        timer.Stop(); tray.Visible = false;
        tray.ContextMenuStrip?.Dispose(); tray.Dispose();
    }
}
