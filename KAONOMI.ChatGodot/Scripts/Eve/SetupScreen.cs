using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The screen of the first start (and of <c>/setup</c>): every dependency of the chat (<see cref="SetupCheck"/>, plus the
/// shaders and the renderer of this Godot version) in a list, with its state (✅ ⚠ ❌), what was found or what is
/// missing, and for what can be installed from here a button (DOWNLOAD, INSTALL, PULL…) whose output goes to the log at
/// the bottom; otherwise the command to run (COPY) or the page to open (OPEN). DONE keeps the screen from coming back at
/// the next start; CHECK AGAIN runs the checks again.
/// </summary>
public static class SetupScreen
{
    public const string Key = "setup";
    public const string Icon = "🧰";

    public static Control Build(ChatSettings settings, Action done)
    {
        var check = new SetupCheck(settings);
        check.Dependencies.Add(new Dependency { Group = SetupCheck.Animations, Name = "Shaders of the 3D desk", Check = CheckShadersAsync });
        check.Dependencies.Add(new Dependency { Group = SetupCheck.Animations, Name = "Renderer", Check = CheckRendererAsync });

        var list = Ui.Column(6);
        var rows = new Dictionary<Dependency, Row>();
        string? group = null;
        foreach (var dependency in check.Dependencies.OrderBy(d => GroupOrder(d.Group)))
        {
            if (dependency.Group != group)
            {
                group = dependency.Group;
                list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
                list.AddChild(Eve.Caption(group.ToUpperInvariant(), Eve.Cyan, 11));
            }
            var row = new Row(dependency);
            rows[dependency] = row;
            list.AddChild(row.Control);
        }

        var log = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(0, 120), WrapMode = TextEdit.LineWrappingMode.Boundary };
        log.AddThemeFontOverride("font", Eve.Mono);
        log.AddThemeFontSizeOverride("font_size", 11);
        var summary = Eve.Text_("Checking…", Eve.Muted, 12, wrap: true);

        CancellationTokenSource? running = null;
        async Task RunChecks()
        {
            running?.Cancel();
            running = new CancellationTokenSource();
            var token = running.Token;
            summary.Text = "Checking…";
            try
            {
                await check.RunAsync(d => Bind.OnMain(() => { if (GodotObject.IsInstanceValid(rows[d].Control)) rows[d].Refresh(); }), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested)
                return;
            Bind.OnMain(() => { if (GodotObject.IsInstanceValid(summary)) summary.Text = Summary(check.Dependencies); });
            // The result in the activity log too (the lines that are not OK), to read it after the fact.
            ActivityLog.Info("Setup", Summary(check.Dependencies));
            foreach (var dependency in check.Dependencies.Where(d => d.State != DependencyState.Ok))
                ActivityLog.Info("Setup", $"{(dependency.State == DependencyState.Missing ? "missing" : "warning")} · {dependency.Name}: {dependency.Detail}");
        }
        // The log written only while the screen is there (an install goes on in the background after it is closed).
        void Say(string line) => Bind.OnMain(() => { if (GodotObject.IsInstanceValid(log)) Append(log, line); });
        // The installs are stopped with the screen (DONE, ✕, a change of theme).
        var closing = new CancellationTokenSource();
        foreach (var row in rows.Values)
            row.FixRequested += async dependency =>
            {
                Say($"▸ {dependency.Name}: {dependency.FixLabel}…");
                row.Busy(true);
                try
                {
                    var progress = new Progress<string>(Say);
                    await dependency.Fix!(progress, closing.Token);
                    Say($"✓ {dependency.Name}: done");
                }
                catch (OperationCanceledException)
                {
                    ActivityLog.Info("Setup", $"{dependency.Name}: the install was stopped with the setup screen");
                    return;
                }
                catch (Exception ex)
                {
                    Say($"✗ {dependency.Name}: {ex.Message}");
                }
                Bind.OnMain(() => { if (GodotObject.IsInstanceValid(row.Control)) row.Busy(false); });
                if (!closing.IsCancellationRequested)
                    await RunChecks();
            };

        var again = Ui.Action("CHECK AGAIN", () => _ = RunChecks(), tooltip: "Runs every check again (after an installation made by hand)");
        var finish = Ui.Action("DONE", () =>
        {
            Preferences.SetupDone = true;
            running?.Cancel();
            done();
        }, primary: true, tooltip: "Closes this screen; it does not come back at the next start (/setup opens it again)");
        var header = Ui.Column(4,
            Eve.Caption("The dependencies of the chat", Eve.TextBright, 13),
            Eve.Text_("Speech to text, text to speech, the 3D animations and the services, checked at the first start. A button installs what can be installed from here; otherwise the line says what to do by hand (COPY copies the command, OPEN opens the page).", Eve.Muted, 12, wrap: true));
        var bottom = Ui.Row(8, Ui.Fill(summary), again, finish);
        var page = Ui.Column(10, header, Ui.Fill(Ui.Scroll(list), vertical: true), log, bottom);
        var root = Ui.Margin(page, 8);
        Bind.OnGone(root, () =>
        {
            running?.Cancel();
            closing.Cancel();
        });
        _ = RunChecks();
        return root;
    }

    private static int GroupOrder(string group) => group switch
    {
        SetupCheck.SpeechToText => 0,
        SetupCheck.TextToSpeech => 1,
        SetupCheck.Animations => 2,
        _ => 3
    };

    private static string Summary(IEnumerable<Dependency> dependencies)
    {
        var all = dependencies.ToList();
        var ok = all.Count(d => d.State == DependencyState.Ok);
        var missing = all.Count(d => d.State == DependencyState.Missing);
        var warnings = all.Count(d => d.State == DependencyState.Warning);
        return missing == 0 && warnings == 0
            ? $"Everything is there ({ok} checks)."
            : $"{ok} OK · {missing} missing · {warnings} warning(s)";
    }

    private static void Append(TextEdit log, string line)
    {
        log.Text += line + "\n";
        log.SetCaretLine(log.GetLineCount() - 1);
    }

    private static Task CheckShadersAsync(Dependency d, CancellationToken ct)
    {
        var folder = ProjectSettings.GlobalizePath("res://Shaders");
        var count = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.gdshader").Count() : 0;
        if (count > 0)
            d.Set(DependencyState.Ok, $"{count} shader(s) in {folder}");
        else
            d.Set(DependencyState.Missing, $"no shader in {folder}", "The Shaders/ folder of KAONOMI.ChatGodot is missing: get the project again");
        return Task.CompletedTask;
    }

    private static Task CheckRendererAsync(Dependency d, CancellationToken ct)
    {
        var adapter = RenderingServer.GetVideoAdapterName();
        var api = RenderingServer.GetVideoAdapterApiVersion();
        d.Set(string.IsNullOrEmpty(adapter) ? DependencyState.Warning : DependencyState.Ok,
            string.IsNullOrEmpty(adapter) ? "no video adapter reported" : $"{adapter} ({api})");
        return Task.CompletedTask;
    }

    /// <summary>One line of the list: the state, the name and the detail, the hint, and the buttons.</summary>
    private sealed class Row
    {
        private readonly Dependency _dependency;
        private readonly Label _state;
        private readonly Label _name;
        private readonly Label _detail;
        private readonly Label _hint;
        private readonly Button _fix;
        private readonly Button _copy;
        private readonly Button _open;

        public event Action<Dependency>? FixRequested;

        public Control Control { get; }

        public Row(Dependency dependency)
        {
            _dependency = dependency;
            _state = new Label { CustomMinimumSize = new Vector2(26, 0), HorizontalAlignment = HorizontalAlignment.Center };
            _state.AddThemeFontSizeOverride("font_size", 15);
            _name = Eve.Text_(dependency.Name, Eve.TextBright, 13);
            _detail = Eve.Text_(string.Empty, Eve.Muted, 12, wrap: true);
            _hint = Eve.Text_(string.Empty, Eve.Amber, 11, wrap: true);
            _hint.AddThemeFontOverride("font", Eve.Mono);
            _fix = Eve.Button("INSTALL", primary: true);
            _fix.Pressed += () => FixRequested?.Invoke(_dependency);
            _copy = Eve.Button("COPY", tooltip: "Copies the command to the clipboard");
            _copy.Pressed += () => DisplayServer.ClipboardSet(_dependency.Hint ?? string.Empty);
            _open = Eve.Button("OPEN", tooltip: "Opens the page in the browser");
            _open.Pressed += () =>
            {
                if (_dependency.Link is { } link)
                    Links.Open(link);
            };
            var texts = Ui.Fill(Ui.Column(2, _name, _detail, _hint));
            var buttons = Ui.Row(6, _fix, _copy, _open);
            buttons.SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkBegin;
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", Eve.Box(Eve.Field, Eve.Edge, 8));
            panel.AddChild(Ui.Row(8, _state, texts, buttons));
            Control = panel;
            Refresh();
        }

        public void Refresh()
        {
            _state.Text = _dependency.State switch
            {
                DependencyState.Ok => "✅",
                DependencyState.Warning => "⚠",
                DependencyState.Missing => "❌",
                _ => "⏳"
            };
            _detail.Text = _dependency.Detail;
            _hint.Text = _dependency.Hint ?? string.Empty;
            _hint.Visible = _dependency.Hint is not null;
            _fix.Text = _dependency.FixLabel ?? "INSTALL";
            _fix.Visible = _dependency.Fix is not null;
            _copy.Visible = _dependency.Hint is not null && _dependency.State != DependencyState.Ok;
            _open.Visible = _dependency.Link is not null && _dependency.State != DependencyState.Ok;
        }

        public void Busy(bool busy)
        {
            _fix.Disabled = busy;
            _fix.Text = busy ? "…" : _dependency.FixLabel ?? "INSTALL";
        }
    }
}
