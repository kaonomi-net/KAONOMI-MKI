namespace KAONOMI.Chat.Services;

/// <summary>Language of the spoken answers (<c>language</c> in config.yml): FR, EN, DE or NL; English otherwise.</summary>
public static class SpeechLanguage
{
    /// <summary>English name of the language, used to ask the model for a summary in it.</summary>
    public static string Name(string language) => language.Trim().ToUpperInvariant() switch
    {
        "FR" => "French",
        "DE" => "German",
        "NL" => "Dutch",
        "JA" => "Japanese",
        _ => "English"
    };

    /// <summary>What is said instead of reading the code of an answer.</summary>
    public static string CodeAttached(string language) => language.Trim().ToUpperInvariant() switch
    {
        "FR" => "Vous trouverez le code ci-joint.",
        "DE" => "Den Code finden Sie im Anhang.",
        "NL" => "U vindt de code in de bijlage.",
        _ => "You will find the code attached."
    };

    /// <summary>What the avatar says when the reminder of an event of /calendar rings (<paramref name="time"/>: null for a whole day).</summary>
    public static string Reminder(string language, string title, string? time) => language.Trim().ToUpperInvariant() switch
    {
        "FR" => time is null ? $"Rappel : {title}, aujourd'hui." : $"Rappel : {title} à {time}.",
        "DE" => time is null ? $"Erinnerung: {title}, heute." : $"Erinnerung: {title} um {time}.",
        "NL" => time is null ? $"Herinnering: {title}, vandaag." : $"Herinnering: {title} om {time}.",
        "JA" => time is null ? $"リマインダー：今日、{title}。" : $"リマインダー：{time}、{title}。",
        _ => time is null ? $"Reminder: {title}, today." : $"Reminder: {title} at {time}."
    };

    /// <summary>What the avatar says when an alarm of /alarm rings.</summary>
    public static string WakeUp(string language) => language.Trim().ToUpperInvariant() switch
    {
        "FR" => "Bouge ton cul, gros !",
        "DE" => "Beweg deinen Hintern, Alter!",
        "NL" => "Kom van je luie kont, makker!",
        "JA" => "ケツを上げろ、相棒！",
        _ => "Move your butt, buddy!"
    };
}
