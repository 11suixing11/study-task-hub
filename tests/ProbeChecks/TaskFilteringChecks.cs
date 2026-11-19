using AccessProbe;

internal static class TaskFilteringChecks
{
    public static void Run(Action<bool, string> check)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Synthetic UTC+8", TimeSpan.FromHours(8), "Synthetic UTC+8", "Synthetic UTC+8");
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00+08:00");
        TaskItem Row(string id, string? date, TaskState state = TaskState.Pending, string platform = "chaoxing", string course = "课程 A") => new()
        {
            Id = id, Title = id, PlatformId = platform, CourseName = course, State = state,
            DeadlineKind = date is null ? DeadlineKind.Unknown : DeadlineKind.Known,
            DeadlineUtc = date is null ? null : DateTimeOffset.Parse(date), CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var before = Row("before", "2026-11-15T09:59:59+08:00");
        var exact = Row("exact", "2026-11-15T10:00:00+08:00");
        var todayEnd = Row("todayEnd", "2026-11-15T23:59:59+08:00");
        var tomorrow = Row("tomorrow", "2026-11-16T00:00:00+08:00");
        var seventhEnd = Row("seventhEnd", "2026-11-21T23:59:59+08:00");
        var eighth = Row("eighth", "2026-11-22T00:00:00+08:00");
        var thirtiethEnd = Row("thirtiethEnd", "2026-12-14T23:59:59+08:00");
        var thirtyFirst = Row("thirtyFirst", "2026-12-15T00:00:00+08:00");
        var unknown = Row("unknown", null);
        var untrusted = Row("untrusted", "2026-11-15T12:00:00+08:00") with { Source = TaskSource.Chaoxing };
        var estimated = untrusted with { Id = "estimated", DeadlineEvidence = DeadlineEvidence.RemainingTime };
        var none = Row("none", null) with { DeadlineKind = DeadlineKind.None };
        var complete = before with { Id = "complete", State = TaskState.Completed };
        var ignored = before with { Id = "ignored", State = TaskState.Ignored };
        var returned = before with { Id = "returned", State = TaskState.Returned };
        var review = unknown with { Id = "review", State = TaskState.NeedsReview };
        TaskItem[] rows = [before, exact, todayEnd, tomorrow, seventhEnd, eighth, thirtiethEnd, thirtyFirst,
            unknown, untrusted, estimated, none, complete, ignored, returned, review];
        string[] Ids(TaskFilterOptions options) => TaskFiltering.Apply(rows, options, now, zone).Select(task => task.Id).Order().ToArray();
        bool Has(TaskItem row, TaskFilterOptions options) => TaskFiltering.Matches(row, options, now, zone);
        check(Ids(new()).Length == rows.Length, "all filters retain tasks with unknown or no deadline");
        check(Has(before, new() { Time = TaskFilterTime.Today }) && Has(todayEnd, new() { Time = TaskFilterTime.Today })
            && !Has(tomorrow, new() { Time = TaskFilterTime.Today }), "today uses a full local calendar date including earlier deadlines");
        check(Has(exact, new() { Time = TaskFilterTime.Next7Days }) && Has(seventhEnd, new() { Time = TaskFilterTime.Next7Days })
            && !Has(eighth, new() { Time = TaskFilterTime.Next7Days }) && !Has(before, new() { Time = TaskFilterTime.Next7Days }),
            "next seven days include now and end of seventh local date but exclude overdue and eighth date");
        check(Has(thirtiethEnd, new() { Time = TaskFilterTime.Next30Days }) && !Has(thirtyFirst, new() { Time = TaskFilterTime.Next30Days }),
            "next thirty days end at thirtieth local date across month boundary");
        check(Has(before, new() { Time = TaskFilterTime.Overdue }) && !Has(exact, new() { Time = TaskFilterTime.Overdue }),
            "overdue is strictly before now");
        check(Has(Row("24h", "2026-11-16T10:00:00+08:00"), new() { Time = TaskFilterTime.Next24Hours })
            && !Has(Row("24hPlus", "2026-11-16T10:00:01+08:00"), new() { Time = TaskFilterTime.Next24Hours }),
            "next twenty-four hours preserve exact elapsed-time boundary");
        check(Ids(new() { State = TaskFilterState.Completed, Time = TaskFilterTime.Overdue }).SequenceEqual(["complete"]),
            "completed state combines with overdue independently of active-task shortcut");
        check(Has(review, new() { State = TaskFilterState.Active }) && Has(returned, new() { State = TaskFilterState.Active })
            && !Has(ignored, new() { State = TaskFilterState.Active }), "active filter includes review and returned but preserves ignored state");
        check(Has(complete, new() { State = TaskFilterState.Completed }) && !Has(complete, new() { State = TaskFilterState.Pending }),
            "completion filtering respects personal stored status");
        var attention = complete with { Source = TaskSource.Pta, Overrides = TaskOverrides.State, PlatformObservedState = TaskState.Returned,
            PlatformAttentionToken = "synthetic-token" };
        check(Has(attention, new() { State = TaskFilterState.Active }) && Has(attention, new() { State = TaskFilterState.Completed }),
            "official renewed attention stays visible in active and personally completed filters");
        check(Has(unknown, new() { Time = TaskFilterTime.Unknown }) && Has(untrusted, new() { Time = TaskFilterTime.Unknown })
            && !Has(estimated, new() { Time = TaskFilterTime.Unknown }) && !Has(none, new() { Time = TaskFilterTime.Unknown }),
            "unknown time is separate from explicit no deadline and usable estimated time");
        check(!Has(untrusted, new() { Time = TaskFilterTime.Today }) && Has(estimated, new() { Time = TaskFilterTime.Today }),
            "calendar filters do not guess unusable platform dates and retain supported estimates");
        check(Ids(new() { Time = TaskFilterTime.NoDeadline }).SequenceEqual(["none"]), "no deadline matches only explicit absence");
        var sameDate = new TaskFilterOptions { Time = TaskFilterTime.Custom, FromDate = new(2026, 11, 15), ToDate = new(2026, 11, 15) };
        check(Has(before, sameDate) && Has(todayEnd, sameDate) && !Has(tomorrow, sameDate), "custom range includes both ends of final local day");
        check(Has(todayEnd with { DeadlineUtc = DateTimeOffset.Parse("2026-11-15T15:59:59Z") }, sameDate)
            && !Has(todayEnd with { DeadlineUtc = DateTimeOffset.Parse("2026-11-15T16:00:00Z") }, sameDate),
            "UTC timestamps cross the user-local midnight correctly");
        check(Has(before, sameDate with { FromDate = null }) && !Has(tomorrow, sameDate with { FromDate = null }),
            "custom date supports end-only range");
        check(Has(tomorrow, sameDate with { ToDate = null }) && !Has(before with { DeadlineUtc = now.AddDays(-1) }, sameDate with { ToDate = null }),
            "custom date supports start-only range");
        check(TaskFiltering.Validate(sameDate with { ToDate = new(2026, 11, 14) }) is not null
            && Ids(sameDate with { ToDate = new(2026, 11, 14) }).Length == 0, "reversed dates return explanatory validation rather than all tasks");
        check(TaskFiltering.Validate(new() { Time = TaskFilterTime.Custom }) is not null
            && Ids(new() { Time = TaskFilterTime.Custom }).Length == 0, "empty custom dates request an endpoint rather than silently removing filter");
        check(Has(todayEnd, sameDate with { Search = " 课程 A " }) && !Has(todayEnd, sameDate with { Search = "missing" }),
            "trimmed case-insensitive search combines with calendar conditions");
        check(Has(todayEnd, new() { PlatformId = "chaoxing" }) && !Has(todayEnd, new() { PlatformId = "pta" }),
            "platform filter composes with other conditions");
        var boundKey = CourseRecordingRules.ChaoxingKey("101", "201", "301");
        var bound = todayEnd with { RecordingCourseKey = boundKey, Source = TaskSource.Chaoxing, DeadlineEvidence = DeadlineEvidence.PlatformDate };
        var other = bound with { Id = "other", RecordingCourseKey = CourseRecordingRules.ChaoxingKey("102", "201", "301") };
        check(Has(bound, new() { CourseKey = boundKey, PlatformId = "chaoxing", State = TaskFilterState.Pending, Time = TaskFilterTime.Today })
            && !Has(other, new() { CourseKey = boundKey }), "verified course identity separates same-name official courses");
        var courses = TaskFiltering.Courses([bound, other, todayEnd, todayEnd with { PlatformId = "pta" }]);
        check(courses.Count == 5 && courses[0].Key == "", "course picker includes all-course option and separate official/manual/platform identities");
        check(TaskFiltering.Courses([bound, other, todayEnd, todayEnd with { PlatformId = "pta" }], "pta").Count == 2,
            "course picker narrows by selected platform without modifying tasks");
        check(TaskFiltering.Apply([untrusted, unknown, none, todayEnd], new() { Sort = TaskFilterSort.Deadline }, now, zone).First().Id == todayEnd.Id,
            "deadline sort keeps unusable or absent dates after known dates");
        check(TaskFiltering.Apply([before, todayEnd with { UpdatedAtUtc = now.AddSeconds(1) }], new() { Sort = TaskFilterSort.Updated }, now, zone).First().Id == todayEnd.Id,
            "update sort retains most recently updated task first");
        check(rows.All(task => task.ProgressPercent is null) && complete.State == TaskState.Completed && ignored.State == TaskState.Ignored,
            "filtering does not write progress or task state");
        var dstZone = TimeZoneInfo.CreateCustomTimeZone("Synthetic DST", TimeSpan.FromHours(-5), "Synthetic DST", "Synthetic Standard", "Synthetic Daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2020, 1, 1), new(2030, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
        var dstRange = sameDate with { FromDate = new(2026, 11, 1), ToDate = new(2026, 11, 1) };
        check(TaskFiltering.Matches(before with { DeadlineUtc = DateTimeOffset.Parse("2026-11-01T05:30:00Z") }, dstRange, now, dstZone)
            && TaskFiltering.Matches(before with { DeadlineUtc = DateTimeOffset.Parse("2026-11-01T06:30:00Z") }, dstRange, now, dstZone),
            "both repeated daylight-saving hours belong to same inclusive local date");
    }
}
