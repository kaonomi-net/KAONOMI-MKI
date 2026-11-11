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
}
