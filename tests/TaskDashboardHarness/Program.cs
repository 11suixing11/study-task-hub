using System.IO;
using System.Windows;
using AccessProbe;

namespace TaskDashboardHarness;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is not (2 or 4) || args[0] != "--project-root") return 2;
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))) return 2;
        var harnessRoot = Path.Combine(root, ".local", "task-dashboard-harness");
        Directory.CreateDirectory(harnessRoot);
        var runRoot = args.Length == 4 && args[2] == "--run-root"
            ? Path.GetFullPath(args[3]) : Path.Combine(harnessRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (!runRoot.StartsWith(harnessRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(harnessRoot, "latest-run.txt"), runRoot);
        Environment.SetEnvironmentVariable("TEMP", runRoot);
        Environment.SetEnvironmentVariable("TMP", runRoot);
        var store = new TaskStore(Path.Combine(runRoot, "tasks.db"));
        if (store.LoadTasks().Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            var draft = new TaskDraft("离线验收 · 临期任务", "mooc", "演示课程", DeadlineKind.Known, null,
                now.AddHours(8), "https://example.invalid/synthetic-task", null, "", "本清单全部使用合成数据", null);
            store.Create(draft, now);
            store.Create(draft with { Title = "离线验收 · 已逾期任务", DeadlineUtc = now.AddHours(-4) }, now);
            store.Create(draft with { Title = "离线验收 · 未知时间", DeadlineKind = DeadlineKind.Unknown, DeadlineUtc = null }, now);
            const string link = "https://mooc1.chaoxing.com/mooc-ans/mooc2/work/task?courseId=101&classId=202&cpi=303&workId=404&answerId=505&enc=SYNTHETIC-ONLY";
            store.ImportWorks([new("离线验收 · 平台已完成", "已完成", "剩余48小时", WorkListParser.ParseTaskLocation(link))], "合成学习通课程", now);
            store.SaveSettings(new(false, true));
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TaskDashboardWindow? dashboard = null;
        ReminderService? reminders = null;
        var exiting = false;
        void Exit()
        {
            if (exiting) return; exiting = true;
            reminders?.Dispose(); dashboard?.CloseForExit(); app.Shutdown();
        }
        dashboard = new TaskDashboardWindow(store, root,
            () => MessageBox.Show(dashboard!, "采集流程请使用独立 WorkPageHarness 验证。本窗口的所有任务均为合成数据。", "离线测试提示"), Exit)
        { Title = "聚合平台 · 离线桌面验收" };
        reminders = new ReminderService(store, () => dashboard.ShowDashboard(), Exit);
        dashboard.AttachReminders(reminders);
        dashboard.ShowSuccess("离线合成清单；任务保存在独立测试目录，可重启本程序验证。");
        app.MainWindow = dashboard;
        app.Run(dashboard);
        reminders.Dispose(); return 0;
    }
}
