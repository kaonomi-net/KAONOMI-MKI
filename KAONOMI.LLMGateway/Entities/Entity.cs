namespace KAONOMI.LLMGateway.Entities;

/// <summary>
/// An entity found in a prompt: its type (CODE, MATH, URL, DATE, MONEY, PERSON… or a type of a gazetteer), its value
/// (the language of a code, the canonical title of a gazetteer, the text otherwise), the text as written and where.
/// </summary>
public sealed record Entity(string Type, string Value, string Text, int Start, int Length)
{
    /// <summary>TYPE:value, as the rules and the log show it.</summary>
    public override string ToString() => $"{Type}:{Value}";
}

/// <summary>The types the extractor finds by itself (the gazetteers add theirs).</summary>
public static class EntityTypes
{
    public const string Code = "CODE";
    public const string ProgrammingLanguage = "PROGRAMMING_LANGUAGE";
    public const string Math = "MATH";
    public const string Url = "URL";
    public const string Email = "EMAIL";
    public const string Ip = "IP";
    public const string Date = "DATE";
    public const string Time = "TIME";
    public const string Money = "MONEY";
    public const string Percent = "PERCENT";
    public const string Phone = "PHONE";
    public const string Number = "NUMBER";
    public const string File = "FILE";
    public const string Hashtag = "HASHTAG";
    public const string Mention = "MENTION";
    public const string Person = "PERSON";
    public const string ProperNoun = "PROPER_NOUN";
    public const string Language = "LANGUAGE";
    public const string Image = "IMAGE";

    public static readonly IReadOnlyList<string> Builtin =
    [
        Code, ProgrammingLanguage, Math, Url, Email, Ip, Date, Time, Money, Percent, Phone, Number, File, Hashtag, Mention,
        Person, ProperNoun, Language, Image
    ];

    /// <summary>
    /// The types whose values must be the same in two prompts for a cached answer to be given back (the entity guard):
    /// "the capital of France" is not "the capital of Spain", 2 + 2 is not 2 + 3.
    /// </summary>
    public static bool Guarded(string type) => type is not (Language or Image or ProgrammingLanguage);
}
