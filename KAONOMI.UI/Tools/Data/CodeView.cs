using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The code of an answer ("show code" of the chat): one tab per block of code (its number and language), the code in
/// the syntax colours of its language (read-only, line numbers, selectable); the details of the selected block and the
/// status in the header, COPY (the whole block to the clipboard), SAVE AS…, and OPEN IN BROWSER for the HTML blocks.
/// The window takes the title of the view model.
/// </summary>
[ToolView(typeof(CodeWindowViewModel), "Code", "⌨", 900, 680, PerObject = true)]
public sealed class CodeView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CodeWindowViewModel)viewModel;

        var details = DataUi.Bound(vm, nameof(vm.SelectedSnippet), Ui.Muted, 12, format: v => (v as CodeSnippetViewModel)?.Details ?? string.Empty);
        var browser = DataUi.Action("OPEN IN BROWSER", () => OpenInBrowser(vm), "Open the HTML code in the default web browser");
        Ui.When(browser, vm, () => browser.IsVisible = vm.SelectedSnippet?.Snippet.IsHtml == true, nameof(vm.SelectedSnippet));
        var header = DataUi.Toolbar(DataUi.Bar([], details,
            DataUi.Bound(vm, nameof(vm.Status), Ui.Text, 12),
            browser,
            DataUi.Action("COPY", () => Copy(vm), "Copy all the code to the clipboard"),
            DataUi.Action("SAVE AS…", () => SaveAs(vm), primary: true)));

        var tabs = new TabControl();
        var pages = new System.Collections.Generic.List<TabItem>();
        foreach (var snippet in vm.Snippets)
        {
            var code = CodeSyntax.View();
            code.ShowCode(snippet.Snippet.Language, snippet.Snippet.Code);
            pages.Add(new TabItem { Header = new TextBlock { Text = snippet.Header, FontSize = 12 }, Content = code });
        }
        if (pages.Count == 0)
            pages.Add(new TabItem { Header = "—", Content = DataUi.Placeholder("No code") });
        tabs.ItemsSource = pages;
        var updating = false;
        Ui.When(tabs, vm, () =>
        {
            var index = vm.SelectedSnippet is { } s ? vm.Snippets.ToList().IndexOf(s) : -1;
            if (index >= 0 && tabs.SelectedIndex != index)
            {
                updating = true;
                tabs.SelectedIndex = index;
                updating = false;
            }
        }, nameof(vm.SelectedSnippet));
        tabs.SelectionChanged += (_, _) =>
        {
            if (!updating && tabs.SelectedIndex >= 0 && tabs.SelectedIndex < vm.Snippets.Count)
                vm.SelectedSnippet = vm.Snippets[tabs.SelectedIndex];
        };
        tabs.Margin = new Thickness(8, 4, 8, 8);

        var page = Ui.Dock((header, Dock.Top), (tabs, Dock.Top));
        DataUi.TitleWhenOpen(page, vm.Title);
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
