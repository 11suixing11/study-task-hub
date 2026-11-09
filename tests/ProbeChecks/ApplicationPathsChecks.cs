using AccessProbe;

internal static class ApplicationPathsChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aggregator-install-path-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "程序 带空格"); var local = Path.Combine(root, "用户数据");
        Directory.CreateDirectory(app); Directory.CreateDirectory(local);
        var checks = 0;
        void Check(bool value) { checks++; if (!value) throw new Exception("Application data location check failed: " + checks); }
        void Reject(string[] args)
        {
            try { ApplicationPaths.Resolve(args, app, local); throw new Exception("Invalid launch accepted."); }
            catch (TaskValidationException) { checks++; }
        }
        try
        {
            Reject([]);
            File.WriteAllText(Path.Combine(app, "installed-app.json"), "{\"schema\":1}");
            var installed = ApplicationPaths.Resolve([], app, local);
            Check(installed.Installed && installed.Root == Path.Combine(local, "LearningTaskAggregator"));
            Check(installed.Database == Path.Combine(installed.Root, ".local", "data", "tasks.db"));
            Check(!Directory.Exists(installed.Root));
            var explicitRoot = ApplicationPaths.Resolve(["--data-root", Path.Combine(root, "独立测试")], app, local);
            Check(explicitRoot.Installed && explicitRoot.Root != installed.Root);
            Check(explicitRoot.InstanceMutex == installed.InstanceMutex);
            Reject(["--data-root", "relative"]); Reject(["--project-root"]);
            Reject(["--data-root", local, "--data-root", app]); Reject(["--connect-platform", "fif"]);
            Reject(["--unknown", "value"]); Reject(["--project-root", root]);
            Directory.CreateDirectory(Path.Combine(root, "scripts")); Directory.CreateDirectory(Path.Combine(root, "src", "AccessProbe"));
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), "test"); File.WriteAllText(Path.Combine(root, "scripts", "build.ps1"), "test");
            File.WriteAllText(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"), "test");
            var project = ApplicationPaths.Resolve(["--project-root", root, "--connect-platform", "pta"], app, local);
            Check(!project.Installed && project.Root == root && project.ConnectPlatform == "pta");
            Check(project.InstanceMutex != installed.InstanceMutex);
            Check(ApplicationPaths.Resolve(["--project-root", root.ToLowerInvariant()], app, local).InstanceMutex == project.InstanceMutex);
            foreach (var id in new[] {"chaoxing", "yuketang", "changjiang", "mooc", "xuetang", "ucampus", "zhihuishu", "pta"})
                Check(ApplicationPaths.Resolve(["--connect-platform", id], app, local).ConnectPlatform == id);
            Check(ApplicationPaths.HelpPath(root).EndsWith("使用说明.txt", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "README.md"), "test"); Check(ApplicationPaths.HelpPath(root) == Path.Combine(root, "README.md"));
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {checks} installed/project data locations, argument validation, process isolation and help checks (synthetic only).");
    }
}
