using System.IO;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /json: an HTTP JSON client. The method (GET / POST / PUT) and the URL (Enter sends), SEND (CANCEL while it runs);
/// OPEN REQUEST… (a saved request of the collection folder), SAVE REQUEST, SAVE RESULT; the request body (JSON
/// colours, disabled for GET) with FORMAT, the response (read-only, JSON colours) with its status and a red / green
/// dot; the status bar with the busy bar.
/// </summary>
[ToolView(typeof(JsonClientWindowViewModel), "HTTP JSON client", "🌐", 1100, 750)]
public sealed class JsonClientView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (JsonClientWindowViewModel)viewModel;

        var method = Bind.Options(new OptionButton { CustomMinimumSize = new Vector2(100, 0) }, vm.Methods, vm, nameof(vm.Method));
        var url = Ui.Line(vm, nameof(vm.Url), vm.UrlPlaceholder);
        url.TextSubmitted += _ => Bind.Run(vm.SendCommand);
        var send = Ui.Command("SEND", vm.SendCommand, primary: true);
        send.CustomMinimumSize = new Vector2(110, 0);
        Bind.Visible(send, vm.SendCommand, "IsRunning", invert: true);
        var cancel = Ui.Command("CANCEL", vm.SendCancelCommand);
        cancel.CustomMinimumSize = new Vector2(110, 0);
        Bind.Visible(cancel, vm.SendCommand, "IsRunning");

        var open = Ui.Action("OPEN REQUEST…", () => OpenQuery(vm), tooltip: $"Load a request from {vm.CollectionDirectory}");
        var saveQuery = Ui.Command("SAVE REQUEST", vm.SaveQueryCommand, tooltip: $"Saves the method, URL and body to {vm.CollectionDirectory}");
        var saveResult = Ui.Command("SAVE RESULT", vm.SaveResultCommand, tooltip: "Saves the response to the result directory configured in config.yml");

        var toolbar = Ui.Toolbar(Ui.Column(6,
            Ui.Row(8, method, url, send, cancel),
            Ui.Row(6, open, saveQuery, saveResult)));

        // Request.
        var body = CodeEditors.Editor();
        body.SyntaxHighlighter = CodeEditors.Json();
        body.PlaceholderText = "Request body (ignored for GET)";
        Bind.Edit(body, vm, nameof(vm.Body));
        Bind.Enabled(body, vm, nameof(vm.IsBodyEnabled));
        Bind.When(body, vm, () => body.Modulate = vm.IsBodyEnabled ? Colors.White : new Color(1, 1, 1, 0.5f), nameof(vm.IsBodyEnabled));
        var requestHeader = Ui.Row(8, Ui.Fill(EveStyle.Caption("Request (JSON)")), Ui.Command("FORMAT", vm.FormatBodyCommand));
        requestHeader.CustomMinimumSize = new Vector2(0, 32);
        var request = Ui.Column(6, requestHeader, body);

        // Response.
        var response = CodeEditors.Editor(editable: false);
        response.SyntaxHighlighter = CodeEditors.Json();
        Bind.When(response, vm, () =>
        {
            if (response.Text != vm.Response)
                response.Text = vm.Response;
        }, nameof(vm.Response));
        var responseHeader = Ui.Row(6,
            Ui.Fill(EveStyle.Caption("Response")),
            DashUi.Dot(EveStyle.Red, vm, nameof(vm.IsResponseError)),
            DashUi.Dot(EveStyle.Green, vm, nameof(vm.IsResponseSuccess)),
            Ui.Label(vm, nameof(vm.ResponseStatus), EveStyle.Text, 12));
        responseHeader.CustomMinimumSize = new Vector2(0, 32);
        var result = Ui.Column(6, responseHeader, response);

        // Request and response share the width (the bar between them moves).
        var split = Ui.SplitH(Ui.Fill(request), Ui.Fill(result), 0);
        var page = Ui.Column(6,
            toolbar,
            Ui.FillHeight(Ui.Margin(split, 10, 4, 10, 6)),
            DashUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), vm.SendCommand));
        page.Ready += () => url.CallDeferred(Control.MethodName.GrabFocus);
        return page;
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
