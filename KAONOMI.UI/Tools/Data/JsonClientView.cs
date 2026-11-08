using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /json: an HTTP JSON client. The method (GET / POST / PUT) and the URL (Enter sends), SEND (CANCEL while it runs);
/// OPEN REQUEST… (a saved request of the collection folder), SAVE REQUEST, SAVE RESULT; the request body (disabled for
/// GET) with FORMAT, the response (read-only, JSON colours) with its status and a red / green dot; the status bar with
/// the busy bar.
/// </summary>
[ToolView(typeof(JsonClientWindowViewModel), "HTTP JSON client", "🌐", 1100, 750)]
public sealed class JsonClientView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (JsonClientWindowViewModel)viewModel;

        var method = DataUi.Options(vm.Methods, vm, nameof(vm.Method), minWidth: 100);
        var url = DataUi.FocusWhenShown(DataUi.Line(vm, nameof(vm.Url), vm.UrlPlaceholder));
        DataUi.Enter(url, vm.SendCommand);
        var send = DataUi.Command("SEND", vm.SendCommand, primary: true);
        send.MinWidth = 110;
        send.Visible(vm.SendCommand, "IsRunning", invert: true);
        var cancel = DataUi.Command("CANCEL", vm.SendCancelCommand);
        cancel.MinWidth = 110;
        cancel.Visible(vm.SendCommand, "IsRunning");

        var open = DataUi.Action("OPEN REQUEST…", () => OpenQuery(vm), $"Load a request from {vm.CollectionDirectory}");
        var saveQuery = DataUi.Command("SAVE REQUEST", vm.SaveQueryCommand, $"Saves the method, URL and body to {vm.CollectionDirectory}");
        var saveResult = DataUi.Command("SAVE RESULT", vm.SaveResultCommand, "Saves the response to the result directory configured in config.yml");

        var toolbar = DataUi.Toolbar(Ui.Column(6,
            DataUi.Bar([method], url, send, cancel),
            Ui.Row(6, open, saveQuery, saveResult)));

        // Request.
        var body = CodeSyntax.Editor("Request body (ignored for GET)");
        body.Bind(TextBox.TextProperty, vm, nameof(vm.Body), BindingMode.TwoWay);
        DataUi.Enabled(body, vm, nameof(vm.IsBodyEnabled));
        var requestHeader = DataUi.Bar([], DataUi.Caption("Request (JSON)"), DataUi.Command("FORMAT", vm.FormatBodyCommand));
        requestHeader.Height = 32;
        var request = Ui.Dock((requestHeader, Dock.Top), (body, Dock.Top));

        // Response.
        var response = CodeSyntax.View();
        string? shown = null;
        Ui.When(response, vm, () =>
        {
            if (shown != vm.Response)
            {
                shown = vm.Response;
                response.ShowJson(vm.Response ?? string.Empty);
            }
        }, nameof(vm.Response));
        var responseHeader = DataUi.Bar([], DataUi.Caption("Response"),
            DataUi.Dot(Ui.Red, vm, nameof(vm.IsResponseError)),
            DataUi.Dot(Ui.Green, vm, nameof(vm.IsResponseSuccess)),
            DataUi.Bound(vm, nameof(vm.ResponseStatus), Ui.Text, 12));
        responseHeader.Height = 32;
        var result = Ui.Dock((responseHeader, Dock.Top), (response, Dock.Top));

        // Request and response share the width (the bar between them moves).
        var split = DataUi.SplitH(request, result, 0);
        split.Margin = new Thickness(10, 4, 10, 6);
        return Ui.Dock(
            (toolbar, Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), null, vm.SendCommand), Dock.Bottom),
            (split, Dock.Top));
    }

    /// <summary>OPEN REQUEST…: a saved JSON request of the collection folder.</summary>
    private static async void OpenQuery(JsonClientWindowViewModel vm)
    {
        Directory.CreateDirectory(vm.CollectionDirectory);
        var path = await Dialogs.OpenFile("Open request", ["*.json ; JSON request", "* ; All files"], vm.CollectionDirectory);
        if (path is not null)
            await vm.LoadQueryAsync(path);
    }
}
