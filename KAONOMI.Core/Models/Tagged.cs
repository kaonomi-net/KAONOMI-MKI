using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A document that is never erased: a delete (the REST <c>DELETE</c> of its kind, or <c>Delete</c> of the
    /// repository) marks it <c>isDeleted</c> instead, and it stays in its index.
    /// </summary>
    public interface ISoftDeletable
    {
        bool isDeleted { get; set; }
    }

    /// <summary>
    /// A document with a title, tags (<c>#TOPIC</c>), events (<c>@EVENT</c>) and signals (<c>!SIGNAL</c>), in upper case,
    /// <c>-</c> for the spaces (<see cref="TagRules.Apply"/> makes them so when the document is saved or read). Every
    /// <see cref="EntityBase"/> is one.
    /// </summary>
    public interface ITagged
    {
        string title { get; set; }
        List<string> Tags { get; set; }
        List<string> Events { get; set; }
        List<string> Signals { get; set; }
    }

    /// <summary>
    /// The rules of the tags, events and signals of <see cref="ITagged"/>: a prefix (<c>#</c> a tag, <c>@</c> an event,
    /// <c>!</c> a signal), then the words in upper case joined by <c>-</c> (no space; the <c>_</c> of the old tags become
    /// <c>-</c>), the empty ones and the doubles left out.
    /// </summary>
    public static class TagRules
    {
        public const char TagPrefix = '#';
        public const char EventPrefix = '@';
        public const char SignalPrefix = '!';

        /// <summary>The prefixes of the old tags that are no longer kept in the tags: a question and a link become tags.</summary>
        private const string OldPrefixes = "?&";

        /// <summary>The tags as they are kept: <c>#TOPIC</c>.</summary>
        public static List<string> NormalizeTags(IEnumerable<string>? tags) => NormalizeAll(tags, TagPrefix);

        /// <summary>The events as they are kept: <c>@EVENT</c>.</summary>
        public static List<string> NormalizeEvents(IEnumerable<string>? events) => NormalizeAll(events, EventPrefix);

        /// <summary>The signals as they are kept: <c>!SIGNAL</c>.</summary>
        public static List<string> NormalizeSignals(IEnumerable<string>? signals) => NormalizeAll(signals, SignalPrefix);

        /// <summary>
        /// Puts the metadata of <paramref title="document"/> as they are kept, moving the old tags: a <c>!ALERT</c> of the
        /// tags becomes a signal, a <c>?QUESTION</c> or a <c>&amp;LINK</c> a tag <c>#QUESTION</c> / <c>#LINK</c>, an
        /// <c>@EVENT</c> an event; the <c>_</c> become <c>-</c>; the title is trimmed.
        /// </summary>
        public static void Apply(ITagged document)
        {
            var tags = document.Tags ?? new List<string>();
            var signals = (document.Signals ?? new List<string>()).Concat(tags.Where(t => t?.TrimStart().StartsWith(SignalPrefix) == true));
            var events = (document.Events ?? new List<string>()).Concat(tags.Where(t => t?.TrimStart().StartsWith(EventPrefix) == true));
            document.Tags = NormalizeTags(tags.Where(t => t?.TrimStart() is { Length: > 0 } s && s[0] != SignalPrefix && s[0] != EventPrefix));
            document.Signals = NormalizeSignals(signals);
            document.Events = NormalizeEvents(events);
            document.title = document.title?.Trim() ?? string.Empty;
        }

        /// <summary>One item as it is kept with <paramref title="prefix"/>, or null when nothing is left of it.</summary>
        public static string? Normalize(string? raw, char prefix)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var text = raw.Trim();
            // Any prefix it was written with (its own, or one of the other kinds, or an old one) is replaced by its own.
            while (text.Length > 0 && (text[0] is TagPrefix or EventPrefix or SignalPrefix || OldPrefixes.IndexOf(text[0]) >= 0))
                text = text.Substring(1);
            var body = new StringBuilder();
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c) || c is '_' or '-')
                {
                    if (body.Length > 0 && body[^1] != '-')
                        body.Append('-');
                    continue;
                }
                body.Append(char.ToUpperInvariant(c));
            }
            var name = body.ToString().TrimEnd('-');
            return name.Length == 0 ? null : prefix + name;
        }

        /// <summary>True when <paramref title="item"/> is already as it is kept with <paramref title="prefix"/>.</summary>
        public static bool IsValid(string? item, char prefix) => item != null && Normalize(item, prefix) == item;

        private static List<string> NormalizeAll(IEnumerable<string>? items, char prefix)
        {
            var result = new List<string>();
            if (items == null)
                return result;
            foreach (var raw in items)
            {
                var item = Normalize(raw, prefix);
                if (item != null && !result.Contains(item))
                    result.Add(item);
            }
            return result;
        }
    }
}
