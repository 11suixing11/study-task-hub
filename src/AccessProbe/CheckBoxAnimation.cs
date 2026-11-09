using System.Windows;

namespace AccessProbe;

// A typed animation value remains valid when WPF recycles or removes a course row.
public static class CheckBoxAnimation
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached(
        "Progress", typeof(double), typeof(CheckBoxAnimation), new PropertyMetadata(1.0));

    public static double GetProgress(DependencyObject element) => (double)element.GetValue(ProgressProperty);

    public static void SetProgress(DependencyObject element, double value) => element.SetValue(ProgressProperty, value);
}
