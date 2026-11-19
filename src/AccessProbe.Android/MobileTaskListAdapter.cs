using AccessProbe;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace AccessProbe.Mobile;

internal sealed class MobileTaskListAdapter(Context context, Action<string> openTask) : BaseAdapter<TaskItem>
{
    private static readonly Color Accent = Color.Rgb(49, 91, 232);
    private static readonly Color Ink = Color.Rgb(32, 38, 50);
    private static readonly Color Muted = Color.Rgb(103, 113, 128);
    private static readonly Color Line = Color.Rgb(229, 232, 238);
    private static readonly Color Completed = Color.Rgb(45, 124, 91);
    private static readonly Color Overdue = Color.Rgb(182, 65, 67);
    private IReadOnlyList<TaskItem> tasks = [];
    private DateTimeOffset snapshotNow;

    public int CreatedViewCount { get; private set; }
    public int BoundViewCount { get; private set; }
    public override int Count => tasks.Count;
    public override TaskItem this[int position] => tasks[position];
    public override long GetItemId(int position) => position;

    public void Submit(IReadOnlyList<TaskItem> snapshot, DateTimeOffset now)
    {
        tasks = snapshot.ToArray(); snapshotNow = now; NotifyDataSetChanged();
    }

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        var holder = convertView?.Tag as TaskViewHolder;
        if (holder is null) { holder = CreateView(); CreatedViewCount++; }
        var task = tasks[position]; holder.Owner = this; holder.Position = position; holder.TaskId = task.Id;
        var course = PlatformCatalog.Name(task.PlatformId) + " · " + (task.CourseName.Length == 0 ? "课程待补充" : task.CourseName);
        var state = TaskRules.StateName(task);
        var overdue = TaskRules.IsActive(task) && TaskRules.HasUsableDeadline(task) && task.DeadlineUtc < snapshotNow;
        var deadline = (overdue ? "已逾期 · " : "") + TaskRules.DeadlineName(task);
        holder.Title.Text = task.Title; holder.Course.Text = course; holder.State.Text = state; holder.Deadline.Text = deadline;
        holder.State.SetTextColor(task.State == TaskState.Completed && !TaskRules.HasPlatformAttention(task) ? Completed : Accent);
        holder.Deadline.SetTextColor(overdue ? Overdue : Muted);
        holder.Root.ContentDescription = $"{task.Title}，{course}，{state}，{deadline}，打开任务详情";
        BoundViewCount++; return holder.Root;
    }

    private TaskViewHolder CreateView()
    {
        var root = Column(); root.SetPadding(Dp(20), 0, Dp(20), 0); root.Clickable = true; root.Focusable = true;
        root.ImportantForAccessibility = ImportantForAccessibility.Yes;
        var card = Column(); card.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
        var surface = new GradientDrawable(); surface.SetColor(Color.White); surface.SetCornerRadius(Dp(14)); surface.SetStroke(Dp(1), Line);
        card.Background = surface; card.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        root.AddView(card, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6), BottomMargin = Dp(10) });
        var titleRow = Row(card); var title = Text(titleRow, 16, true, Ink);
        title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var arrow = Text(titleRow, 22, false, Muted); arrow.Text = "›";
        arrow.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(12) };
        var course = Text(card, 12, false, Muted);
        var metadata = Row(card); var state = Text(metadata, 12, true, Accent);
        state.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(12) };
        var deadline = Text(metadata, 12, false, Muted);
        deadline.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var holder = new TaskViewHolder(root, title, course, state, deadline); root.Tag = holder;
        root.Click += (_, _) => holder.Owner?.OpenBoundTask(holder);
        return holder;
    }

    private void OpenBoundTask(TaskViewHolder holder)
    {
        // A recycled row must use its current binding, and an invalidated snapshot cannot open a stale task.
        if (holder.Position >= 0 && holder.Position < tasks.Count && tasks[holder.Position].Id == holder.TaskId)
            openTask(holder.TaskId);
    }

    private int Dp(float value) => (int)(value * context.Resources!.DisplayMetrics!.Density + .5f);
    private LinearLayout Column() => new(context) { Orientation = Orientation.Vertical };
    private LinearLayout Row(LinearLayout parent)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal }; row.SetGravity(GravityFlags.CenterVertical);
        parent.AddView(row, new LinearLayout.LayoutParams(-1, -2)); return row;
    }
    private TextView Text(LinearLayout parent, int size, bool bold, Color color)
    {
        var text = new TextView(context) { TextSize = size }; text.SetTextColor(color);
        if (bold) text.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        text.SetPadding(0, Dp(4), 0, Dp(4)); text.SetLineSpacing(0, 1.08f);
        parent.AddView(text, new LinearLayout.LayoutParams(-1, -2)); return text;
    }

    private sealed class TaskViewHolder(LinearLayout root, TextView title, TextView course, TextView state, TextView deadline) : Java.Lang.Object
    {
        public LinearLayout Root { get; } = root;
        public TextView Title { get; } = title;
        public TextView Course { get; } = course;
        public TextView State { get; } = state;
        public TextView Deadline { get; } = deadline;
        public MobileTaskListAdapter? Owner { get; set; }
        public int Position { get; set; } = -1;
        public string TaskId { get; set; } = "";
    }
}
