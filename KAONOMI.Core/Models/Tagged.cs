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
    /// A document with tags: each one starts with <c>#</c>, <c>!</c>, <c>?</c> or <c>&amp;</c>, in upper case, without a
    /// space (<see cref="TagRules.Normalize"/> makes them so when the document is saved).
    /// </summary>
    public interface ITagged
    {
        List<string> Tags { get; set; }
    }

    /// <summary>The rules of the tags of <see cref="ITagged"/>.</summary>
    public static class TagRules
    {
        /// <summary>The characters a tag may start with: a topic (#), an alert (!), a question (?), a link (&amp;).</summary>
        public const string Prefixes = "#!?&";

        /// <summary>
        /// The tags as they are kept: trimmed, their spaces turned into <c>_</c>, in upper case, each starting with one of
        /// <see cref="Prefixes"/> (<c>#</c> added when none), the empty ones and the doubles left out.
        /// </summary>
        public static List<string> Normalize(IEnumerable<string>? tags)
        {
            var result = new List<string>();
            if (tags == null)
                return result;
            foreach (var raw in tags)
            {
                var tag = Normalize(raw);
                if (tag != null && !result.Contains(tag))
                    result.Add(tag);
            }
            return result;
        }

        /// <summary>One tag as it is kept, or null when nothing is left of it.</summary>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var text = raw.Trim();
            var prefix = Prefixes.IndexOf(text[0]) >= 0 ? text[0] : '#';
            if (Prefixes.IndexOf(text[0]) >= 0)
                text = text.Substring(1);
            var body = new StringBuilder();
            var lastWasSpace = false;
            foreach (var c in text.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && body.Length > 0)
                        body.Append('_');
                    lastWasSpace = true;
                    continue;
                }
                lastWasSpace = false;
                if (Prefixes.IndexOf(c) >= 0 && body.Length == 0)
                    continue; // a doubled prefix
                body.Append(char.ToUpperInvariant(c));
            }
            var name = body.ToString().TrimEnd('_');
            return name.Length == 0 ? null : prefix + name;
        }

        /// <summary>True when <paramref name="tag"/> is already as it is kept.</summary>
        public static bool IsValid(string? tag) => tag != null && Normalize(tag) == tag;
    }
}
