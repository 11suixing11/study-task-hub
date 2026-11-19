using AccessProbe;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace AccessProbe.Mobile;

internal static class MobileTaskListAdapterChecks
{
    public static void Run(Context context, Action<bool, string> check)
    {
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00+08:00");
        var clicks = new List<string>(); using var parent = new ListView(context);
        using var adapter = new MobileTaskListAdapter(context, clicks.Add);
        var overdue = new TaskItem
        {
            Id = "adapter-overdue", Title = "合成逾期任务", PlatformId = "pta", CourseName = "合成课程",
            DeadlineKind = DeadlineKind.Known, DeadlineUtc = now.AddMinutes(-1)
        };
        var completed = overdue with { Id = "adapter-completed", Title = "合成已完成任务", State = TaskState.Completed };
        TaskItem[] input = [overdue, completed]; adapter.Submit(input, now);
        input[0] = overdue with { Id = "changed-input" };
        check(adapter.Count == 2 && adapter[0].Id == overdue.Id, "adapter owns an immutable submitted task snapshot");
        using var root = adapter.GetView(0, null, parent);
        IEnumerable<View> Descendants(View view)
        {
            yield return view;
            if (view is ViewGroup group)
                for (var index = 0; index < group.ChildCount; index++)
                    foreach (var child in Descendants(group.GetChildAt(index)!)) yield return child;
        }
        TextView Text(string value) => Descendants(root).OfType<TextView>().Single(view => view.Text == value);
        var originalViews = Descendants(root).ToArray();
        var horizontalPadding = (int)(20 * context.Resources!.DisplayMetrics!.Density + .5f);
        check(root.PaddingLeft == horizontalPadding && root.PaddingRight == horizontalPadding,
            "recycled task rows preserve the main list horizontal margins");
        check(Text(overdue.Title).TextSize > 0 && Text("PTA · 合成课程").Text == "PTA · 合成课程"
            && Text("未完成").Text == "未完成" && root.ContentDescription?.Contains("已逾期", StringComparison.Ordinal) == true,
            "adapter initially binds task title platform course state deadline and accessible overdue text");
        root.PerformClick(); check(clicks.SequenceEqual([overdue.Id]), "initial row click opens its currently bound task");
        var reused = adapter.GetView(1, root, parent);
        check(ReferenceEquals(root, reused) && originalViews.SequenceEqual(Descendants(reused)) && adapter.CreatedViewCount == 1,
            "convertView reuse retains every native child view");
        check(Text(completed.Title).Text == completed.Title && Text("已完成").CurrentTextColor == Color.Rgb(45, 124, 91).ToArgb()
            && root.ContentDescription?.Contains("已逾期", StringComparison.Ordinal) == false,
            "rebinding removes stale overdue text and updates completed state color");
        clicks.Clear(); root.PerformClick(); check(clicks.SequenceEqual([completed.Id]), "reused row click opens the new task once");
        var attention = completed with
        {
            Id = "adapter-attention", Title = "官网要求重做的合成任务", Source = TaskSource.Pta, DeadlineEvidence = DeadlineEvidence.PlatformDate,
            Overrides = TaskOverrides.State, PlatformObservedState = TaskState.Returned, PlatformAttentionToken = "synthetic-attention"
        };
        adapter.Submit([attention], now); clicks.Clear(); root.PerformClick();
        check(clicks.Count == 0, "a row from an invalidated snapshot cannot open a stale task before rebinding");
        adapter.GetView(0, root, parent);
        check(Text("已完成 · 官网要求重做").CurrentTextColor == Color.Rgb(49, 91, 232).ToArgb()
            && root.ContentDescription?.Contains("已逾期", StringComparison.Ordinal) == true,
            "personal completion with renewed official attention remains active and overdue after reuse");
        clicks.Clear(); root.PerformClick(); check(clicks.SequenceEqual([attention.Id]), "official-attention row retains its new click target");
        var width = (int)(360 * context.Resources!.DisplayMetrics!.Density + .5f);
        int Measure()
        {
            root.Measure(View.MeasureSpec.MakeMeasureSpec(width, MeasureSpecMode.Exactly), View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
            root.Layout(0, 0, width, root.MeasuredHeight); return root.MeasuredHeight;
        }
        var shortTitle = overdue with { Id = "adapter-short", Title = "短标题" };
        adapter.Submit([shortTitle], now); adapter.GetView(0, root, parent); var shortHeight = Measure();
        var longTitle = shortTitle with { Id = "adapter-long", Title = new string('长', 300) };
        adapter.Submit([longTitle], now); adapter.GetView(0, root, parent); var longHeight = Measure();
        check(longHeight > shortHeight && Text(longTitle.Title).MaxLines > 1,
            "long task titles wrap and increase recycled row height without fixed-height clipping");
        var many = Enumerable.Range(0, 512).Select(index => shortTitle with { Id = "adapter-scale-" + index, Title = "合成规模任务 " + index }).ToArray();
        adapter.Submit(many, now);
        for (var index = 0; index < many.Length; index++) adapter.GetView(index, root, parent);
        clicks.Clear(); root.PerformClick();
        check(adapter.CreatedViewCount == 1 && adapter.BoundViewCount >= many.Length && clicks.SequenceEqual([many[^1].Id]),
            "hundreds of row bindings allocate no extra card views and install no duplicate click handlers");
        adapter.Submit([], now); clicks.Clear(); root.PerformClick();
        check(adapter.Count == 0 && clicks.Count == 0, "clearing the task snapshot invalidates detached row click targets");
        var secondClicks = new List<string>(); using var secondAdapter = new MobileTaskListAdapter(context, secondClicks.Add);
        secondAdapter.Submit([shortTitle], now); var transferred = secondAdapter.GetView(0, root, parent);
        root.PerformClick();
        check(ReferenceEquals(transferred, root) && secondAdapter.CreatedViewCount == 0 && clicks.Count == 0 && secondClicks.SequenceEqual([shortTitle.Id]),
            "a reused holder routes through its current adapter rather than an earlier event closure");
    }
}
