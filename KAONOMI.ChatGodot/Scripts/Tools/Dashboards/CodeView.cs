using System;
using System.IO;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// The code of an answer ("show code" of the chat): one tab per block of code (its number and language), the code in
/// the syntax colours of its language (read-only code editor, line numbers, selectable); the details of the selected
/// block and the status in the header, COPY (the whole block to the clipboard), SAVE AS…, and OPEN IN BROWSER for
/// the HTML blocks (as <see cref="HtmlPreview"/>). The window takes the title of the view model.
/// </summary>
[ToolView(typeof(CodeWindowViewModel), "Code", "⌨", 900, 680, PerObject = true)]
public sealed class CodeView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CodeWindowViewModel)viewModel;

        var details = Ui.Label(vm, nameof(vm.SelectedSnippet), EveStyle.Muted, 12, format: v => (v as CodeSnippetViewModel)?.Details ?? string.Empty);
        details.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var browser = Ui.Action("OPEN IN BROWSER", () => OpenInBrowser(vm), tooltip: "Open the HTML code in the default web browser");
        Bind.When(browser, vm, () => browser.Visible = vm.SelectedSnippet?.Snippet.IsHtml == true, nameof(vm.SelectedSnippet));
        var header = Ui.Toolbar(Ui.Row(8,
            details,
            Ui.Label(vm, nameof(vm.Status), EveStyle.Text, 12),
            browser,
            Ui.Action("COPY", () => Copy(vm), tooltip: "Copy all the code to the clipboard"),
            Ui.Action("SAVE AS…", () => SaveAs(vm), primary: true)));

        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var snippet in vm.Snippets)
        {
            var code = CodeEditors.Editor(editable: false);
            code.SyntaxHighlighter = new CodeSyntaxHighlighter(snippet.Snippet.Language);
            code.Text = snippet.Snippet.Code;
            code.Name = $"Snippet{snippet.Index}";
            tabs.AddChild(code);
            tabs.SetTabTitle(tabs.GetTabCount() - 1, snippet.Header);
        }
        if (vm.Snippets.Count == 0)
            tabs.AddChild(Ui.Placeholder("No code"));
        var updating = false;
        Bind.When(tabs, vm, () =>
        {
            var index = vm.SelectedSnippet is { } s ? vm.Snippets.ToList().IndexOf(s) : -1;
            if (index >= 0 && tabs.CurrentTab != index)
            {
                updating = true;
                tabs.CurrentTab = index;
                updating = false;
            }
        }, nameof(vm.SelectedSnippet));
        tabs.TabChanged += index =>
        {
            if (!updating && index >= 0 && index < vm.Snippets.Count)
                vm.SelectedSnippet = vm.Snippets[(int)index];
        };

        var page = Ui.Column(6, header, Ui.FillHeight(Ui.Margin(tabs, 8, 0, 8, 8)));
        DashUi.TitleWhenOpen(page, shell, vm.Title);
        return page;
    }

    private static void Copy(CodeWindowViewModel vm)
    {
        if (vm.SelectedSnippet is not { } selected)
            return;
        Dialogs.Copy(selected.Snippet.Code);
        vm.Status = "Code copied to the clipboard";
        ActivityLog.Info("Code", $"{selected.Snippet.DisplayLanguage} code copied to the clipboard ({selected.Snippet.Code.Length} chars)");
    }

    private static void OpenInBrowser(CodeWindowViewModel vm)
    {
        if (vm.SelectedSnippet is not { } selected)
            return;
        var files = HtmlPreview.Open([selected.Snippet]);
        vm.Status = files.Count > 0 ? "Opened in the browser" : "Could not open the browser";
    }

    private static async void SaveAs(CodeWindowViewModel vm)
    {
        if (vm.SelectedSnippet is not { } selected)
            return;
        var snippet = selected.Snippet;
        try
        {
            var path = await Dialogs.SaveFile("Save code", selected.SuggestedFileName, [$"*{snippet.FileExtension} ; {snippet.DisplayLanguage}", "* ; All files"]);
            if (path is null)
                return;
            await File.WriteAllTextAsync(path, snippet.Code);
            vm.Status = $"Saved: {Path.GetFileName(path)}";
            ActivityLog.Info("Code", $"{snippet.DisplayLanguage} code saved to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            vm.Status = $"Save failed: {ex.Message}";
            ActivityLog.Error("Code", "Code save failed", ex);
        }
    }
}
