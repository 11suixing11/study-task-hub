using AccessProbe;

// Existing regression fixtures explicitly opt into their known courses; product defaults stay unchecked.
internal static class FixtureTaskStore
{
    public static TaskStore Create(string path, ILocalLinkProtector? protector = null, IReadOnlyList<string>? pendingPlatforms = null)
    {
        var store = new TaskStore(path, protector);
        foreach (var platform in PlatformCatalog.Sites)
        {
            if (pendingPlatforms?.Contains(platform.Id) == true) continue;
            var settings = store.LoadCourseRecording(platform.Id);
            if (!settings.SelectionConfirmed) store.SaveCourseRecording(platform.Id, true, [], true, settings.Revision, DateTimeOffset.UtcNow);
        }
        return store;
    }
}
