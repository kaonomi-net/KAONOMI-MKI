using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;

// The few types of the views of the Avalonia chat that its view models use: given here without their Avalonia
// controls, so that the view models are shared as they are (linked sources) with this Godot version.
namespace KAONOMI.Chat.Views
{
    /// <summary>A function drawn by the graph of /work.</summary>
    public sealed record GraphFunction(string Label, Func<double, double> Evaluate, Color Color);

    /// <summary>A slice of the pie of the languages of /completion.</summary>
    public sealed record PieSlice(string Label, int Value, IBrush Brush)
    {
        public double Share { get; init; }
    }

    /// <summary>What a window says about itself at one end of a link (links are not drawn in this version).</summary>
    public sealed record LinkEnd(string WindowName, string? ObjectId, string Context);

    /// <summary>A YouTube player as an end of a link: never linking in this version (no red string).</summary>
    public sealed class PlayerAnchor
    {
        public bool Linking => false;
    }

    /// <summary>The players reported by the YouTube page: nothing is kept (no red string in this version).</summary>
    public static class LinkAnchors
    {
        public static PlayerAnchor Report(string id, string videoId, string? title, PixelRect rect) => new();
        public static PlayerAnchor? Player(string id) => null;
    }

    /// <summary>The red string of the links: removed from this version.</summary>
    public static class LinkString
    {
        public static bool IsActive => false;
        public static void Start(PlayerAnchor anchor)
        {
        }
    }

    /// <summary>The image lines of the Markdown of the journal and of the tape of /work (the parts without controls).</summary>
    public static class JournalMarkdown
    {
        public static readonly Regex ImageLine = new(
            @"^[ \t]*!\[(?<alt>[^\]|]*)(\|(?<width>\d+))?\]\((?<url><[^>\n]+>|[^)\s]+)\)[ \t]*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public const int DefaultImageWidth = 480;

        public static string LocalPath(string url)
        {
            var path = url.Trim().TrimStart('<').TrimEnd('>');
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
                return uri.LocalPath;
            return path;
        }

        public static string ImageMarkdown(string path, int width)
        {
            var alt = System.IO.Path.GetFileNameWithoutExtension(path).Replace("]", string.Empty).Replace("|", string.Empty);
            var link = path.Contains(' ') || path.Contains(')') ? $"<{path}>" : path;
            return $"![{alt}|{width}]({link})";
        }

        public static string SetImageWidth(string markdown, int index, int width)
        {
            var count = 0;
            return ImageLine.Replace(markdown, match =>
            {
                if (count++ != index)
                    return match.Value;
                var indent = match.Value[..(match.Value.Length - match.Value.TrimStart().Length)];
                return $"{indent}![{match.Groups["alt"].Value}|{width}]({match.Groups["url"].Value})";
            });
        }
    }
}

namespace KAONOMI.Chat.Themes
{
    /// <summary>The themes of the chat: this version has its own look (Eve), the names stay known.</summary>
    public enum AppThemeKind
    {
        Default, Kaonomi, Black, Cyberpunk, Matrix, Apple, Windows, Linux, Glass, Swift, Mari, Jarvis, Kandinsky, Pepper, Hal9000, Steam, Eve,
        Ghost,
        Analyst,
        Minimal,
        Defense,
        Police,
        Trading
    }

    /// <summary>The theme in use; this version draws one look only, the choice is kept in config.yml.</summary>
    public static class AppTheme
    {
        public static IReadOnlyList<(AppThemeKind Kind, string Description)> All { get; } =
        [
            (AppThemeKind.Eve, "Dark · deep space and translucent panels, in the style of EVE Online"),
            (AppThemeKind.Kaonomi, "Light · white paper, turquoise and gold, in the style of www.kaonomi.net"),
            (AppThemeKind.Matrix, "Dark · the digital rain, phosphor green on black and the monospace of a terminal, in the style of the film"),
            (AppThemeKind.Ghost, "Dark · Niihama at night from a rooftop, cyan and magenta neons in the rain, after Ghost in the Shell"),
            (AppThemeKind.Analyst, "Dark · the desk of an intelligence analyst: low contrast slate, steel blue and muted amber, made for long hours"),
            (AppThemeKind.Minimal, "Light · white and light greys, anthracite text and a single black accent, nothing more"),
            (AppThemeKind.Defense, "Dark · a command post: olive and khaki, sand text, tactical green and signal amber on a topographic map"),
            (AppThemeKind.Police, "Dark · a dispatch center at night: navy blue, police blue and badge gold, patrols and incidents on the city map"),
            (AppThemeKind.Trading, "Dark · a trading floor of crypto and stocks: graphite, exchange yellow, green rises and red falls, candlesticks scrolling")
        ];

        public static AppThemeKind Current { get; private set; } = AppThemeKind.Eve;

        /// <summary>The theme shown (set by the shell when it applies one).</summary>
        public static void SetCurrent(AppThemeKind kind) => Current = kind;

        /// <summary>Raised when a theme is applied.</summary>
        public static event Action<AppThemeKind>? Applied;

        public static string ToName(AppThemeKind kind) => kind == AppThemeKind.Default ? "white" : kind.ToString().ToLowerInvariant();

        public static bool TryParse(string? name, out AppThemeKind kind)
        {
            kind = AppThemeKind.Default;
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || char.IsDigit(trimmed[0]))
                return false;
            if (trimmed.Equals("white", StringComparison.OrdinalIgnoreCase))
                return true;
            return Enum.TryParse(trimmed, ignoreCase: true, out kind) && Enum.IsDefined(kind);
        }

        public static void Apply(AppThemeKind kind)
        {
            Current = kind;
            Applied?.Invoke(kind);
        }
    }
}
