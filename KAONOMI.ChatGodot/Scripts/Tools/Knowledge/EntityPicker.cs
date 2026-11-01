using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The search of the entities of the ontology opened by + in the relation graph (port of <c>EntityPickerWindow</c>), a
/// window of the desktop: a text (Enter or Search), the entities found (their name, type and place in the tree), and
/// Add, which adds the selected one to the graph as an element (a double-click on an entity does the same). The window
/// stays open to add more; Close or Escape closes it.
/// </summary>
internal static class EntityPicker
{
    public static DeckWindow Open(Shell shell, string key,
        Func<string, CancellationToken, Task<(List<OntologyNodeViewModel> Nodes, bool ServerAnswered)>> find,
        Action<OntologyNodeViewModel> add)
    {
        DeckWindow? window = null;
        var term = new LineEdit { PlaceholderText = "Search an entity of the ontology (all fields, accents ignored) · Enter", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var status = Eve.Eve.Text_("Type a text and Enter: the entities found are listed", Eve.Eve.Muted, 12, wrap: true);
        var results = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MaxTextLines = 2, AutoHeight = true, SameColumnWidth = true };
        var addButton = Eve.Eve.Button("ADD", primary: true);
        addButton.CustomMinimumSize = new Vector2(110, 0);
        addButton.Disabled = true;
        var hits = new List<EntitySearchHit>();
        CancellationTokenSource? search = null;

        async Task SearchAsync()
        {
            search?.Cancel();
            var current = search = new CancellationTokenSource();
            var text = term.Text.Trim();
            if (text.Length == 0)
            {
                hits.Clear();
                results.Clear();
                addButton.Disabled = true;
                status.Text = "Type a text to search";
                return;
            }
            status.Text = "Searching…";
            try
            {
                var (nodes, serverAnswered) = await find(text, current.Token);
                if (current.IsCancellationRequested || !GodotObject.IsInstanceValid(results))
                    return;
                hits = nodes.Where(n => !n.IsDeleted).Select(n => new EntitySearchHit(n, OntologyWindowViewModel.PathOf(n))).ToList();
                results.Clear();
                foreach (var hit in hits)
                {
                    var index = results.AddItem(hit.Node.DisplayName + (hit.Node.HasEntityType ? "  · " + hit.Node.EntityType : string.Empty) + "  — " + hit.PathLabel);
                    results.SetItemTooltip(index, hit.PathLabel);
                }
                var count = hits.Count;
                status.Text = (count == 0 ? $"No entity matches \"{text}\"" : $"{count} entit{(count == 1 ? "y" : "ies")} · select one and Add (or double-click it)")
                              + (serverAnswered ? string.Empty : " · server search unavailable (local result)");
                if (count > 0)
                    results.Select(0);
                addButton.Disabled = count == 0;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
            {
                status.Text = "The entities could not be searched: " + ex.Message;
            }
        }

        void AddSelected()
        {
            var selected = results.GetSelectedItems();
            if (selected.Length == 0 || selected[0] >= hits.Count)
                return;
            var hit = hits[selected[0]];
            add(hit.Node);
            status.Text = $"Added to the graph: {hit.Node.DisplayName}";
        }

        term.TextSubmitted += text => { _ = SearchAsync(); };
        results.ItemSelected += _ => addButton.Disabled = false;
        results.ItemActivated += _ => AddSelected();
        addButton.Pressed += AddSelected;
        var close = Ui.Action("CLOSE", () =>
        {
            if (window is not null)
                shell.Deck.Close(window);
        });

        var page = Ui.Margin(Ui.Column(8,
            Ui.Row(8, term, Ui.Action("SEARCH", () => _ = SearchAsync())),
            status,
            results,
            Ui.Row(8, Ui.Spring(), addButton, close)), 14);
        KnowledgeUi.Shortcuts(page, k =>
        {
            if (k.Keycode != Key.Escape || window is null)
                return false;
            shell.Deck.Close(window);
            return true;
        });
        window = shell.OpenControl(key, "Add an entity to the graph", "🔍", page, new Vector2(520, 560));
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(term) && term.IsInsideTree())
                term.GrabFocus();
        }).CallDeferred();
        return window;
    }
}
