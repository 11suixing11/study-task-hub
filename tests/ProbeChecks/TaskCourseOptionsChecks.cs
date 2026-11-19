using AccessProbe;
using System.Globalization;

internal static class TaskCourseOptionsChecks
{
    public static void Run(Action<bool, string> check)
    {
        TaskItem Row(string id, string name, string platform = "pta", TaskSource source = TaskSource.Manual, string key = "") => new()
        { Id = id, Title = id, PlatformId = platform, CourseName = name, Source = source, RecordingCourseKey = key };
        var firstKey = CourseRecordingRules.MetadataKey("pta", "synthetic-course-options", "101", "class");
        var secondKey = CourseRecordingRules.MetadataKey("pta", "synthetic-course-options", "102", "class");
        var emptyKey = CourseRecordingRules.MetadataKey("pta", "synthetic-course-options", "103", "class");
        var mixedKey = CourseRecordingRules.MetadataKey("pta", "synthetic-course-options", "104", "class");
        var manual = Row("manual", "  计算机基础  ");
        var first = Row("first", "计算机基础", source: TaskSource.Pta, key: firstKey);
        var second = first with { Id = "second", RecordingCourseKey = secondKey };
        var crossPlatform = Row("cross", "计算机基础", "chaoxing");
        var unique = Row("unique", "独立课程");
        var upper = Row("upper", "Programming"); var lower = Row("lower", "programming", source: TaskSource.Pta);
        var emptyManual = Row("empty-manual", "   ");
        var emptyOfficial = Row("empty-official", "未填写课程", source: TaskSource.Pta, key: emptyKey);
        var mixed = Row("mixed-manual", "混合记录", key: mixedKey);
        var mixedManual = Row("mixed-unbound", "混合记录");
        var legacy = Row("legacy", "旧版课程", source: TaskSource.Pta);
        var legacyManual = Row("legacy-manual", "旧版课程");
        TaskItem[] tasks = [manual, manual with { Id = "manual-repeat", CourseName = "计算机基础" }, first,
            first with { Id = "first-repeat", CourseName = "同一身份的后续名称" }, second, crossPlatform, unique, upper, lower,
            emptyManual, emptyManual with { Id = "empty-repeat", CourseName = "" }, emptyOfficial,
            mixed, mixed with { Id = "mixed-official", Source = TaskSource.Pta }, mixedManual,
            legacy, legacy with { Id = "legacy-repeat" }, legacyManual];
        var courses = TaskFiltering.Courses(tasks);
        TaskCourseFilterOption Option(TaskItem task) => courses.Single(course => course.Key == CourseRecordingRules.TaskCourseKey(task));
        check(courses.Count == 14 && courses[0] == new TaskCourseFilterOption("", "全部课程", ""),
            "course options retain one all-course option and each verified/manual/legacy identity once");
        check(Option(manual).Label == "计算机基础 · PTA（手动记录）" && Option(first).Label == "计算机基础 · PTA（官网课程）"
            && Option(second).Label == Option(first).Label && Option(first).Key != Option(second).Key,
            "same-name official terms and manual records remain separate and explicitly labelled");
        check(!Option(first).Label.Contains("后续名称", StringComparison.Ordinal),
            "one course identity retains the first observed display name");
        check(Option(crossPlatform).Label == "计算机基础 · 学习通" && Option(unique).Label == "独立课程 · PTA",
            "same-name counts apply within a platform and do not add suffixes to unique courses");
        check(Option(upper).Label == "Programming · PTA" && Option(lower).Label == "programming · PTA",
            "same-name disambiguation preserves ordinal case-sensitive name equality");
        check(Option(emptyManual).Label == "未填写课程 · PTA（手动记录）" && Option(emptyOfficial).Label == "未填写课程 · PTA（官网课程）",
            "blank names retain their fallback and collide with the same displayed official name");
        check(Option(mixed).Label == "混合记录 · PTA（官网课程）" && Option(mixedManual).Label == "混合记录 · PTA（手动记录）",
            "a course group is labelled manual only when every task in that identity is manual");
        check(Option(legacy).Label == "旧版课程 · PTA（官网课程）" && Option(legacyManual).Label == "旧版课程 · PTA（手动记录）",
            "legacy official tasks remain separate from same-name manual records");
        check(TaskFiltering.Courses(tasks, "chaoxing").SequenceEqual([new TaskCourseFilterOption("", "全部课程", ""), Option(crossPlatform)])
            && TaskFiltering.Courses(tasks, "missing").Count == 1,
            "platform filtering recomputes available course options without changing their labels");
        var sameNameKeys = new[] { CourseRecordingRules.TaskCourseKey(manual), firstKey, secondKey };
        var sameNameOrder = courses.Select((course, index) => (course, index)).Where(item => sameNameKeys.Contains(item.course.Key, StringComparer.Ordinal)).ToArray();
        check(sameNameOrder.Length == 3 && sameNameOrder[^1].index - sameNameOrder[0].index == 2
            && sameNameOrder.Select(item => item.course.Key).SequenceEqual(sameNameKeys.Order(StringComparer.Ordinal)),
            "same-name official and manual identities share deterministic ordinal key order");
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var ordered = TaskFiltering.Courses([Row("z", "Z course"), Row("a", "A course"), Row("m", "M course")], "pta");
            check(ordered.Skip(1).Select(course => course.Label).SequenceEqual(["A course · PTA", "M course · PTA", "Z course · PTA"]),
                "course option labels retain the user-culture name order");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        var many = Enumerable.Range(0, 4096).Select(index => Row("scale-" + index, "相同名称", source: TaskSource.Pta,
            key: CourseRecordingRules.MetadataKey("pta", "synthetic-scale", index.ToString(CultureInfo.InvariantCulture), "class"))).ToArray();
        var largeOptions = TaskFiltering.Courses(many);
        check(largeOptions.Count == many.Length + 1 && largeOptions.Skip(1).All(course => course.Label == "相同名称 · PTA（官网课程）")
            && largeOptions.Select(course => course.Key).Distinct(StringComparer.Ordinal).Count() == largeOptions.Count,
            "large same-name catalogs retain every identity and consistent official labels");
        check(tasks[0].CourseName == "  计算机基础  " && tasks[3].CourseName == "同一身份的后续名称",
            "course option generation does not edit original task metadata");
    }
}
