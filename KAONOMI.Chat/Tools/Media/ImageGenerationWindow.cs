using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

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
        var prompt = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 84,
            PlaceholderText = "Describe the image, in English, e.g. \"a clean diagram of a right triangle with its angles, white background, flat style\""
        };
        var size = new ComboBox { ItemsSource = Array.ConvertAll(Sizes, s => s.Replace('*', '×')), SelectedIndex = 0, MinWidth = 140 };
        var performance = new ComboBox { ItemsSource = Performances, SelectedIndex = 0, MinWidth = 150 };
        ToolTip.SetTip(performance, "Speed: about a minute; Quality: twice as long; Extreme Speed, Lightning, Hyper-SD: a few seconds");
        var run = Ui.Button("✨ GENERATE", null, "Ask Fooocus for the image (Ctrl / ⌘ + Enter)", primary: true);
        run.VerticalAlignment = VerticalAlignment.Bottom;
        var image = new Image { Stretch = Stretch.Uniform };
        var placeholder = Kit.Placeholder("The image shows here; generate it again until it fits, then Insert");
        var status = Ui.Label(string.Empty, Ui.Muted, 11, wrap: true);
        var progress = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 4, MinHeight = 4 };
        var insert = Ui.Button("INSERT");
        insert.IsEnabled = false;
        var cancel = Ui.Button("CANCEL");

        string? path = null;
        string? inserted = null;
        var generated = new List<string>();
        CancellationTokenSource? running = null;
        var elapsed = new Stopwatch();
        var frame = new Border
        {
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            MinHeight = 240,
            Child = new Grid { Children = { image, placeholder } }
        };

        var options = Ui.Row(8, Ui.Caption("Size"), size, Ui.Caption("Performance"), performance);
        options.Children[2].Margin = new Thickness(8, 0, 0, 0);
        var buttons = Kit.FillRow(8, [], status, insert, cancel);
        var page = Kit.Rows("Auto,Auto,Auto,Auto,*,Auto",
            Ui.Caption("Prompt"),
            Kit.FillRow(8, [], prompt, run),
            options,
            progress,
            frame,
            buttons);
        page.RowSpacing = 8;
        var root = Kit.Margin(page, 12);

        // The clock of the generation.
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => status.Text = $"✨ Fooocus is drawing the image… {elapsed.Elapsed:m\\:ss}";

        async void Generate()
        {
            if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                return;
            running = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            run.IsEnabled = false;
            progress.IsVisible = true;
            root.Cursor = new Cursor(StandardCursorType.Wait);
            elapsed.Restart();
            clock.Start();
            status.Text = "✨ Fooocus is drawing the image… 0:00";
            try
            {
                path = await generate(prompt.Text.Trim(), Sizes[Math.Max(0, size.SelectedIndex)], Performances[Math.Max(0, performance.SelectedIndex)], running.Token);
                generated.Add(path);
                var picture = path;
                var loaded = await Task.Run(() => ImageKit.Load(picture));
                image.Source = loaded;
                placeholder.IsVisible = loaded is null;
                insert.IsEnabled = true;
                insert.Classes.Add("primary");
                status.Text = $"✓ Image ready in {elapsed.Elapsed.TotalSeconds:0} s, saved as {System.IO.Path.GetFileName(path)}: Insert it, or generate again";
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException
                                           or System.Text.Json.JsonException or System.IO.IOException or FormatException)
            {
                status.Text = ex is OperationCanceledException ? "Stopped." : $"Not generated: {ex.Message}";
            }
            finally
            {
                running?.Dispose();
                running = null;
                clock.Stop();
                run.IsEnabled = true;
                progress.IsVisible = false;
                root.Cursor = Cursor.Default;
            }
        }

        DeskWindow? window = null;
        void Finish(string? result)
        {
            if (!done.TrySetResult(result))
                return;
            inserted = result;
            if (window is not null)
                shell.Deck.Close(window);
        }

        run.Click += (_, _) => Generate();
        insert.Click += (_, _) => Finish(path);
        cancel.Click += (_, _) => Finish(null);
        Kit.Keys(root, e =>
        {
            if (e.Key == Key.Enter && Kit.CommandHeld(e.KeyModifiers))
            {
                Generate();
                return true;
            }
            if (e.Key != Key.Escape)
                return false;
            Finish(null);
            return true;
        });

        window = shell.OpenControl($"image-generation#{Guid.NewGuid():N}", title, "✨", root, new Size(860, 820));
        window.Closed += () =>
        {
            clock.Stop();
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
        Kit.FocusWhenShown(prompt);
        return done.Task;
    }
}
