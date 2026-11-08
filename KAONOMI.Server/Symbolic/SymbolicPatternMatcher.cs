using System.Diagnostics;
using System.Text.RegularExpressions;
using KAONOMI.Core;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Symbolic
{
    /// <summary>What the symbolic patterns found in a text: one entry per pattern that matched, and what did not work.</summary>
    public sealed class SymbolicMatchResult
    {
        public List<(SymbolicPattern Pattern, List<SymbolMatch> Matches)> detections { get; } = [];

        public List<string> warnings { get; } = [];

        public int patterns { get; set; }

        public double durationMs { get; set; }
    }

    /// <summary>
    /// The symbolic patterns of the search engine (PUT /symbolicpattern), their regexes compiled once and read again
    /// every five minutes (a new pattern is used without restarting the server). A regex that does not compile is left
    /// out with a warning; each match is limited in time, so that a bad regex cannot block the dialog sequence.
    /// </summary>
    public sealed class SymbolicPatternMatcher
    {
        private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
        private static readonly object Gate = new();
        private static SymbolicPatternMatcher? _shared;

        private readonly Config _config;
        private List<(SymbolicPattern Pattern, List<(string Source, Regex Regex)> Regexes)> _compiled = [];
        private List<string> _loadWarnings = [];
        private DateTime _read = DateTime.MinValue;

        private SymbolicPatternMatcher(Config config) => _config = config;

        /// <summary>The matcher of the server, made at the first call.</summary>
        public static SymbolicPatternMatcher Get(Config config)
        {
            lock (Gate)
                return _shared ??= new SymbolicPatternMatcher(config);
        }

        /// <summary>The patterns that match <paramref title="text"/>, each with every place it matched.</summary>
        public SymbolicMatchResult Match(string? text)
        {
            var watch = Stopwatch.StartNew();
            var result = new SymbolicMatchResult();
            var (compiled, warnings) = Patterns();
            result.patterns = compiled.Count;
            result.warnings.AddRange(warnings);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (var (pattern, regexes) in compiled)
            {
                var matches = new List<SymbolMatch>();
                foreach (var (source, regex) in regexes)
                {
                    try
                    {
                        foreach (Match m in regex.Matches(text))
                            if (m.Length > 0 && !matches.Any(x => x.start < m.Index + m.Length && m.Index < x.end))
                                matches.Add(new SymbolMatch { text = m.Value, start = m.Index, end = m.Index + m.Length, regex = source });
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        result.warnings.Add($"The regex of the pattern '{pattern.Symbol}' ({pattern.id}) took too long and was skipped: {source}");
                    }
                }
                if (matches.Count > 0)
                    result.detections.Add((pattern, matches.OrderBy(x => x.start).ToList()));
            }
            result.durationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
            return result;
        }

        /// <summary>The compiled patterns, read again from the search engine when older than five minutes (kept as they were when it does not answer).</summary>
        private (List<(SymbolicPattern, List<(string, Regex)>)> Compiled, List<string> Warnings) Patterns()
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _read > Refresh)
                {
                    _read = DateTime.UtcNow;
                    try
                    {
                        var warnings = new List<string>();
                        var compiled = new List<(SymbolicPattern, List<(string, Regex)>)>();
                        foreach (var pattern in new SessionManager(_config).SymbolicPatternsAll())
                        {
                            var regexes = new List<(string, Regex)>();
                            foreach (var source in pattern.RegexPatterns ?? [])
                            {
                                if (string.IsNullOrWhiteSpace(source))
                                    continue;
                                try
                                {
                                    regexes.Add((source, new Regex(source, RegexOptions.CultureInvariant, MatchTimeout)));
                                }
                                catch (ArgumentException ex)
                                {
                                    warnings.Add($"The regex of the pattern '{pattern.Symbol}' ({pattern.id}) is invalid and left out: {ex.Message}");
                                }
                            }
                            if (regexes.Count > 0)
                                compiled.Add((pattern, regexes));
                        }
                        _compiled = compiled;
                        _loadWarnings = warnings;
                    }
                    catch (Exception ex)
                    {
                        _loadWarnings = [$"The symbolic patterns could not be read: {ex.Message}"];
                    }
                }
                return (_compiled, _loadWarnings);
            }
        }
    }
}
