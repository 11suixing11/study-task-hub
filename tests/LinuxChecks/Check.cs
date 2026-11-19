namespace LinuxChecks;

static class Check
{
    public static int Count { get; private set; }
    public static void True(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidDataException(message); Count++; }
    public static void Equal<T>(T expected, T actual)
    { True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}"); }
    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); } catch (TException error) { Count++; return error; }
        throw new InvalidDataException("Expected " + typeof(TException).Name);
    }
}

sealed class TestRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, ".local", "linux-checks", Guid.NewGuid().ToString("N"));
    public TestRoot() => Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    public void Dispose() => Directory.Delete(Path, true);
}
