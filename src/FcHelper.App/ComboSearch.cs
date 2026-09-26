using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FcHelper.Market;

namespace FcHelper.App;

/// <summary>Adds a contains-search field to a ComboBox drop-down without making the ComboBox itself editable.</summary>
public static class ComboSearch
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ComboSearch), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(ComboSearch), new PropertyMetadata("", OnQueryChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static string GetQuery(DependencyObject element) => (string)element.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject element, string value) => element.SetValue(QueryProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box) return;
        box.DropDownOpened -= OnDropDownOpened;
        box.DropDownClosed -= OnDropDownClosed;
        if (e.NewValue is true)
        {
            box.DropDownOpened += OnDropDownOpened;
            box.DropDownClosed += OnDropDownClosed;
        }
        else
        {
            SetQuery(box, "");
        }
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox box) return;
        SetQuery(box, "");
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (box.IsDropDownOpen && box.Template.FindName("PART_SearchBox", box) is TextBox search)
            {
                search.PreviewKeyDown -= OnSearchKeyDown;
                search.PreviewKeyDown += OnSearchKeyDown;
                search.Tag = box;
                search.Focus();
                search.SelectAll();
            }
        });
    }

    private static void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: ComboBox box } || e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Enter && box.Items.Count > 0) box.SelectedItem = box.Items[0];
        box.IsDropDownOpen = false;
        box.Focus();
        e.Handled = true;
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox box) SetQuery(box, "");
    }

    private static void OnQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box || !box.Items.CanFilter) return;
        var query = (string?)e.NewValue ?? "";
        box.Items.Filter = query.Trim().Length == 0 ? null : item => OptionSearch.Matches(DisplayText(box, item), query);
    }

    private static string DisplayText(ComboBox box, object? item)
    {
        if (item is null) return "";
        if (box.DisplayMemberPath is not { Length: > 0 } path) return item.ToString() ?? "";
        return TypeDescriptor.GetProperties(item)[path]?.GetValue(item)?.ToString() ?? "";
    }
}
