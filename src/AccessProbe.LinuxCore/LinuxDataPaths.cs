namespace AccessProbe.Linux;
public static class LinuxDataPaths
{
    public static string Resolve(string? explicitRoot, string? xdgDataHome, string userHome) => Path.GetFullPath(
        !string.IsNullOrWhiteSpace(explicitRoot) ? explicitRoot :
        Path.Combine(!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathFullyQualified(xdgDataHome)
            ? xdgDataHome : Path.Combine(userHome, ".local", "share"), "study-task-hub"));

    public static void EnsurePrivateDirectory(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new TaskValidationException("KEY001", "数据目录不能使用符号链接。");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, mode);
        if (File.GetUnixFileMode(path) != mode) throw new TaskValidationException("KEY001", "数据目录权限必须为 0700，请检查数据位置。");
    }
}
