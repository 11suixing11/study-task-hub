namespace AccessProbe;

public sealed partial class ChaoxingSyncService
{
    public ConnectionReport? Connection => store.LoadConnectionReport("chaoxing");
    public event Action<ConnectionReport>? ConnectionChanged;
    public event Action? CourseSelectionRequested;
    public void RequestCourseSelection() => CourseSelectionRequested?.Invoke();
    private ConnectionFeedback PublishConnection(ChaoxingSyncSummary summary, bool catalogOnly, bool saved)
    {
        var report = store.SaveConnectionReport(ConnectionReports.From(summary, store.LoadCourseRecording("chaoxing"), catalogOnly, saved));
        ConnectionChanged?.Invoke(report);
        return ConnectionFeedback.From(report);
    }
}

public sealed partial class RainSyncService
{
    public ConnectionReport? Connection => store.LoadConnectionReport(Platform.Id);
    public event Action<ConnectionReport>? ConnectionChanged;
    public event Action? CourseSelectionRequested;
    public void RequestCourseSelection() => CourseSelectionRequested?.Invoke();
    private ConnectionFeedback PublishConnection(RainSyncSummary summary, bool catalogOnly, bool saved)
    {
        var report = store.SaveConnectionReport(ConnectionReports.From(summary, store.LoadCourseRecording(Platform.Id), catalogOnly, saved));
        ConnectionChanged?.Invoke(report);
        return ConnectionFeedback.From(report);
    }
}

public sealed partial class MoocSyncService
{
    public ConnectionReport? Connection => store.LoadConnectionReport("mooc");
    public event Action<ConnectionReport>? ConnectionChanged;
    public event Action? CourseSelectionRequested;
    public void RequestCourseSelection() => CourseSelectionRequested?.Invoke();
    private ConnectionFeedback PublishConnection(MoocSyncSummary summary, bool catalogOnly, bool saved)
    {
        var report = store.SaveConnectionReport(ConnectionReports.From(summary, store.LoadCourseRecording("mooc"), catalogOnly, saved));
        ConnectionChanged?.Invoke(report);
        return ConnectionFeedback.From(report);
    }
}

public sealed partial class XuetangSyncService
{
    public ConnectionReport? Connection => store.LoadConnectionReport("xuetang");
    public event Action<ConnectionReport>? ConnectionChanged;
    public event Action? CourseSelectionRequested;
    public void RequestCourseSelection() => CourseSelectionRequested?.Invoke();
    private ConnectionFeedback PublishConnection(XuetangSyncSummary summary, bool catalogOnly, bool saved)
    {
        var report = store.SaveConnectionReport(ConnectionReports.From(summary, store.LoadCourseRecording("xuetang"), catalogOnly, saved));
        ConnectionChanged?.Invoke(report);
        return ConnectionFeedback.From(report);
    }
}

public sealed partial class MetadataSyncService
{
    public ConnectionReport? Connection => store.LoadConnectionReport(Platform.Id);
    public event Action<ConnectionReport>? ConnectionChanged;
    public event Action? CourseSelectionRequested;
    public void RequestCourseSelection() => CourseSelectionRequested?.Invoke();
    public void LoginPageCompleted()
    {
        if (Platform.Id == "pta" && Connection is { } report && IsCurrentConnection(report) && LoginDiagnostics.PtaHomeConfirmed)
            ConnectionChanged?.Invoke(report);
    }
    private ConnectionFeedback PublishConnection(MetadataSyncSummary summary, bool catalogOnly, bool saved)
    {
        var report = store.SaveConnectionReport(ConnectionReports.From(summary, store.LoadCourseRecording(Platform.Id), catalogOnly, saved));
        if (Platform.Id == "pta")
        {
            validatedConnectionAtUtc = report.CheckedAtUtc; validatedConnectionGeneration = generation;
            validatedConnectionRevision = LoginDiagnostics.AccountValidationRevision;
        }
        ConnectionChanged?.Invoke(report);
        return ConnectionFeedback.From(report);
    }
}
