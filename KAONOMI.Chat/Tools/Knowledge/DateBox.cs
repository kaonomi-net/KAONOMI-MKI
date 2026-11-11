using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// An optional date: typed as yyyy-MM-dd (Enter or leaving the field), or picked on a small calendar (📅, with TODAY);
/// ✕ empties it. A date that cannot be read is shown in red and kept out.
/// </summary>
public sealed class DateBox : Grid
{
    private readonly TextBox _edit = new() { PlaceholderText = "(none)", MinWidth = 96 };
    private DateTime? _value;

    public DateBox()
    {
        ColumnSpacing = 3;
        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto");
        Children.Add(_edit);

        var calendar = new Avalonia.Controls.Calendar { SelectionMode = CalendarSelectionMode.SingleDate, IsTodayHighlighted = true };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var today = Ui.Button("TODAY", () =>
        {
            SetValue(DateTime.Today, true);
            flyout.Hide();
        });
        flyout.Content = Ui.Column(6, calendar, today);
        var updating = false;
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (updating || calendar.SelectedDate is not { } date)
                return;
            SetValue(date, true);
            flyout.Hide();
        };
        flyout.Opening += (_, _) =>
        {
            updating = true;
            calendar.SelectedDate = _value;
            calendar.DisplayDate = _value ?? DateTime.Today;
            updating = false;
        };
        var pick = new Button { Content = "📅", Flyout = flyout, Padding = new Thickness(6, 2) };
        ToolTip.SetTip(pick, "Pick the date on a calendar");
        var clear = Ui.Button("✕", () => SetValue(null, true), "No date");
        clear.Padding = new Thickness(6, 2);
        SetColumn(pick, 1);
        SetColumn(clear, 2);
        Children.Add(pick);
        Children.Add(clear);
        _edit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Parse();
                e.Handled = true;
            }
        };
        _edit.LostFocus += (_, _) => Parse();
    }

    /// <summary>Raised when the user changes the date.</summary>
    public event Action<DateTime?>? Changed;

    /// <summary>The date (no time), or null.</summary>
    public DateTime? Value
    {
        get => _value;
        set => SetValue(value, false);
    }

    /// <summary>The tooltip of the text of the date.</summary>
    public string Tip
    {
        set => ToolTip.SetTip(_edit, value);
    }

    private void SetValue(DateTime? value, bool raise)
    {
        var changed = _value != value?.Date;
        _value = value?.Date;
        _edit.Text = KnowledgeUi.Format(_value);
        _edit.ClearValue(TextBox.ForegroundProperty);
        if (raise && changed)
            Changed?.Invoke(_value);
    }

    private void Parse()
    {
        var text = (_edit.Text ?? string.Empty).Trim();
        if (text == KnowledgeUi.Format(_value))
            return;
        if (text.Length == 0)
        {
            SetValue(null, true);
            return;
        }
        string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "dd/MM/yyyy", "d/M/yyyy", "yyyy/MM/dd", "d.M.yyyy"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
            SetValue(date, true);
        else
            _edit.Foreground = Ui.Red;
    }

    /// <summary>A date box bound both ways to the <see cref="DateTime"/>? property <paramref name="name"/> of <paramref name="source"/>.</summary>
    public static DateBox Bound(INotifyPropertyChanged source, string name, string tip = "")
    {
        var box = new DateBox { Tip = tip };
        var property = source.GetType().GetProperty(name);
        Ui.When(box, source, () => box.Value = property?.GetValue(source) as DateTime?, name);
        box.Changed += value => property?.SetValue(source, value);
        return box;
    }
}
