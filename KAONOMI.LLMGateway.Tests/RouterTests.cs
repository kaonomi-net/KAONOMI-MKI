using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Entities;
using KAONOMI.LLMGateway.Routing;

namespace KAONOMI.LLMGateway.Tests;

public class RouterTests
{
    private static readonly EntityExtractor Extractor = new();

    private static RoutingSettings Settings(bool overrideRequested = true) => new()
    {
        Enabled = true,
        OverrideRequested = overrideRequested,
        DefaultModel = "general",
        Rules =
        [
            new RouteRule { Name = "images", Any = ["IMAGE"], Model = "vision", Priority = 100 },
            new RouteRule { Name = "code", Any = ["CODE", "PROGRAMMING_LANGUAGE"], Model = "coder", Priority = 80 },
            new RouteRule { Name = "math", Any = ["MATH"], None = ["CODE"], Model = "reasoner", Priority = 70 },
            new RouteRule { Name = "fr-names", All = ["LANGUAGE:fr"], Any = ["PROPER_NOUN", "PERSON"], Model = "multilingual", Priority = 20 },
            new RouteRule { Name = "missing", Any = ["URL"], Model = "not-installed", Priority = 10 }
        ]
    };

    private static ModelRouter Router(RoutingSettings settings) =>
        new(() => settings, () => ["general:latest", "coder", "reasoner", "vision", "multilingual"]);

    [Fact]
    public void Code_goes_to_the_coder() =>
        Assert.Equal("coder", Router(Settings()).Route("general", Extractor.Extract("Fix this:\n```csharp\nvar x = 1;\n```")).Model);

    [Fact]
    public void Auto_without_a_matching_rule_goes_to_the_default_model()
    {
        var decision = Router(Settings()).Route("auto", Extractor.Extract("Tell me a joke"));
        Assert.Equal("general", decision.Model);
        Assert.Null(decision.Rule);
    }

    [Fact]
    public void Without_override_only_auto_is_routed()
    {
        var router = Router(Settings(overrideRequested: false));
        Assert.Equal("general", router.Route("general", Extractor.Extract("Combien font 12 * 7 ?")).Model);
        Assert.Equal("reasoner", router.Route("auto", Extractor.Extract("Combien font 12 * 7 ?")).Model);
    }

    [Fact]
    public void None_excludes_and_All_requires_a_value()
    {
        var router = Router(Settings());
        Assert.Equal("coder", router.Route("auto", Extractor.Extract("```python\nprint(2 + 2)\n```")).Model);
        Assert.Equal("multilingual", router.Route("auto", Extractor.Extract("Qui était le roi de Navarre ?")).Model);
        Assert.Equal("general", router.Route("auto", Extractor.Extract("Who was the king of Navarre?")).Model);
    }

    [Fact]
    public void A_rule_whose_model_is_not_installed_is_skipped()
    {
        var decision = Router(Settings()).Route("general", Extractor.Extract("Read https://kaonomi.net"));
        Assert.Equal("general", decision.Model);
        Assert.Contains("not-installed", decision.Reason);
    }

    [Fact]
    public void The_highest_priority_wins_and_routing_off_keeps_the_model()
    {
        var entities = Extractor.Extract("```python\nprint(1)\n```").Append(new Entity(EntityTypes.Image, "image", "", 0, 0)).ToList();
        Assert.Equal("vision", Router(Settings()).Route("general", entities).Model);
        var off = Settings();
        off.Enabled = false;
        Assert.Equal("general", Router(off).Route("general", entities).Model);
    }

    [Fact]
    public void Latest_is_implied() => Assert.True(ModelRouter.IsInstalled(["general:latest"], "general"));
}
