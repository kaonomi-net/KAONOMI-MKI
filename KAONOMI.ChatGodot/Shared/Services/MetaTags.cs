using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The tags (<c>#TOPIC</c>), events (<c>@EVENT</c>) and signals (<c>!SIGNAL</c>) of the documents, as the server keeps
/// them (<c>TagRules</c> of KAONOMI.Core): a prefix, then the words in upper case joined by <c>-</c>, no space. In the
/// screens they are typed in one line each, separated by spaces, commas, semicolons or new lines (a tag of several words
/// is written with <c>-</c>: <c>#SALON-IA</c>); the prefix may be left out, it is added.
/// </summary>
public static class MetaTags
{
    public const char Tag = '#';
    public const char Event = '@';
    public const char Signal = '!';

    /// <summary>The placeholders of the fields.</summary>
    public const string TagsHint = "#TOPIC #OTHER-TOPIC";
    public const string EventsHint = "@EVENT @OTHER-EVENT";
    public const string SignalsHint = "!SIGNAL !OTHER-SIGNAL";

    private static readonly char[] Separators = [' ', ',', ';', '\n', '\r', '\t'];

    /// <summary>The items typed in <paramref name="text"/>, as they are kept with <paramref name="prefix"/>.</summary>
    public static List<string> Parse(string? text, char prefix) =>
        Normalize((text ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries), prefix);

    /// <summary>The items as they are kept with <paramref name="prefix"/>, the empty ones and the doubles left out.</summary>
    public static List<string> Normalize(IEnumerable<string?>? items, char prefix)
    {
        var result = new List<string>();
        foreach (var raw in items ?? [])
            if (Normalize(raw, prefix) is { } item && !result.Contains(item))
                result.Add(item);
        return result;
    }

    /// <summary>The items in one line, separated by spaces, for a field.</summary>
    public static string Format(IEnumerable<string>? items) => string.Join(' ', items ?? []);

    /// <summary>One item as it is kept with <paramref name="prefix"/>, or null when nothing is left of it.</summary>
    public static string? Normalize(string? raw, char prefix)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim().TrimStart(Tag, Event, Signal, '?', '&');
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
}
