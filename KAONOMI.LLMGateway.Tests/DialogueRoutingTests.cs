using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Entities;
using KAONOMI.LLMGateway.Routing;

namespace KAONOMI.LLMGateway.Tests;

/// <summary>
/// The rules of <c>config/llmgateway.json</c> as they are: the plain dialogues stay on the default model; figures,
/// mathematics and a long sentence held by "si", "et"… go to the reasoning model.
/// </summary>
public class DialogueRoutingTests
{
    private const string Default = "lfm3-omni-2b";
    private const string Reasoner = "openai/gpt-oss-20b";

    private static readonly GatewaySettings Settings = Load();
    private static readonly EntityExtractor Extractor = new(Settings.Gazetteers);

    private static GatewaySettings Load()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "KAONOMI.slnx")))
            folder = folder.Parent;
        return GatewaySettings.Load(Path.Combine(folder!.FullName, "KAONOMI.LLMGateway", "config", "llmgateway.json"));
    }

    private static RouteDecision Route(string prompt, string model = "auto") =>
        new ModelRouter(() => Settings.Routing, () => [Default, Reasoner]).Route(model, Extractor.Extract(prompt));

    [Theory]
    [InlineData("Bonjour, comment vas-tu ?")]
    [InlineData("Raconte-moi une blague sur les chats.")]
    [InlineData("Quelle est la capitale de la France ?")]
    [InlineData("Hello, who are you and what can you do?")]
    [InlineData("Merci beaucoup, c'est gentil et très clair.")]
    [InlineData("Si tu veux, parle-moi de toi.")]
    public void A_plain_dialogue_stays_on_the_default_model(string prompt)
    {
        var decision = Route(prompt);
        Assert.Null(decision.Rule);
        Assert.Equal(Default, decision.Model);
    }

    [Theory]
    [InlineData("J'ai 3 pommes et j'en mange 2, combien en reste-t-il ?", "numbers")]
    [InlineData("Un loyer de 1 250 € par mois, c'est cher ?", "numbers")]
    [InlineData("Que représente une hausse de 12 % ?", "numbers")]
    [InlineData("Combien font 12 * 7 + 3 ?", "math")]
    [InlineData("Résous cette équation pour moi.", "math")]
    [InlineData("What is the derivative of x^2 + 3x?", "math")]
    public void Figures_and_mathematics_go_to_the_reasoning_model(string prompt, string rule)
    {
        var decision = Route(prompt);
        Assert.Equal(rule, decision.Rule);
        Assert.Equal(Reasoner, decision.Model);
    }

    [Theory]
    [InlineData("Si je pars de Paris le matin et que mon train a du retard, est-ce que je peux encore attraper la correspondance à Lyon ou dois-je prévoir une nuit sur place ?")]
    [InlineData("If the supplier delivers late and the client refuses to wait, should we cancel the order or negotiate a discount, and who has to decide that in the end?")]
    [InlineData("Explique-moi pourquoi, lorsque le projet prend du retard et que l'équipe est fatiguée, il vaut parfois mieux réduire le périmètre plutôt que repousser la date.")]
    public void A_long_sentence_with_si_and_et_goes_to_the_reasoning_model(string prompt)
    {
        var decision = Route(prompt);
        Assert.Equal("complex", decision.Rule);
        Assert.Equal(Reasoner, decision.Model);
    }

    [Fact]
    public void A_long_list_joined_by_et_alone_is_not_complex() =>
        Assert.Null(EntityExtractor.ComplexSentence(
            "J'aime les pommes et les poires et les cerises et les fraises et les framboises et les mûres et les abricots et les prunes."));

    [Fact]
    public void Short_sentences_one_after_the_other_are_not_complex() =>
        Assert.Null(EntityExtractor.ComplexSentence(
            "Si tu veux. Et toi ? Moi je vais bien. Alors on y va. Mais pas trop vite. Ou alors demain. Si c'est possible. Et voilà."));

    [Fact]
    public void A_model_asked_by_the_client_is_replaced_too() =>
        Assert.Equal(Reasoner, Route("Combien font 12 * 7 + 3 ?", Default).Model);

    [Fact]
    public void Complexity_does_not_change_the_entity_guard_of_the_cache()
    {
        var entities = Extractor.Extract("Si je pars de Paris le matin et que mon train a du retard, est-ce que je peux encore attraper la correspondance à Lyon ou dois-je prévoir une nuit sur place ?");
        Assert.Contains(entities, e => e.Type == EntityTypes.Complex);
        Assert.DoesNotContain("COMPLEX", EntityExtractor.Signature(entities));
    }
}
