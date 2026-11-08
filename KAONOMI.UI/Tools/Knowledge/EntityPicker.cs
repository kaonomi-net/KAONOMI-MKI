using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The search of the entities of the ontology opened by + in the relation graph, a window of the desktop: a text (Enter
/// or Search), the entities found (their name, type and place in the tree), and Add, which adds the selected one to the
/// graph as an element (a double-click on an entity does the same). The window stays open to add more; Close or Escape
/// closes it.
/// </summary>
internal static class EntityPicker
{
    public static DeskWindow Open(Shell shell, string key,
        Func<string, CancellationToken, Task<(List<OntologyNodeViewModel> Nodes, bool ServerAnswered)>> find,
        Action<OntologyNodeViewModel> add)
    {
        DeskWindow? window = null;
        var term = new TextBox { PlaceholderText = "Search an entity of the ontology (all fields, accents ignored) · Enter" };
        var status = Ui.Label("Type a text and Enter: the entities found are listed", Ui.Muted, 12, wrap: true);
        var results = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<EntitySearchHit>((hit, _) => hit is null ? new Panel() : new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(2),
                Children =
                {
                    new TextBlock { Text = hit.Node.DisplayName + (hit.Node.HasEntityType ? "  · " + hit.Node.EntityType : string.Empty), Foreground = Ui.TextBright, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = hit.PathLabel, Foreground = Ui.Muted, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = hit.PathLabel }
                }
            })
        };
        var addButton = Ui.Button("ADD", primary: true);
        addButton.MinWidth = 110;
        addButton.IsEnabled = false;
        var hits = new List<EntitySearchHit>();
        CancellationTokenSource? search = null;

        async Task SearchAsync()
        {
            search?.Cancel();
            var current = search = new CancellationTokenSource();
            var text = (term.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                hits = [];
                results.ItemsSource = hits;
                addButton.IsEnabled = false;
                status.Text = "Type a text to search";
                return;
            }
            status.Text = "Searching…";
            try
            {
                var (nodes, serverAnswered) = await find(text, current.Token);
                if (current.IsCancellationRequested)
                    return;
                Ui.OnMain(() =>
                {
                    hits = nodes.Where(n => !n.IsDeleted).Select(n => new EntitySearchHit(n, OntologyWindowViewModel.PathOf(n))).ToList();
                    results.ItemsSource = hits;
                    var count = hits.Count;
                    status.Text = (count == 0 ? $"No entity matches \"{text}\"" : $"{count} entit{(count == 1 ? "y" : "ies")} · select one and Add (or double-click it)")
                                  + (serverAnswered ? string.Empty : " · server search unavailable (local result)");
                    if (count > 0)
                        results.SelectedIndex = 0;
                    addButton.IsEnabled = count > 0;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
            {
                Ui.OnMain(() => status.Text = "The entities could not be searched: " + ex.Message);
            }
        }

        void AddSelected()
        {
            if (results.SelectedItem is not EntitySearchHit hit)
                return;
            add(hit.Node);
            status.Text = $"Added to the graph: {hit.Node.DisplayName}";
        }

        term.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = SearchAsync();
                e.Handled = true;
            }
        };
        results.SelectionChanged += (_, _) => addButton.IsEnabled = results.SelectedItem is not null;
        results.DoubleTapped += (_, _) => AddSelected();
        addButton.Click += (_, _) => AddSelected();
        var close = Ui.Button("CLOSE", () =>
        {
            if (window is not null)
                shell.Deck.Close(window);
        });

        var buttons = Ui.Row(8, addButton, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var page = Ui.Dock(
            (KnowledgeUi.FillRow(8, [], term, Ui.Button("SEARCH", () => _ = SearchAsync())), Dock.Top),
            (status, Dock.Top),
            (buttons, Dock.Bottom),
            (Ui.Box(results, 2, Ui.Field), Dock.Top));
        status.Margin = new Thickness(0, 8);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        page.Margin = new Thickness(14);
        KnowledgeUi.Shortcuts(page, k =>
        {
            if (k.Key != Key.Escape || window is null)
                return false;
            shell.Deck.Close(window);
            return true;
        });
        window = shell.OpenControl(key, "Add an entity to the graph", "🔍", page, new Size(520, 560));
        KnowledgeUi.FocusLater(term);
        return window;
    }
}
