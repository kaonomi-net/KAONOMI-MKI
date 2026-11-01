namespace KAONOMI.Core.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// Look and feel of the Kaonomi console applications, chosen by <c>consoleTheme</c> in config.json:
    /// <list type="bullet">
    /// <item><c>matrix</c>: black screen, phosphor green text, digital rain intro.</item>
    /// <item><c>jarvis</c>: Iron Man / J.A.R.V.I.S. HUD, navy screen, arc reactor blue text with gold accents,
    /// arc reactor boot animation and system diagnostics.</item>
    /// <item><c>ubuntu</c>: Ubuntu terminal, aubergine screen, Ubuntu orange accents, boot splash with the loading dots,
    /// systemd boot messages, message-of-the-day banner and <c>user@host:~$</c> prompt.</item>
    /// <item>any other value: the classic console (plain text, default colors).</item>
    /// </list>
    /// Themed consoles are 30 % transparent. The intro is skipped when the output is redirected, when a key is pressed,
    /// or when the environment variable <c>KAONOMI_NO_INTRO</c> is set.
    /// </summary>
    public static class HelperConsoleTheme
    {
        /// <summary>Transparency of a themed console window (0 = opaque, 0.3 = 30 % transparent).</summary>
        public const double Transparency = 0.30;

        private const string Esc = "\u001b";

        private static readonly string[] Logo =
        [
            "██╗  ██╗ █████╗  ██████╗ ███╗   ██╗ ██████╗ ███╗   ███╗██╗",
            "██║ ██╔╝██╔══██╗██╔═══██╗████╗  ██║██╔═══██╗████╗ ████║██║",
            "█████╔╝ ███████║██║   ██║██╔██╗ ██║██║   ██║██╔████╔██║██║",
            "██╔═██╗ ██╔══██║██║   ██║██║╚██╗██║██║   ██║██║╚██╔╝██║██║",
            "██║  ██╗██║  ██║╚██████╔╝██║ ╚████║╚██████╔╝██║ ╚═╝ ██║██║",
            "╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝ ╚═╝  ╚═══╝ ╚═════╝ ╚═╝     ╚═╝╚═╝",
        ];

        /// <summary>
        /// Colors and texts of a theme. <see cref="Title"/> is a format string: {0} application, {1} user, {2} machine.
        /// <see cref="Palette"/> redefines console colors (ANSI palette index, #RRGGBB) where the terminal allows it.
        /// </summary>
        private sealed record Theme(
            string Name,
            ConsoleColor Text,
            ConsoleColor Dim,
            ConsoleColor Glow,
            ConsoleColor Accent,
            string DefaultForeground,
            string DefaultBackground,
            string Title,
            string Farewell,
            (int Index, string Color)[]? Palette = null);

        private static readonly Theme Matrix = new(
            "matrix", ConsoleColor.Green, ConsoleColor.DarkGreen, ConsoleColor.White, ConsoleColor.Green,
            "#00FF41", "#000000", "{0} :: the Matrix has you", "Goodbye! Disconnecting from the Matrix...");

        private static readonly Theme Jarvis = new(
            "jarvis", ConsoleColor.Cyan, ConsoleColor.DarkCyan, ConsoleColor.White, ConsoleColor.Yellow,
            "#7FDBFF", "#050B14", "{0} :: J.A.R.V.I.S. online", "Powering down. Goodbye, {0}.");

        // Ubuntu: aubergine background, Ubuntu orange (DarkYellow), Tango colors of the Ubuntu terminal for the prompt.
        private static readonly Theme Ubuntu = new(
            "ubuntu", ConsoleColor.White, ConsoleColor.DarkGray, ConsoleColor.White, ConsoleColor.DarkYellow,
            "#FFFFFF", "#300A24", "{1}@{2}: ~/{0}", "logout",
            [(3, "#E95420"), (5, "#77216F"), (8, "#A08A98"), (9, "#EF2929"), (10, "#8AE234"), (11, "#FCE94F"), (12, "#729FCF")]);

        private static readonly object Sync = new();
        private static Theme? _theme;
        private static bool _ansi;
        private static bool _skip;

        /// <summary>Name of the applied theme (<c>matrix</c>, <c>jarvis</c>, <c>ubuntu</c>), or <c>classic</c>.</summary>
        public static string Current => _theme?.Name ?? "classic";

        /// <summary>True when a theme is applied (see <see cref="Start"/>).</summary>
        public static bool IsThemed => _theme is not null;

        /// <summary>Goodbye message of the current theme.</summary>
        public static string Farewell => string.Format(_theme?.Farewell ?? "Goodbye!", Environment.UserName);

        /// <summary>
        /// Applies the theme named by <paramref name="consoleTheme"/> (colors, transparency, title, intro and banner), or
        /// prints the classic start lines when the value is not a known theme. Call it once, at the start of <c>Main</c>.
        /// </summary>
        public static void Start(string appName, string appDescription, string documentationUrl, string? consoleTheme)
        {
            _theme = consoleTheme?.Trim().ToLowerInvariant() switch
            {
                "matrix" => Matrix,
                "jarvis" => Jarvis,
                "ubuntu" => Ubuntu,
                _ => null,
            };

            if (_theme is null)
            {
                Console.WriteLine($"Starting {appName} - {appDescription}");
                Console.WriteLine($"Documentation: {documentationUrl}");
                Console.WriteLine("https://www.kaonomi.net");
                return;
            }

            Initialize(appName, _theme);

            if (CanAnimate() && Environment.GetEnvironmentVariable("KAONOMI_NO_INTRO") is null)
                Intro(_theme);

            Banner(appName, appDescription, documentationUrl, _theme);
        }

        /// <summary>Puts the terminal colors, cursor and opacity back as they were before the theme.</summary>
        public static void Restore()
        {
            lock (Sync)
            {
                if (_ansi)
                    Console.Write(Esc + "]110\u0007" + Esc + "]111\u0007" + Esc + "]104\u0007" + Esc + "[?25h");
                Console.ResetColor();
                SetWindowOpacity(1);
            }
        }

        private enum Level { Info, Success, Warning, Error, Trace }

        /// <summary>Status line, e.g. <c>[HH:mm:ss] &gt; message</c> (<c>HH:mm:ss [ INFO ] message</c> with ubuntu).</summary>
        public static void Info(string message) => Line(message, Level.Info);

        /// <summary>Success line, highlighted.</summary>
        public static void Success(string message) => Line(message, Level.Success);

        /// <summary>Warning line (yellow).</summary>
        public static void Warning(string message) => Line(message, Level.Warning);

        /// <summary>Error line (red).</summary>
        public static void Error(string message) => Line(message, Level.Error);

        /// <summary>Discreet line, for statistics and details.</summary>
        public static void Trace(string message) => Line(message, Level.Trace);

        /// <summary>Shell-like prompt; the text typed afterwards is highlighted.</summary>
        public static void Prompt(string path)
        {
            lock (Sync)
            {
                if (_theme == Ubuntu)
                {
                    // Default Ubuntu bash prompt: green user@host, blue path.
                    Write($"{Environment.UserName}@{Environment.MachineName}", ConsoleColor.Green);
                    Write(":", Ubuntu.Text);
                    Write("~/" + path.TrimStart('/'), ConsoleColor.Blue);
                    Write("$ ", Ubuntu.Text);
                    Console.ForegroundColor = Ubuntu.Text;
                }
                else if (_theme == Jarvis)
                {
                    Write("JARVIS", Jarvis.Accent);
                    Write(" ▸ ", Jarvis.Dim);
                    Write(Environment.UserName, Jarvis.Text);
                    Write(":", Jarvis.Dim);
                    Write(path, Jarvis.Text);
                    Write(" › ", Jarvis.Accent);
                    Console.ForegroundColor = Jarvis.Glow;
                }
                else if (_theme == Matrix)
                {
                    Write("KAONOMI@" + Environment.UserName, Matrix.Text);
                    Write(":", Matrix.Dim);
                    Write(path, Matrix.Text);
                    Write("$ ", Matrix.Dim);
                    Console.ForegroundColor = Matrix.Glow;
                }
                else
                {
                    // Classic prompt colors.
                    Write("KAONOMI@" + Environment.UserName, ConsoleColor.Green);
                    Write(":", ConsoleColor.White);
                    Write(path, ConsoleColor.Blue);
                    Write("$ ", ConsoleColor.White);
                    Console.ForegroundColor = ConsoleColor.Cyan;
                }
            }
        }

        /// <summary>
        /// Writes <paramref name="text"/> as if it were typed (a few milliseconds per character), then a new line, in
        /// <paramref name="color"/> or the theme text color. Without a theme, long texts or redirected output, the text is
        /// written at once.
        /// </summary>
        public static void TypeLine(string text, ConsoleColor? color = null, int delayMs = 4)
        {
            lock (Sync)
            {
                if (_theme is null)
                {
                    Console.WriteLine(text);
                    return;
                }

                Console.ForegroundColor = color ?? _theme.Text;
                if (!CanAnimate() || delayMs <= 0 || text.Length > 1500 || _skip)
                {
                    Console.WriteLine(text);
                    return;
                }

                foreach (var c in text)
                {
                    Console.Write(c);
                    if (!_skip && !char.IsWhiteSpace(c))
                    {
                        if (KeyPressed())
                            _skip = true;
                        else
                            Thread.Sleep(delayMs);
                    }
                }
                Console.WriteLine();
            }
        }

        private static void Line(string message, Level level)
        {
            lock (Sync)
            {
                var color = level switch
                {
                    Level.Warning => ConsoleColor.Yellow,
                    Level.Error => ConsoleColor.Red,
                    Level.Success => _theme?.Glow ?? ConsoleColor.Gray,
                    Level.Trace => _theme?.Dim ?? ConsoleColor.DarkGray,
                    _ => _theme?.Text ?? ConsoleColor.Gray,
                };

                if (_theme is null)
                {
                    // Classic look: the message alone, colored only for errors, warnings and details.
                    if (level is Level.Warning or Level.Error or Level.Trace)
                        Console.ForegroundColor = color;
                    Console.WriteLine(message);
                    Console.ResetColor();
                    return;
                }

                if (_theme == Ubuntu)
                {
                    // systemd style: HH:mm:ss [  OK  ] message.
                    var (tag, tagColor) = level switch
                    {
                        Level.Success => ("  OK  ", ConsoleColor.Green),
                        Level.Warning => (" WARN ", ConsoleColor.Yellow),
                        Level.Error => ("FAILED", ConsoleColor.Red),
                        Level.Trace => ("  ..  ", Ubuntu.Dim),
                        _ => (" INFO ", Ubuntu.Accent),
                    };
                    Write($"{DateTime.Now:HH:mm:ss} [", Ubuntu.Dim);
                    Write(tag, tagColor);
                    Write("] ", Ubuntu.Dim);
                    Write(message, level is Level.Info or Level.Success ? Ubuntu.Text : color);
                }
                else
                {
                    var marker = level switch
                    {
                        Level.Success => '✓',
                        Level.Warning => '!',
                        Level.Error => 'x',
                        Level.Trace => '·',
                        _ => _theme == Jarvis ? '▸' : '>',
                    };
                    Write($"[{DateTime.Now:HH:mm:ss}] ", _theme.Dim);
                    Write($"{marker} {message}", color);
                }

                Console.WriteLine();
                Console.ForegroundColor = _theme.Text;
            }
        }

        private static void Write(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
        }

        // ------------------------------------------------------------------ setup and banner

        /// <summary>Theme colors (including the terminal default ones, used by the framework loggers), title, UTF-8 and transparency.</summary>
        private static void Initialize(string appName, Theme theme)
        {
            Try(() => Console.OutputEncoding = Encoding.UTF8);
            Try(() => Console.Title = string.Format(theme.Title, appName, Environment.UserName, Environment.MachineName));

            if (!Console.IsOutputRedirected)
            {
                _ansi = EnableVirtualTerminal();
                if (_ansi)
                {
                    Console.Write($"{Esc}]10;{theme.DefaultForeground}\u0007{Esc}]11;{theme.DefaultBackground}\u0007");
                    foreach (var (index, color) in theme.Palette ?? [])
                        Console.Write($"{Esc}]4;{index};{color}\u0007");
                }

                SetWindowOpacity(1 - Transparency);
            }

            // With ANSI the terminal default background (set above) shows through; otherwise plain black.
            if (!_ansi)
                Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = theme.Text;
            Try(Console.Clear);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        }

        /// <summary>KAONOMI logo and the application information lines, in the theme colors.</summary>
        private static void Banner(string appName, string appDescription, string documentationUrl, Theme theme)
        {
            if (theme == Ubuntu)
            {
                UbuntuBanner(appName, appDescription, documentationUrl);
                return;
            }

            var jarvis = theme == Jarvis;
            var width = Logo[0].Length;

            lock (Sync)
            {
                Console.WriteLine();
                if (jarvis)
                    HudRule("J.A.R.V.I.S.  //  KAONOMI INTERFACE", width);

                ConsoleColor[] gradient = jarvis
                    ? [ConsoleColor.White, ConsoleColor.Cyan, ConsoleColor.Cyan, ConsoleColor.Cyan, ConsoleColor.DarkCyan, ConsoleColor.DarkCyan]
                    : [theme.Glow, theme.Text, theme.Text, theme.Text, theme.Dim, theme.Dim];
                for (var i = 0; i < Logo.Length; i++)
                    Write("  " + Logo[i] + Environment.NewLine, gradient[i]);

                if (!jarvis)
                    Write("  " + new string('═', width) + Environment.NewLine, theme.Dim);
                else
                    HudRule(null, width);
            }

            var marker = jarvis ? "▸" : ">";
            InfoLine(marker, "application  ", $"{appName} :: {appDescription}", theme);
            InfoLine(marker, "documentation", documentationUrl, theme);
            InfoLine(marker, "web          ", "https://www.kaonomi.net", theme);
            InfoLine(marker, jarvis ? "user         " : "operator     ", $"{Environment.UserName}@{Environment.MachineName}  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}", theme);

            lock (Sync)
            {
                if (jarvis)
                    HudRule("ALL SYSTEMS OPERATIONAL", width);
                else
                    Write("  " + new string('═', width) + Environment.NewLine, theme.Dim);
                Console.WriteLine();
                Console.ForegroundColor = theme.Text;
            }
        }

        private static void InfoLine(string marker, string label, string value, Theme theme)
        {
            lock (Sync)
            {
                Write($"  {marker} ", theme.Accent);
                Write(label + " : ", theme.Dim);
            }
            TypeLine(value, theme.Text);
        }

        /// <summary>Ubuntu "message of the day": logo, welcome line, links and system information.</summary>
        private static void UbuntuBanner(string appName, string appDescription, string documentationUrl)
        {
            lock (Sync)
            {
                Console.WriteLine();
                ConsoleColor[] gradient = [Ubuntu.Glow, Ubuntu.Glow, Ubuntu.Accent, Ubuntu.Accent, Ubuntu.Accent, ConsoleColor.DarkMagenta];
                for (var i = 0; i < Logo.Length; i++)
                    Write("  " + Logo[i] + Environment.NewLine, gradient[i]);
                Console.WriteLine();
            }

            var os = $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
            TypeLine($"Welcome to {appName} ({os})", Ubuntu.Glow);
            Console.WriteLine();
            MotdLine("Description:  ", appDescription);
            MotdLine("Documentation:", documentationUrl);
            MotdLine("Web:          ", "https://www.kaonomi.net");
            Console.WriteLine();
            TypeLine("  System information as of " + DateTime.Now.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture), Ubuntu.Dim);
            Console.WriteLine();
            MotdLine("User:         ", $"{Environment.UserName}@{Environment.MachineName}");
            MotdLine("Runtime:      ", $".NET {Environment.Version}");
            MotdLine("Processors:   ", Environment.ProcessorCount.ToString());
            Console.WriteLine();
            Console.ForegroundColor = Ubuntu.Text;
        }

        private static void MotdLine(string label, string value)
        {
            lock (Sync)
            {
                Write("  * ", Ubuntu.Accent);
                Write(label + " ", Ubuntu.Dim);
            }
            TypeLine(value, Ubuntu.Text);
        }

        /// <summary>HUD separator <c>◢■■[ TITLE ]━━━━◣</c> (gold title, blue line).</summary>
        private static void HudRule(string? title, int width)
        {
            if (title is null)
            {
                Write("  " + new string('━', width) + Environment.NewLine, Jarvis.Dim);
                return;
            }

            Write("  ◢■■", Jarvis.Accent);
            Write("[ ", Jarvis.Dim);
            Write(title, Jarvis.Accent);
            Write(" ]", Jarvis.Dim);
            var rest = Math.Max(0, width - title.Length - 9);
            Write(new string('━', rest), Jarvis.Dim);
            Write("◣" + Environment.NewLine, Jarvis.Accent);
        }

        // ------------------------------------------------------------------ intros

        private static void Intro(Theme theme)
        {
            try
            {
                if (theme == Matrix)
                    MatrixIntro();
                else if (theme == Ubuntu)
                    UbuntuIntro();
                else
                    JarvisIntro();
            }
            finally
            {
                if (_ansi)
                    Console.Write(Esc + "[0m" + Esc + "[?25h");
                Try(Console.Clear);
                _skip = false;
            }
        }

        /// <summary>Digital rain, then the "Wake up" message.</summary>
        private static void MatrixIntro()
        {
            if (_ansi)
                DigitalRain(TimeSpan.FromMilliseconds(1600));
            if (KeyPressed())
                return;

            Try(Console.Clear);
            Console.WriteLine();
            foreach (var (text, pause) in new[]
                     {
                         ($"  Wake up, {Environment.UserName}...", 500),
                         ("  The Matrix has you...", 400),
                         ("  Follow the white rabbit.", 300),
                     })
            {
                TypeLine(text, Matrix.Text, 30);
                if (_skip || KeyPressed())
                    return;
                Thread.Sleep(pause);
            }
        }

        /// <summary>Arc reactor power-up, system diagnostics with progress bars, then the J.A.R.V.I.S. greeting.</summary>
        private static void JarvisIntro()
        {
            if (_ansi)
                ArcReactor(TimeSpan.FromMilliseconds(1800));
            if (KeyPressed())
                return;

            Try(Console.Clear);
            Console.WriteLine();
            TypeLine("  J.A.R.V.I.S.  —  Just A Rather Very Intelligent System", Jarvis.Accent, 12);
            TypeLine("  Initializing HUD...", Jarvis.Dim, 12);
            Console.WriteLine();

            foreach (var system in new[] { "Arc reactor", "Repulsor calibration", "Flight stabilizers", "Targeting system", "Neural interface", "HUD overlay" })
            {
                lock (Sync)
                {
                    Write("  ▸ ", Jarvis.Accent);
                    Write(system.PadRight(22, '.') + " ", Jarvis.Text);
                    Write("[", Jarvis.Dim);
                    for (var i = 0; i < 12; i++)
                    {
                        Write("█", i < 8 ? Jarvis.Text : ConsoleColor.White);
                        if (!_skip && KeyPressed())
                            _skip = true;
                        if (!_skip)
                            Thread.Sleep(12);
                    }
                    Write("] ", Jarvis.Dim);
                    Write("100%  ", Jarvis.Text);
                    Write("ONLINE" + Environment.NewLine, Jarvis.Accent);
                }
            }

            Console.WriteLine();
            var hour = DateTime.Now.Hour;
            var moment = hour < 12 ? "morning" : hour < 18 ? "afternoon" : "evening";
            TypeLine($"  Good {moment}, {Environment.UserName}. All systems are operational.", ConsoleColor.White, 25);
            if (!_skip && !KeyPressed())
                Thread.Sleep(700);
        }

        /// <summary>Boot splash with the loading dots, systemd boot messages, then the login line.</summary>
        private static void UbuntuIntro()
        {
            if (_ansi)
                UbuntuSplash(TimeSpan.FromMilliseconds(1800));
            if (KeyPressed())
                return;

            Try(Console.Clear);
            foreach (var unit in new[]
                     {
                         "Reached target Local File Systems.",
                         "Started Journal Service.",
                         "Started Network Manager.",
                         "Reached target Network is Online.",
                         $"Started .NET runtime {Environment.Version}.",
                         $"Started {AppDomain.CurrentDomain.FriendlyName}.",
                         "Reached target Multi-User System.",
                     })
            {
                lock (Sync)
                {
                    Write("[", Ubuntu.Text);
                    Write("  OK  ", ConsoleColor.Green);
                    Write("] " + unit + Environment.NewLine, Ubuntu.Text);
                }
                if (!_skip && !KeyPressed())
                    Thread.Sleep(90);
            }

            Console.WriteLine();
            lock (Sync)
                Write($"{Environment.MachineName.ToLowerInvariant()} login: ", Ubuntu.Text);
            TypeLine(Environment.UserName, Ubuntu.Glow, 60);
            if (!_skip && !KeyPressed())
                Thread.Sleep(500);
        }

        /// <summary>
        /// Plymouth-like splash: the "ubuntu" word in the middle of the window and five dots lighting up one after
        /// the other below it (needs ANSI escape sequences).
        /// </summary>
        private static void UbuntuSplash(TimeSpan duration)
        {
            string[] word =
            [
                "       _                 _         ",
                " _   _| |__  _   _ _ __ | |_ _   _ ",
                "| | | | '_ \\| | | | '_ \\| __| | | |",
                "| |_| | |_) | |_| | | | | |_| |_| |",
                " \\__,_|_.__/ \\__,_|_| |_|\\__|\\__,_|",
            ];

            var width = Math.Max(10, Console.WindowWidth - 1);
            var height = Math.Max(word.Length + 4, Console.WindowHeight - 1);
            var top = Math.Max(1, height / 2 - word.Length);
            var left = Math.Max(1, (width - word[0].Length) / 2 + 1);
            var dot = UnicodeTerminal() ? '●' : 'o';

            Console.Write(Esc + "[?25l" + Esc + "[2J");
            var frame = new StringBuilder();
            for (var i = 0; i < word.Length; i++)
                frame.Append(Esc).Append('[').Append(top + i).Append(';').Append(left).Append('H').Append(Esc).Append("[97m").Append(word[i]);
            Console.Write(frame.ToString());

            var dotsRow = top + word.Length + 2;
            var dotsLeft = Math.Max(1, (width - 9) / 2 + 1);
            var end = DateTime.UtcNow + duration;
            var tick = 0;

            while (DateTime.UtcNow < end && !KeyPressed())
            {
                frame.Clear();
                frame.Append(Esc).Append('[').Append(dotsRow).Append(';').Append(dotsLeft).Append('H');
                for (var i = 0; i < 5; i++)
                {
                    // Lit dot in white, the others in Ubuntu orange (palette index 3).
                    frame.Append(Esc).Append(i == tick % 5 ? "[97m" : "[33m").Append(dot).Append(' ');
                }
                Console.Write(frame.ToString());
                tick++;
                Thread.Sleep(180);
            }

            Console.Write(Esc + "[0m");
        }

        /// <summary>
        /// Arc reactor in the middle of the window: the rings light up one after the other, the outer ring turns and the
        /// core pulses (needs ANSI escape sequences).
        /// </summary>
        private static void ArcReactor(TimeSpan duration)
        {
            var width = Math.Max(10, Console.WindowWidth - 1);
            var height = Math.Max(6, Console.WindowHeight - 1);
            var radius = Math.Max(3, Math.Min(9, Math.Min((height - 3) / 2, (width - 4) / 4)));
            var cx = width / 2;
            var cy = height / 2;
            var unicode = UnicodeTerminal();
            var core = unicode ? '◉' : '@';
            var dot = unicode ? '•' : 'o';

            // Rings from the core outwards (terminal cells are about twice as high as wide).
            var rings = new List<(int X, int Y)[]>();
            for (var r = 2; r <= radius; r += 2)
            {
                var steps = Math.Max(12, (int)(Math.PI * 2 * r * 2));
                rings.Add(Enumerable.Range(0, steps)
                    .Select(i => i * Math.PI * 2 / steps)
                    .Select(a => (X: cx + (int)Math.Round(Math.Cos(a) * r * 2), Y: cy + (int)Math.Round(Math.Sin(a) * r)))
                    .Distinct()
                    .ToArray());
            }

            Console.Write(Esc + "[?25l" + Esc + "[2J");
            var frame = new StringBuilder();
            var end = DateTime.UtcNow + duration;
            var tick = 0;

            while (DateTime.UtcNow < end && !KeyPressed())
            {
                frame.Clear();
                Put(frame, cx, cy, height, tick % 4 < 2 ? "97" : "96", core);

                var lit = Math.Min(rings.Count, 1 + tick / 3);
                for (var r = 0; r < lit; r++)
                {
                    var ring = rings[r];
                    var outer = r == rings.Count - 1;
                    for (var i = 0; i < ring.Length; i++)
                    {
                        if (outer)
                        {
                            // Turning segments on the outer ring.
                            var on = (i + tick) % 8 < 5;
                            Put(frame, ring[i].X, ring[i].Y, height, on ? "96" : "36", on ? '█' : '░');
                        }
                        else
                        {
                            var fresh = r == lit - 1 && tick / 3 < rings.Count;
                            Put(frame, ring[i].X, ring[i].Y, height, fresh ? "97" : (r % 2 == 0 ? "96" : "36"), dot);
                        }
                    }
                }

                if (tick > rings.Count * 3)
                {
                    const string label = "ARC REACTOR ONLINE";
                    var x = Math.Max(1, cx - label.Length / 2);
                    frame.Append(Esc).Append('[').Append(Math.Min(height, cy + radius + 2)).Append(';').Append(x).Append('H')
                         .Append(Esc).Append(tick % 4 < 2 ? "[93m" : "[33m").Append(label);
                }

                Console.Write(frame.ToString());
                tick++;
                Thread.Sleep(60);
            }

            Console.Write(Esc + "[0m");
        }

        /// <summary>Falling green characters on the whole window (needs ANSI escape sequences).</summary>
        private static void DigitalRain(TimeSpan duration)
        {
            var width = Math.Max(1, Console.WindowWidth - 1);
            var height = Math.Max(1, Console.WindowHeight - 1);
            var glyphs = RainGlyphs();
            var random = new Random();

            // One stream every other column: head position and trail length.
            var columns = (width + 1) / 2;
            var heads = new int[columns];
            var trails = new int[columns];
            for (var i = 0; i < columns; i++)
            {
                heads[i] = -random.Next(0, height);
                trails[i] = random.Next(6, Math.Max(7, height * 2 / 3));
            }

            Console.Write(Esc + "[?25l" + Esc + "[2J");
            var frame = new StringBuilder();
            var end = DateTime.UtcNow + duration;

            while (DateTime.UtcNow < end && !KeyPressed())
            {
                frame.Clear();
                for (var i = 0; i < columns; i++)
                {
                    var x = i * 2 + 1;
                    var y = heads[i];

                    Put(frame, x, y, height, "97", glyphs[random.Next(glyphs.Length)]);       // head: white
                    Put(frame, x, y - 1, height, "92", glyphs[random.Next(glyphs.Length)]);   // bright green
                    Put(frame, x, y - 5, height, "32", glyphs[random.Next(glyphs.Length)]);   // fading green
                    Put(frame, x, y - trails[i], height, "32", ' ');                           // end of the trail

                    heads[i]++;
                    if (y - trails[i] > height)
                    {
                        heads[i] = -random.Next(0, height / 2 + 1);
                        trails[i] = random.Next(6, Math.Max(7, height * 2 / 3));
                    }
                }

                Console.Write(frame.ToString());
                Thread.Sleep(35);
            }

            Console.Write(Esc + "[0m");
        }

        private static void Put(StringBuilder frame, int x, int y, int height, string color, char c)
        {
            if (y < 0 || y >= height || x < 1)
                return;
            frame.Append(Esc).Append('[').Append(y + 1).Append(';').Append(x).Append('H')
                 .Append(Esc).Append('[').Append(color).Append('m').Append(c);
        }

        /// <summary>Half-width katakana where the terminal can draw them, ASCII otherwise.</summary>
        private static char[] RainGlyphs()
        {
            var ascii = "0123456789ABCDEFZ:.=*+-<>¦|";
            if (!UnicodeTerminal())
                return ascii.ToCharArray();

            var glyphs = new StringBuilder(ascii);
            for (var c = 'ｦ'; c <= 'ﾝ'; c++)
                glyphs.Append(c);
            return glyphs.ToString().ToCharArray();
        }

        /// <summary>
        /// Windows Terminal, Linux and macOS terminals draw the less common characters; the classic Windows console may not.
        /// Windows Terminal is recognized by WT_SESSION, or by the window that owns the hidden console window (when Windows
        /// starts the console in Windows Terminal by itself, WT_SESSION is not set).
        /// </summary>
        private static bool UnicodeTerminal()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WT_SESSION") is not null)
                return true;

            try
            {
                var console = GetConsoleWindow();
                return console != IntPtr.Zero && VisibleConsoleWindow() != console;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static bool CanAnimate() => !Console.IsOutputRedirected;

        /// <summary>True (and the key is consumed) when the user pressed a key to skip the animation.</summary>
        private static bool KeyPressed()
        {
            try
            {
                if (Console.IsInputRedirected || !Console.KeyAvailable)
                    return false;
                while (Console.KeyAvailable)
                    Console.ReadKey(intercept: true);
                _skip = true;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
            {
                // Not a real console (redirected output, service...): keep the default look.
            }
        }

        // ------------------------------------------------------------------ window (Windows)

        /// <summary>
        /// When the PC has several screens, moves the console window to the smallest one (by surface) and gives it one cell
        /// of a grid of <paramref name="slotCount"/> cells, so that several background hosts share that screen without
        /// overlapping. Nothing is done with a single screen, outside Windows, or without a console window.
        /// </summary>
        /// <param name="slot">Cell of this application (0 = top left, then left to right and top to bottom).</param>
        /// <param name="slotCount">Number of cells of the grid.</param>
        public static void PlaceOnSmallestScreen(int slot, int slotCount = 4)
        {
            if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
                return;

            try
            {
                // Real pixels on every monitor, whatever its scaling.
                SetThreadDpiAwarenessContext(new IntPtr(-4));

                var window = VisibleConsoleWindow();
                if (window == IntPtr.Zero)
                    return;

                var workAreas = new List<Rect>();
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(monitor, ref info))
                        workAreas.Add(info.Work);
                    return true;
                }, IntPtr.Zero);

                if (workAreas.Count < 2)
                    return;

                var area = workAreas.OrderBy(r => (long)r.Width * r.Height).First();

                var count = Math.Max(1, slotCount);
                var columns = (int)Math.Ceiling(Math.Sqrt(count));
                var rows = (int)Math.Ceiling(count / (double)columns);
                var cell = Math.Clamp(slot, 0, count - 1);
                var width = area.Width / columns;
                var height = area.Height / rows;

                const int SwRestore = 9;
                ShowWindow(window, SwRestore);
                MoveWindow(window, area.Left + cell % columns * width, area.Top + cell / columns * height, width, height, true);

                // Let the terminal resize its buffer before anything is drawn (intro, banner).
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Window placement is cosmetic: keep the default position.
            }
        }

        /// <summary>Turns on ANSI escape sequences (always available outside Windows).</summary>
        private static bool EnableVirtualTerminal()
        {
            if (!OperatingSystem.IsWindows())
                return true;

            try
            {
                const int StdOutputHandle = -11;
                const uint EnableVirtualTerminalProcessing = 0x0004;

                var handle = GetStdHandle(StdOutputHandle);
                return GetConsoleMode(handle, out var mode)
                    && ((mode & EnableVirtualTerminalProcessing) != 0
                        || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Window opacity (Windows only). The whole window becomes translucent: the classic console window itself, or the
        /// Windows Terminal window that owns the hidden console window (Windows Terminal ignores the hidden one).
        /// </summary>
        private static void SetWindowOpacity(double opacity)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                const int GwlExStyle = -20;
                const int WsExLayered = 0x80000;
                const uint LwaAlpha = 0x2;

                var window = VisibleConsoleWindow();
                if (window == IntPtr.Zero)
                    return;

                SetWindowLong(window, GwlExStyle, GetWindowLong(window, GwlExStyle) | WsExLayered);
                SetLayeredWindowAttributes(window, 0, (byte)Math.Round(Math.Clamp(opacity, 0.1, 1) * 255), LwaAlpha);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // No console window: nothing to do.
            }
        }

        /// <summary>
        /// The window the user sees: the classic console window itself, or the Windows Terminal window that owns the
        /// hidden console window.
        /// </summary>
        private static IntPtr VisibleConsoleWindow()
        {
            const uint GwOwner = 4;

            var window = GetConsoleWindow();
            if (window != IntPtr.Zero && GetWindow(window, GwOwner) is var owner && owner != IntPtr.Zero)
                return owner;
            return window;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;

            public readonly int Width => Right - Left;

            public readonly int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
