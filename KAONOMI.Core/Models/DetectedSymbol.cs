using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A symbolic pattern found in a dialog, kept in the search engine (index <c>detectedsymbol</c>), one document per
    /// pattern and dialog: the symbol, the pattern it comes from, every place it matched (the text, where, which regex)
    /// and how many times, and the dialog and session it was found in. Its title, tags, events and signals are those of
    /// the pattern (<c>!SAD</c>, <c>!HAPPY</c>, <c>!FEAR</c>…), and its event the one the pattern triggers. The id is made
    /// from the dialog and the pattern: the same dialog analysed again writes the same document over.
    /// </summary>
    public class DetectedSymbol : EntityBase, ISoftDeletable
    {
        /// <summary>Builds an empty detection (for the deserialization).</summary>
        public DetectedSymbol()
        {
        }

        /// <summary>Builds the detection of <paramref title="pattern"/> in the dialog <paramref title="dialogId"/>.</summary>
        public DetectedSymbol(SymbolicPattern pattern, string dialogId, string? sessionId, List<SymbolMatch> matches)
            : base(MakeId(dialogId, pattern.id))
        {
            patternId = pattern.id;
            symbol = pattern.Symbol ?? string.Empty;
            language = pattern.Language;
            @event = pattern.Event;
            title = string.IsNullOrWhiteSpace(pattern.title) ? symbol : pattern.title;
            Tags = new List<string>(pattern.Tags ?? new List<string>());
            Events = new List<string>(pattern.Events ?? new List<string>());
            Signals = new List<string>(pattern.Signals ?? new List<string>());
            this.dialogId = dialogId;
            this.sessionId = sessionId;
            this.matches = matches;
            count = matches.Count;
            date = DateTime.UtcNow;
            machineName = Environment.MachineName;
            userName = Environment.UserName;
        }

        /// <summary>The symbol of the pattern (its first word: mort, naissance, guerre…).</summary>
        public string symbol { get; set; } = string.Empty;

        /// <summary>The id of the symbolic pattern that matched.</summary>
        public string patternId { get; set; } = string.Empty;

        /// <summary>The language of the pattern (FR…).</summary>
        public string? language { get; set; }

        /// <summary>The event the pattern triggers, or null.</summary>
        public string? @event { get; set; }

        /// <summary>The dialog the symbol was found in.</summary>
        public string dialogId { get; set; } = string.Empty;

        /// <summary>The session of that dialog.</summary>
        public string? sessionId { get; set; }

        /// <summary>Every place the pattern matched in the prompt.</summary>
        public List<SymbolMatch> matches { get; set; } = new List<SymbolMatch>();

        /// <summary>How many times it matched.</summary>
        public int count { get; set; }

        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; }

        /// <summary>The id of a detection: the same dialog and pattern give the same id.</summary>
        private static string MakeId(string dialogId, string patternId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{dialogId}|{patternId}"));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }
    }

    /// <summary>One place a symbolic pattern matched: the text, where (characters, end excluded), and which regex.</summary>
    public class SymbolMatch
    {
        public string text { get; set; } = string.Empty;

        public int start { get; set; }

        public int end { get; set; }

        /// <summary>The regex of the pattern that matched.</summary>
        public string regex { get; set; } = string.Empty;
    }
}
