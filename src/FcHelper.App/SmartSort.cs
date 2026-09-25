using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FcHelper.App;

/// <summary>
/// Every DataGrid in the app sorts its text columns by what they mean, not letter by letter: "9억" comes before
/// "93.7억" and "99.2억", "-32%" before "+96%", "0.01억 미만" before "0.42억". Columns bound to numbers, or with their
/// own SortMemberPath, keep the grid's default sort; text that is not an amount (names) sorts as text.
/// </summary>
internal static partial class SmartSort
{
    /// <summary>Set to true by the app-wide DataGrid style (App.xaml), so every grid sorts this way.</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmartSort), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.Sorting -= OnSorting;
        if ((bool)e.NewValue) grid.Sorting += OnSorting;
    }

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid || e.Column is not DataGridBoundColumn { Binding: Binding { Path.Path: { Length: > 0 } path } }) return;
        if (!string.IsNullOrEmpty(e.Column.SortMemberPath) && e.Column.SortMemberPath != path) return;
        if (grid.ItemsSource is null || CollectionViewSource.GetDefaultView(grid.ItemsSource) is not ListCollectionView view) return;
        if (view.Cast<object>().FirstOrDefault() is not { } first || first.GetType().GetProperty(path) is not { PropertyType: var type } property
            || type != typeof(string)) return;

        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in grid.Columns) if (!ReferenceEquals(c, e.Column)) c.SortDirection = null;
        e.Column.SortDirection = direction;
        view.CustomSort = new Comparer(property, direction);
    }

    /// <summary>The number a cell's text stands for, or null when it is not an amount, a percentage or a plain number.</summary>
    public static double? Value(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Replace(",", "").Replace(" ", "");
        if (s.EndsWith("억미만", StringComparison.Ordinal)) return 0.005 * 1e8;
        var m = AmountRegex().Match(s);
        if (!m.Success) return null;
        var number = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "조" => number * 1e12,
            "억" => number * 1e8,
            "만" => number * 1e4,
            _ => number, // %, 배, 점 or a bare number
        };
    }

    [GeneratedRegex(@"^([+\-−]?\d+(?:\.\d+)?)(조|억|만|%|배|점|명|장|경기)?$")]
    private static partial Regex AmountRegex();

    private sealed class Comparer(PropertyInfo property, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = property.GetValue(x) as string;
            var b = property.GetValue(y) as string;
            var va = Value(a?.Replace('−', '-'));
            var vb = Value(b?.Replace('−', '-'));
            int result;
            if (va is { } na && vb is { } nb) result = na.CompareTo(nb);
            else if (va is not null) result = -1; // amounts before blanks and words
            else if (vb is not null) result = 1;
            else result = string.Compare(a, b, StringComparison.CurrentCulture);
            return direction == ListSortDirection.Ascending ? result : -result;
        }
    }
}
