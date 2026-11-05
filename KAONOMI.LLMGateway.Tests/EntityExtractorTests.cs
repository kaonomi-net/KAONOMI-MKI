using KAONOMI.LLMGateway.Entities;

namespace KAONOMI.LLMGateway.Tests;

public class EntityExtractorTests
{
    private static readonly EntityExtractor Extractor = new(new Dictionary<string, List<string>>
    {
        ["ORG"] = ["KAONOMI", "Anthropic"],
        ["PRODUCT"] = ["Godot"]
    });

    private static List<string> Types(string text) => Extractor.Extract(text).Select(e => e.ToString()).ToList();

    [Fact]
    public void A_fenced_block_is_code_of_its_language_and_hides_what_is_inside()
    {
        var entities = Extractor.Extract("Pourquoi ça plante ?\n```python\nx = 2024-01-01\nprint(x)\n```");
        Assert.Contains(entities, e => e.Type == EntityTypes.Code && e.Value == "python");
        Assert.DoesNotContain(entities, e => e.Type == EntityTypes.Date);
    }

    [Theory]
    [InlineData("public static void Main(string[] args) {", "csharp")]
    [InlineData("SELECT name FROM users WHERE id = 3", "sql")]
    [InlineData("def compute(x):", "python")]
    public void A_line_of_code_is_code(string line, string language) =>
        Assert.Contains($"CODE:{language}", Types("Explique ceci :\n" + line));

    [Fact]
    public void A_stack_trace_is_code() =>
        Assert.Contains("CODE:stacktrace", Types("I get this:\n   at KAONOMI.Server.Program.Main(String[] args) in Program.cs:line 12"));

    [Fact]
    public void Contacts_dates_amounts_and_numbers_are_found_once()
    {
        var types = Types("Envoie 1 250 € à jerome@example.com avant le 3 novembre 2026 à 14h30, voir https://kaonomi.net/docs. Soit 15 % de 8333.");
        Assert.Contains("EMAIL:jerome@example.com", types);
        Assert.Contains("URL:https://kaonomi.net/docs", types);
        Assert.Contains(types, t => t.StartsWith("DATE:3 novembre 2026"));
        Assert.Contains("TIME:14h30", types);
        Assert.Contains(types, t => t.StartsWith("MONEY:"));
        Assert.Contains("PERCENT:15%", types);
        Assert.Contains("NUMBER:8333", types);
        Assert.DoesNotContain("NUMBER:2026", types);
    }

    [Theory]
    [InlineData("Combien font 12 * 7 + 3 ?")]
    [InlineData("Résous l'équation x² + 2x - 3 = 0")]
    [InlineData("What is the integral of sin(x)?")]
    public void Formulas_and_calculations_are_math(string prompt) =>
        Assert.Contains(Extractor.Extract(prompt), e => e.Type == EntityTypes.Math);

    [Fact]
    public void Names_of_the_gazetteers_people_and_proper_nouns()
    {
        var types = Types("Est-ce que KAONOMI tourne sous Godot ? Demande à M. Dupont, qui habite à Lyon.");
        Assert.Contains("ORG:KAONOMI", types);
        Assert.Contains("PRODUCT:Godot", types);
        Assert.Contains("PERSON:Dupont", types);
        Assert.Contains("PROPER_NOUN:Lyon", types);
        Assert.DoesNotContain(types, t => t == "PROPER_NOUN:Est");
    }

    [Fact]
    public void Programming_languages_are_named()
    {
        Assert.Contains("PROGRAMMING_LANGUAGE:csharp", Types("How do I read a file in C#?"));
        Assert.Contains("PROGRAMMING_LANGUAGE:python", Types("Écris un script Python qui trie une liste"));
    }

    [Theory]
    [InlineData("Quelle est la capitale de la France ?", "fr")]
    [InlineData("What is the capital of France?", "en")]
    public void The_language_of_the_prompt(string prompt, string language) =>
        Assert.Contains($"LANGUAGE:{language}", Types(prompt));

    [Fact]
    public void The_signature_keeps_the_values_that_change_the_answer()
    {
        var france = EntityExtractor.Signature(Extractor.Extract("What is the capital of France?"));
        var spain = EntityExtractor.Signature(Extractor.Extract("What is the capital of Spain?"));
        var france2 = EntityExtractor.Signature(Extractor.Extract("so, what's the capital of France ?"));
        Assert.NotEqual(france, spain);
        Assert.Equal(france, france2);
        Assert.NotEqual(EntityExtractor.Signature(Extractor.Extract("2 + 2 = ?")), EntityExtractor.Signature(Extractor.Extract("2 + 3 = ?")));
    }
}
