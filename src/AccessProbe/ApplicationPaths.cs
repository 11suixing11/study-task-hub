using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AccessProbe;

public sealed record ApplicationLocation(string Root, bool Installed, string? ConnectPlatform)
{
    public string Database => Path.Combine(Root, ".local", "data", "tasks.db");
    public string InstanceMutex => Installed ? "Local\\LearningTaskAggregator.Installed"
        : "Local\\LearningTaskAggregator.Data." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
}

public static class ApplicationPaths
{
    public static ApplicationLocation Resolve(IReadOnlyList<string> arguments, string executableDirectory, string localApplicationData)
    {
        string? root = null; string? platform = null; var installed = true;
        for (var index = 0; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count) throw new TaskValidationException("APP001", "启动参数不完整，请使用桌面快捷方式或项目启动入口。");
            switch (arguments[index])
            {
                case "--project-root" when root is null:
                    root = arguments[index + 1]; installed = false; break;
                case "--data-root" when root is null:
                    root = arguments[index + 1]; break;
                case "--connect-platform" when platform is null:
                    platform = arguments[index + 1]; break;
                default: throw new TaskValidationException("APP001", "启动参数无法识别，请使用桌面快捷方式或项目启动入口。");
            }
        }
        if (platform is not null && !CourseRecordingRules.SupportsAutomatic(platform))
            throw new TaskValidationException("APP001", "该平台未启用官方登录。");
        if (installed)
        {
            if (!File.Exists(Path.Combine(executableDirectory, "installed-app.json")))
                throw new TaskValidationException("APP001", "请使用安装后的桌面快捷方式，或项目内“启动聚合平台.cmd”启动。");
            root ??= Path.Combine(localApplicationData, "LearningTaskAggregator");
        }
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new TaskValidationException("APP002", "无法确认数据目录，请使用绝对路径。");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!installed && (!File.Exists(Path.Combine(root, "NuGet.Config"))
            || !File.Exists(Path.Combine(root, "scripts", "build.ps1"))
            || !File.Exists(Path.Combine(root, "src", "AccessProbe", "AccessProbe.csproj"))))
            throw new TaskValidationException("APP002", "无法确认项目目录，已停止以避免写入其他位置。");
        return new(root, installed, platform);
    }

    public static string HelpPath(string root) => File.Exists(Path.Combine(root, "README.md"))
        ? Path.Combine(root, "README.md") : Path.Combine(AppContext.BaseDirectory, "使用说明.txt");
}
