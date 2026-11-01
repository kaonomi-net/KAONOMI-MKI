using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// Binds the view models of the Avalonia chat (shared as they are: CommunityToolkit properties and commands, observable
/// collections) to Godot controls. Every binding is undone when its control leaves the tree, and a change raised on
/// another thread is applied on the main thread.
/// </summary>
public static class Bind
{
    /// <summary>The thread of Godot (set by <see cref="AvaloniaBridge.Initialize"/>).</summary>
    public static int MainThread { get; set; } = System.Environment.CurrentManagedThreadId;

    /// <summary>Runs <paramref name="action"/> now on the main thread, else at the next frame there.</summary>
    public static void OnMain(Action action)
    {
        if (System.Environment.CurrentManagedThreadId == MainThread)
            action();
        else
            Callable.From(action).CallDeferred();
    }

    /// <summary>
    /// Calls <paramref name="apply"/> now and whenever one of <paramref name="names"/> of <paramref name="source"/>
    /// changes (any property when <paramref name="names"/> is empty), while <paramref name="owner"/> is in the tree.
    /// </summary>
    public static void When(Node owner, INotifyPropertyChanged source, Action apply, params string[] names)
    {
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (names.Length == 0 || string.IsNullOrEmpty(e.PropertyName) || names.Contains(e.PropertyName))
                OnMain(() =>
                {
                    if (GodotObject.IsInstanceValid(owner))
                        apply();
                });
        }
        source.PropertyChanged += Handler;
        OnGone(owner, () => source.PropertyChanged -= Handler);
        apply();
    }

    /// <summary>
    /// Calls <paramref name="cleanup"/> once <paramref name="owner"/> is gone: freed, or out of the tree for good. A
    /// node only moved (a window sent to the back or brought to the front: its viewport changes parent) leaves the
    /// tree and comes back at once: its bindings stay.
    /// </summary>
    public static void OnGone(Node owner, Action cleanup)
    {
        var done = false;
        owner.TreeExiting += () => Callable.From(() =>
        {
            if (done || GodotObject.IsInstanceValid(owner) && owner.IsInsideTree())
                return;
            done = true;
            try
            {
                cleanup();
            }
            catch (ObjectDisposedException)
            {
                // The chat is closing: what it was bound to is already gone.
            }
        }).CallDeferred();
    }

    /// <summary>The value of the property <paramref name="name"/> of <paramref name="source"/>.</summary>
    public static object? Get(object source, string name) => Property(source, name)?.GetValue(source);

    /// <summary>The value of the property <paramref name="name"/> of <paramref name="source"/> as <typeparamref name="T"/>.</summary>
    public static T? Get<T>(object source, string name) => Get(source, name) is T value ? value : default;

    /// <summary>Sets the property <paramref name="name"/> of <paramref name="source"/> (converted to its type).</summary>
    public static void Set(object source, string name, object? value)
    {
        if (Property(source, name) is not { CanWrite: true } property)
            return;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (value is not null && !type.IsInstanceOfType(value))
        {
            try
            {
                value = type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return;
            }
        }
        property.SetValue(source, value);
    }

    private static PropertyInfo? Property(object source, string name) =>
        source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    // ── Text ──

    /// <summary>A label showing the property <paramref name="name"/> (through <paramref name="format"/>).</summary>
    public static T Text<T>(T label, INotifyPropertyChanged source, string name, Func<object?, string>? format = null) where T : Control
    {
        When(label, source, () =>
        {
            var value = Get(source, name);
            var text = format?.Invoke(value) ?? value?.ToString() ?? string.Empty;
            switch (label)
            {
                case Label l: l.Text = text; break;
                case RichTextLabel r: r.Text = text; break;
                case Button b: b.Text = text; break;
            }
        }, name);
        return label;
    }

    /// <summary>A line edit bound both ways to the string property <paramref name="name"/>.</summary>
    public static LineEdit Edit(LineEdit edit, INotifyPropertyChanged source, string name)
    {
        When(edit, source, () =>
        {
            var text = Get(source, name)?.ToString() ?? string.Empty;
            if (edit.Text != text)
                edit.Text = text;
        }, name);
        edit.TextChanged += text => Set(source, name, text);
        return edit;
    }

    /// <summary>A multi-line edit bound both ways to the string property <paramref name="name"/>.</summary>
    public static TextEdit Edit(TextEdit edit, INotifyPropertyChanged source, string name)
    {
        When(edit, source, () =>
        {
            var text = Get(source, name)?.ToString() ?? string.Empty;
            if (edit.Text != text)
            {
                var caret = (edit.GetCaretLine(), edit.GetCaretColumn());
                edit.Text = text;
                edit.SetCaretLine(Math.Min(caret.Item1, edit.GetLineCount() - 1));
                edit.SetCaretColumn(caret.Item2);
            }
        }, name);
        edit.TextChanged += () => Set(source, name, edit.Text);
        return edit;
    }

    /// <summary>A spin box bound both ways to the number property <paramref name="name"/>.</summary>
    public static SpinBox Number(SpinBox spin, INotifyPropertyChanged source, string name)
    {
        When(spin, source, () =>
        {
            if (Get(source, name) is IConvertible value)
                spin.SetValueNoSignal(value.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
        }, name);
        spin.ValueChanged += value => Set(source, name, value);
        return spin;
    }

    /// <summary>A slider bound both ways to the number property <paramref name="name"/>.</summary>
    public static T Range<T>(T range, INotifyPropertyChanged source, string name) where T : Godot.Range
    {
        When(range, source, () =>
        {
            if (Get(source, name) is IConvertible value)
                range.SetValueNoSignal(value.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
        }, name);
        range.ValueChanged += value => Set(source, name, value);
        return range;
    }

    /// <summary>A check box (or toggle button) bound both ways to the bool property <paramref name="name"/>.</summary>
    public static T Check<T>(T button, INotifyPropertyChanged source, string name) where T : BaseButton
    {
        button.ToggleMode = true;
        When(button, source, () => button.SetPressedNoSignal(Get(source, name) is true), name);
        button.Toggled += on => Set(source, name, on);
        return button;
    }

    // ── State ──

    /// <summary>Shows <paramref name="control"/> while the bool property <paramref name="name"/> is true (false with <paramref name="invert"/>).</summary>
    public static T Visible<T>(T control, INotifyPropertyChanged source, string name, bool invert = false) where T : CanvasItem
    {
        When(control, source, () => control.Visible = (Get(source, name) is true) != invert, name);
        return control;
    }

    /// <summary>Enables <paramref name="control"/> while the bool property <paramref name="name"/> is true (false with <paramref name="invert"/>).</summary>
    public static T Enabled<T>(T control, INotifyPropertyChanged source, string name, bool invert = false) where T : Control
    {
        When(control, source, () =>
        {
            var enabled = (Get(source, name) is true) != invert;
            switch (control)
            {
                case BaseButton b: b.Disabled = !enabled; break;
                case LineEdit l: l.Editable = enabled; break;
                case TextEdit t: t.Editable = enabled; break;
                default: control.Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.45f); control.MouseFilter = enabled ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore; break;
            }
        }, name);
        return control;
    }

    // ── Commands ──

    /// <summary>
    /// A button running <paramref name="command"/> (with <paramref name="parameter"/>), disabled while it cannot run.
    /// </summary>
    public static T Command<T>(T button, ICommand? command, Func<object?>? parameter = null) where T : BaseButton
    {
        if (command is null)
            return button;
        void Refresh() => OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(button))
                button.Disabled = !command.CanExecute(parameter?.Invoke());
        });
        EventHandler handler = (_, _) => Refresh();
        command.CanExecuteChanged += handler;
        OnGone(button, () => command.CanExecuteChanged -= handler);
        button.Pressed += () =>
        {
            var value = parameter?.Invoke();
            if (command.CanExecute(value))
                command.Execute(value);
        };
        Refresh();
        return button;
    }

    /// <summary>Runs <paramref name="command"/> when it can run (a menu item, a key).</summary>
    public static void Run(ICommand? command, object? parameter = null)
    {
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }

    // ── Collections ──

    /// <summary>
    /// Fills <paramref name="container"/> with a control per item of <paramref name="items"/> (built by
    /// <paramref name="build"/>), again whenever the collection changes; <paramref name="empty"/> is shown when there
    /// is none.
    /// </summary>
    public static T Items<T, TItem>(T container, IEnumerable<TItem> items, Func<TItem, Control> build, Control? empty = null) where T : Godot.Container
    {
        var built = new Dictionary<object, Control>(ReferenceEqualityComparer.Instance);
        void Rebuild()
        {
            if (!GodotObject.IsInstanceValid(container))
                return;
            var list = items.ToList();
            var keep = new HashSet<object>(list.Where(i => i is not null).Cast<object>(), ReferenceEqualityComparer.Instance);
            foreach (var (item, control) in built.ToList())
                if (!keep.Contains(item))
                {
                    built.Remove(item);
                    control.QueueFree();
                }
            var index = 0;
            foreach (var item in list)
            {
                if (item is null)
                    continue;
                if (!built.TryGetValue(item, out var control))
                {
                    control = build(item);
                    built[item] = control;
                    container.AddChild(control);
                }
                container.MoveChild(control, index++);
            }
            if (empty is not null)
                empty.Visible = list.Count == 0;
        }
        if (items is INotifyCollectionChanged observable)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => OnMain(Rebuild);
            observable.CollectionChanged += handler;
            OnGone(container, () => observable.CollectionChanged -= handler);
        }
        Rebuild();
        return container;
    }

    /// <summary>
    /// A drop-down listing <paramref name="items"/> (by <paramref name="label"/>) bound both ways to the selected-item
    /// property <paramref name="selected"/> of <paramref name="source"/>.
    /// </summary>
    /// <summary>
    /// An enumerable read again at each enumeration from <paramref name="source"/>: for <see cref="Options"/> on a
    /// list that a property gives anew each time (the presets of the chat), filled again on <c>itemsProperty</c>.
    /// </summary>
    public static IEnumerable<T> Live<T>(Func<IEnumerable<T>> source)
    {
        foreach (var item in source())
            yield return item;
    }

    /// <summary>
    /// A drop-down of <paramref name="items"/> bound both ways to <paramref name="selected"/>; filled again when
    /// <paramref name="items"/> is an observable collection that changes, when <paramref name="itemsProperty"/> of
    /// <paramref name="source"/> changes (with <see cref="Live"/> items), or when <paramref name="refresh"/>, given
    /// the action that fills it, calls that action.
    /// </summary>
    public static OptionButton Options<TItem>(OptionButton options, IEnumerable<TItem> items, INotifyPropertyChanged source, string selected,
        Func<TItem, string>? label = null, string? itemsProperty = null, Action<Action>? refresh = null)
    {
        var list = new List<TItem>();
        var updating = false;
        void Fill()
        {
            if (!GodotObject.IsInstanceValid(options))
                return;
            updating = true;
            list = items.ToList();
            options.Clear();
            foreach (var item in list)
                options.AddItem(label?.Invoke(item) ?? item?.ToString() ?? string.Empty);
            Select();
            updating = false;
        }
        void Select()
        {
            var value = Get(source, selected);
            var index = list.FindIndex(i => Equals(i, value));
            updating = true;
            options.Selected = index;
            updating = false;
        }
        if (items is INotifyCollectionChanged observable)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => OnMain(Fill);
            observable.CollectionChanged += handler;
            OnGone(options, () => observable.CollectionChanged -= handler);
        }
        When(options, source, Select, selected);
        if (itemsProperty is not null)
            When(options, source, Fill, itemsProperty);
        refresh?.Invoke(() => OnMain(Fill));
        options.ItemSelected += index =>
        {
            if (!updating && index >= 0 && index < list.Count)
                Set(source, selected, list[(int)index]);
        };
        Fill();
        return options;
    }

    /// <summary>
    /// An item list showing <paramref name="items"/> (by <paramref name="label"/>, with an optional tooltip) bound both
    /// ways to the selected-item property <paramref name="selected"/> (when given).
    /// </summary>
    public static ItemList List<TItem>(ItemList view, IEnumerable<TItem> items, Func<TItem, string> label, INotifyPropertyChanged? source = null,
        string? selected = null, Func<TItem, string>? tooltip = null, Func<TItem, Color?>? color = null)
    {
        var list = new List<TItem>();
        var updating = false;
        void Select()
        {
            if (source is null || selected is null)
                return;
            var value = Get(source, selected);
            var index = list.FindIndex(i => ReferenceEquals(i, value) || Equals(i, value));
            updating = true;
            if (index >= 0)
            {
                view.Select(index);
                view.EnsureCurrentIsVisible();
            }
            else
                view.DeselectAll();
            updating = false;
        }
        void Fill()
        {
            if (!GodotObject.IsInstanceValid(view))
                return;
            list = items.ToList();
            view.Clear();
            foreach (var item in list)
            {
                var index = view.AddItem(label(item));
                if (tooltip?.Invoke(item) is { } tip)
                    view.SetItemTooltip(index, tip);
                if (color?.Invoke(item) is { } c)
                    view.SetItemCustomFgColor(index, c);
            }
            Select();
        }
        if (items is INotifyCollectionChanged observable)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => OnMain(Fill);
            observable.CollectionChanged += handler;
            OnGone(view, () => observable.CollectionChanged -= handler);
        }
        if (source is not null && selected is not null)
        {
            When(view, source, Select, selected);
            view.ItemSelected += index =>
            {
                if (!updating && index >= 0 && index < list.Count)
                    Set(source, selected, list[(int)index]);
            };
        }
        Fill();
        return view;
    }

    /// <summary>The item of <paramref name="items"/> at <paramref name="index"/>, or default.</summary>
    public static TItem? At<TItem>(IEnumerable<TItem> items, long index) =>
        index >= 0 ? items.Skip((int)index).FirstOrDefault() : default;
}
