using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Images;

/// <summary>
/// The window of ✨🖼 of the calculator tape (port of <c>ImageGenerationWindow</c> of the Avalonia chat): a prompt, the
/// size and the performance; ✨ Generate (Ctrl / ⌘ + Enter) asks Fooocus through the given function and shows the
/// image, which can be generated again until Insert. <see cref="Show"/> gives the path of the inserted image, null when
/// cancelled (Cancel, Esc or the close button of the window). The images generated but not inserted are deleted when the
/// window closes, so that only the inserted one stays in the folder.
/// </summary>
public static class ImageGenerationWindow
{
    /// <summary>Sizes offered, width*height (the ones of SDXL).</summary>
    private static readonly string[] Sizes = ["1152*896", "1024*1024", "896*1152", "1344*768", "768*1344", "1216*832", "832*1216"];

    /// <summary>Performances of Fooocus; the fast ones download a LoRA the first time.</summary>
    private static readonly string[] Performances = ["Speed", "Quality", "Extreme Speed", "Lightning", "Hyper-SD"];

    /// <summary>
    /// Opens the window (in front on the desktop) whose ✨ Generate calls <paramref name="generate"/> with the prompt,
    /// the size and the performance, and gets back the path of the image saved; completes with the inserted path.
    /// </summary>
    public static Task<string?> Show(Shell shell, string title, Func<string, string, string, CancellationToken, Task<string>> generate)
    {
        var done = new TaskCompletionSource<string?>();
        var prompt = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 84),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            PlaceholderText = "Describe the image, in English, e.g. \"a clean diagram of a right triangle with its angles, white background, flat style\""
        };
        var size = new OptionButton { CustomMinimumSize = new Vector2(140, 0) };
        foreach (var s in Sizes)
            size.AddItem(s.Replace('*', '×'));
        size.Selected = 0;
        var performance = new OptionButton { CustomMinimumSize = new Vector2(150, 0), TooltipText = "Speed: about a minute; Quality: twice as long; Extreme Speed, Lightning, Hyper-SD: a few seconds" };
        foreach (var p in Performances)
            performance.AddItem(p);
        performance.Selected = 0;
        var run = Eve.Eve.Button("✨ GENERATE", primary: true, tooltip: "Ask Fooocus for the image (Ctrl / ⌘ + Enter)");
        run.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        var image = Ui.Picture(minHeight: 240);
        image.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        image.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var placeholder = Ui.Placeholder("The image shows here; generate it again until it fits, then Insert");
        placeholder.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var status = Eve.Eve.Text_("", Eve.Eve.Muted, 11, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var progress = new ProgressBar { Indeterminate = true, ShowPercentage = false, Visible = false, CustomMinimumSize = new Vector2(0, 4) };
        var insert = Eve.Eve.Button("INSERT");
        insert.Disabled = true;
        var cancel = Eve.Eve.Button("CANCEL");

        string? path = null;
        string? inserted = null;
        var generated = new List<string>();
        CancellationTokenSource? running = null;
        var elapsed = new Stopwatch();
        var frame = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.3f), Eve.Eve.Edge with { A = 0.3f }, 0));
        var area = new Control();
        area.AddChild(image);
        area.AddChild(placeholder);
        frame.AddChild(area);

        var options = Ui.Row(8, Eve.Eve.Caption("Size"), size, Ui.Margin(Eve.Eve.Caption("Performance"), 8, 0, 0, 0), performance);
        foreach (var child in options.GetChildren())
            if (child is Control c)
                c.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var buttons = Ui.Row(8, status, insert, cancel);
        var page = Ui.Column(8, Eve.Eve.Caption("Prompt"), Ui.Row(8, prompt, run), options, progress, frame, buttons);

        // The clock of the generation.
        var clock = new Godot.Timer { WaitTime = 1, OneShot = false };
        clock.Timeout += () => status.Text = $"✨ Fooocus is drawing the image… {elapsed.Elapsed:m\\:ss}";
        page.AddChild(clock);

        async void Generate()
        {
            if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                return;
            running = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            run.Disabled = true;
            progress.Visible = true;
            page.MouseDefaultCursorShape = Control.CursorShape.Busy;
            elapsed.Restart();
            clock.Start();
            status.Text = "✨ Fooocus is drawing the image… 0:00";
            try
            {
                path = await generate(prompt.Text.Trim(), Sizes[Math.Max(0, size.Selected)], Performances[Math.Max(0, performance.Selected)], running.Token);
                generated.Add(path);
                var picture = path;
                var loaded = await Task.Run(() => LoadImage(picture));
                var texture = loaded is null ? null : ImageTexture.CreateFromImage(loaded);
                if (!GodotObject.IsInstanceValid(image))
                    return;
                image.Texture = texture;
                placeholder.Visible = texture is null;
                insert.Disabled = false;
                Eve.Eve.StylePrimary(insert);
                status.Text = $"✓ Image ready in {elapsed.Elapsed.TotalSeconds:0} s, saved as {System.IO.Path.GetFileName(path)}: Insert it, or generate again";
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException
                                           or System.Text.Json.JsonException or System.IO.IOException or FormatException)
            {
                if (GodotObject.IsInstanceValid(status))
                    status.Text = ex is OperationCanceledException ? "Stopped." : $"Not generated: {ex.Message}";
            }
            finally
            {
                running?.Dispose();
                running = null;
                if (GodotObject.IsInstanceValid(page))
                {
                    clock.Stop();
                    run.Disabled = false;
                    progress.Visible = false;
                    page.MouseDefaultCursorShape = Control.CursorShape.Arrow;
                }
            }
        }

        DeckWindow? window = null;
        void Finish(string? result)
        {
            if (!done.TrySetResult(result))
                return;
            inserted = result;
            if (window is not null)
                shell.Deck.Close(window);
        }

        run.Pressed += Generate;
        prompt.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && ImageKit.Command(key))
            {
                prompt.AcceptEvent();
                Generate();
            }
        };
        insert.Pressed += () => Finish(path);
        cancel.Pressed += () => Finish(null);
        ImageKit.Keys(page, shell, key =>
        {
            if (key.Keycode != Key.Escape)
                return false;
            Finish(null);
            return true;
        });

        window = shell.OpenControl($"image-generation#{Guid.NewGuid():N}", title, "✨", page, new Vector2(860, 820));
        window.Closed += () =>
        {
            done.TrySetResult(null);
            running?.Cancel();
            foreach (var unused in generated)
                if (unused != inserted)
                    try
                    {
                        System.IO.File.Delete(unused);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        ActivityLog.Error("Calculator", $"Unused generated image not deleted: {unused}", ex);
                    }
        };
        Callable.From(() => prompt.GrabFocus()).CallDeferred();
        return done.Task;
    }

    /// <summary>The picture at <paramref name="path"/>, 1400 pixels at most, as a Godot image (read in the background).</summary>
    private static Image? LoadImage(string path)
    {
        var image = new Image();
        if (image.Load(path) != Error.Ok)
            return null;
        if (Math.Max(image.GetWidth(), image.GetHeight()) > 1400)
        {
            var scale = 1400f / Math.Max(image.GetWidth(), image.GetHeight());
            image.Resize((int)(image.GetWidth() * scale), (int)(image.GetHeight() * scale), Image.Interpolation.Bilinear);
        }
        return image;
    }
}
