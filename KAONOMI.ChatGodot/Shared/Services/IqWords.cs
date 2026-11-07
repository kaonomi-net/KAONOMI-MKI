using System;
using System.Collections.Generic;
using System.Linq;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The questions of words of /iq, made from tables in French and in English: analogies (opposites, an animal and its
/// home, an animal and its young, a trade and its tool, a country and its capital, a part and its whole), the odd one
/// out (three words of a kind and one of a close kind), orders (who has the most marbles, who runs the fastest) and
/// syllogisms with words that mean nothing (only the logic counts). Their level: from 1 (the odd one out between fruits
/// and vegetables) to 10 (a syllogism).
/// </summary>
public static class IqWords
{
    private sealed record Relation(string Name, int Level, (string A, string B)[] Pairs);

    private sealed record Kind(string Plural, string Singular, string[] Words);

    private sealed record Language(
        string Analogy, Relation[] Relations,
        string OddText, string OddWhy, (Kind A, Kind B, int Level)[] Odds,
        string[] Names, (string More, string MostQuestion, string LeastQuestion, string Unknown)[] Orders,
        string[] Nonsense, (string Text, string[] Choices, int Answer, string Why)[] Syllogisms);

    // ── French ──

    private static readonly Kind FrFruits = new("des fruits", "un légume", ["pomme", "poire", "cerise", "banane", "fraise", "pêche", "abricot", "prune", "kiwi", "mangue"]);
    private static readonly Kind FrVegetables = new("des légumes", "un fruit", ["carotte", "poireau", "navet", "radis", "épinard", "chou", "courgette", "haricot"]);
    private static readonly Kind FrMammals = new("des mammifères", "un oiseau", ["chien", "chat", "cheval", "vache", "lapin", "loup", "ours", "renard"]);
    private static readonly Kind FrBirds = new("des oiseaux", "un mammifère", ["aigle", "moineau", "pigeon", "merle", "hibou", "corbeau", "mouette", "perroquet"]);
    private static readonly Kind FrFish = new("des poissons", "un mammifère", ["truite", "saumon", "thon", "sardine", "carpe", "brochet", "morue"]);
    private static readonly Kind FrInsects = new("des insectes", "un oiseau", ["fourmi", "abeille", "mouche", "guêpe", "coccinelle", "papillon", "criquet"]);
    private static readonly Kind FrStrings = new("des instruments à cordes", "un instrument à vent", ["violon", "guitare", "harpe", "violoncelle", "contrebasse", "banjo"]);
    private static readonly Kind FrWinds = new("des instruments à vent", "un instrument à cordes", ["trompette", "flûte", "clarinette", "saxophone", "hautbois", "trombone"]);
    private static readonly Kind FrEurope = new("des pays d'Europe", "un pays d'Asie", ["France", "Espagne", "Italie", "Allemagne", "Suède", "Pologne", "Grèce", "Portugal"]);
    private static readonly Kind FrAsia = new("des pays d'Asie", "un pays d'Europe", ["Japon", "Chine", "Inde", "Thaïlande", "Vietnam", "Corée"]);
    private static readonly Kind FrFurniture = new("des meubles", "un vêtement", ["chaise", "table", "armoire", "lit", "commode", "canapé"]);
    private static readonly Kind FrClothes = new("des vêtements", "un meuble", ["chemise", "pantalon", "veste", "robe", "jupe", "manteau"]);
    private static readonly Kind FrMetals = new("des métaux", "une planète", ["fer", "cuivre", "zinc", "plomb", "étain", "nickel"]);
    private static readonly Kind FrPlanets = new("des planètes", "un métal", ["Mars", "Vénus", "Jupiter", "Saturne", "Neptune", "Uranus"]);

    private static readonly Language French = new(
        "{0} est à {1} ce que {2} est à…",
        [
            new("des contraires", 2, [("chaud", "froid"), ("haut", "bas"), ("grand", "petit"), ("jour", "nuit"), ("rapide", "lent"), ("plein", "vide"), ("ouvert", "fermé"), ("riche", "pauvre"), ("dur", "mou"), ("clair", "sombre"), ("jeune", "vieux"), ("entrée", "sortie"), ("gagner", "perdre"), ("avant", "après"), ("monter", "descendre"), ("lourd", "léger")]),
            new("l'adulte et son petit", 3, [("chat", "chaton"), ("chien", "chiot"), ("vache", "veau"), ("cheval", "poulain"), ("mouton", "agneau"), ("poule", "poussin"), ("canard", "caneton"), ("lion", "lionceau"), ("chèvre", "chevreau"), ("ours", "ourson")]),
            new("l'animal et sa maison", 4, [("oiseau", "nid"), ("abeille", "ruche"), ("chien", "niche"), ("cheval", "écurie"), ("lapin", "terrier"), ("araignée", "toile"), ("vache", "étable"), ("cochon", "porcherie"), ("fourmi", "fourmilière"), ("poule", "poulailler")]),
            new("la partie et le tout", 5, [("page", "livre"), ("feuille", "arbre"), ("doigt", "main"), ("roue", "voiture"), ("touche", "clavier"), ("brique", "mur"), ("mot", "phrase"), ("joueur", "équipe"), ("étoile", "galaxie"), ("maillon", "chaîne")]),
            new("le métier et son outil", 5, [("peintre", "pinceau"), ("écrivain", "stylo"), ("menuisier", "scie"), ("jardinier", "râteau"), ("chirurgien", "scalpel"), ("pêcheur", "canne"), ("forgeron", "enclume"), ("couturier", "aiguille"), ("bûcheron", "hache"), ("maçon", "truelle")]),
            new("le pays et sa capitale", 6, [("France", "Paris"), ("Espagne", "Madrid"), ("Italie", "Rome"), ("Allemagne", "Berlin"), ("Portugal", "Lisbonne"), ("Japon", "Tokyo"), ("Égypte", "Le Caire"), ("Canada", "Ottawa"), ("Russie", "Moscou"), ("Grèce", "Athènes"), ("Belgique", "Bruxelles"), ("Norvège", "Oslo")])
        ],
        "Quel mot ne va pas avec les autres ?",
        "{0}: c'est {1}, les autres sont {2}",
        [(FrFruits, FrVegetables, 1), (FrVegetables, FrFruits, 1), (FrFurniture, FrClothes, 1), (FrClothes, FrFurniture, 1), (FrMammals, FrBirds, 2), (FrBirds, FrMammals, 2),
         (FrEurope, FrAsia, 2), (FrAsia, FrEurope, 3), (FrStrings, FrWinds, 3), (FrWinds, FrStrings, 3), (FrFish, FrMammals, 3), (FrInsects, FrBirds, 2), (FrMetals, FrPlanets, 4), (FrPlanets, FrMetals, 4)],
        ["Marie", "Luc", "Paul", "Emma", "Hugo", "Léa", "Nina", "Tom", "Inès", "Jules"],
        [("{0} a plus de billes que {1}.", "Qui a le plus de billes ?", "Qui a le moins de billes ?", "on ne sait pas"),
         ("{0} court plus vite que {1}.", "Qui court le plus vite ?", "Qui court le moins vite ?", "on ne sait pas")],
        ["blip", "zorg", "fum", "trak", "wib", "gruk", "plon", "snif"],
        [
            ("Tous les {0}s sont des {1}s. Tous les {1}s sont des {2}s. Donc…", ["tous les {0}s sont des {2}s", "tous les {2}s sont des {0}s", "aucun {0} n'est un {2}", "on ne peut rien conclure"], 0, "les {0}s sont dans les {1}s, qui sont dans les {2}s"),
            ("Aucun {0} n'est un {1}. Tous les {2}s sont des {1}s. Donc…", ["aucun {2} n'est un {0}", "tous les {2}s sont des {0}s", "certains {0}s sont des {2}s", "on ne peut rien conclure"], 0, "les {2}s sont tous des {1}s, et aucun {1} n'est un {0}"),
            ("Tous les {0}s sont des {1}s. Certains {1}s sont verts. Donc…", ["tous les {0}s sont verts", "certains {0}s sont verts, c'est sûr", "aucun {0} n'est vert", "on ne peut pas savoir si un {0} est vert"], 3, "les {1}s verts ne sont peut-être pas des {0}s"),
            ("Certains {0}s sont des {1}s. Tous les {1}s sont des {2}s. Donc…", ["certains {0}s sont des {2}s", "tous les {0}s sont des {2}s", "aucun {0} n'est un {2}", "tous les {2}s sont des {0}s"], 0, "les {0}s qui sont des {1}s sont aussi des {2}s"),
            ("Si un {0} est rouge, il chante. Ce {0} ne chante pas. Donc…", ["il n'est pas rouge", "il est rouge", "il chantera demain", "on ne peut rien conclure"], 0, "s'il était rouge, il chanterait (contraposée)"),
            ("Si un {0} est rouge, il chante. Ce {0} chante. Donc…", ["il est rouge", "il n'est pas rouge", "il est peut-être rouge, peut-être pas", "tous les {0}s chantent"], 2, "un {0} qui n'est pas rouge peut chanter aussi")
        ]);

    // ── English ──

    private static readonly Kind EnFruits = new("fruits", "a vegetable", ["apple", "pear", "cherry", "banana", "strawberry", "peach", "apricot", "plum", "kiwi", "mango"]);
    private static readonly Kind EnVegetables = new("vegetables", "a fruit", ["carrot", "leek", "turnip", "radish", "spinach", "cabbage", "courgette", "bean"]);
    private static readonly Kind EnMammals = new("mammals", "a bird", ["dog", "cat", "horse", "cow", "rabbit", "wolf", "bear", "fox"]);
    private static readonly Kind EnBirds = new("birds", "a mammal", ["eagle", "sparrow", "pigeon", "blackbird", "owl", "crow", "seagull", "parrot"]);
    private static readonly Kind EnFish = new("fish", "a mammal", ["trout", "salmon", "tuna", "sardine", "carp", "pike", "cod"]);
    private static readonly Kind EnInsects = new("insects", "a bird", ["ant", "bee", "fly", "wasp", "ladybird", "butterfly", "cricket"]);
    private static readonly Kind EnStrings = new("string instruments", "a wind instrument", ["violin", "guitar", "harp", "cello", "double bass", "banjo"]);
    private static readonly Kind EnWinds = new("wind instruments", "a string instrument", ["trumpet", "flute", "clarinet", "saxophone", "oboe", "trombone"]);
    private static readonly Kind EnEurope = new("countries of Europe", "a country of Asia", ["France", "Spain", "Italy", "Germany", "Sweden", "Poland", "Greece", "Portugal"]);
    private static readonly Kind EnAsia = new("countries of Asia", "a country of Europe", ["Japan", "China", "India", "Thailand", "Vietnam", "Korea"]);
    private static readonly Kind EnFurniture = new("furniture", "clothing", ["chair", "table", "wardrobe", "bed", "dresser", "sofa"]);
    private static readonly Kind EnClothes = new("clothing", "furniture", ["shirt", "trousers", "jacket", "dress", "skirt", "coat"]);
    private static readonly Kind EnMetals = new("metals", "a planet", ["iron", "copper", "zinc", "lead", "tin", "nickel"]);
    private static readonly Kind EnPlanets = new("planets", "a metal", ["Mars", "Venus", "Jupiter", "Saturn", "Neptune", "Uranus"]);

    private static readonly Language English = new(
        "{0} is to {1} as {2} is to…",
        [
            new("opposites", 2, [("hot", "cold"), ("high", "low"), ("big", "small"), ("day", "night"), ("fast", "slow"), ("full", "empty"), ("open", "closed"), ("rich", "poor"), ("hard", "soft"), ("light", "dark"), ("young", "old"), ("entrance", "exit"), ("win", "lose"), ("before", "after"), ("up", "down"), ("wet", "dry")]),
            new("the adult and its young", 3, [("cat", "kitten"), ("dog", "puppy"), ("cow", "calf"), ("horse", "foal"), ("sheep", "lamb"), ("hen", "chick"), ("duck", "duckling"), ("lion", "cub"), ("goat", "kid"), ("frog", "tadpole")]),
            new("the animal and its home", 4, [("bird", "nest"), ("bee", "hive"), ("dog", "kennel"), ("horse", "stable"), ("rabbit", "burrow"), ("spider", "web"), ("cow", "barn"), ("pig", "sty"), ("ant", "anthill"), ("hen", "coop")]),
            new("the part and the whole", 5, [("page", "book"), ("leaf", "tree"), ("finger", "hand"), ("wheel", "car"), ("key", "keyboard"), ("brick", "wall"), ("word", "sentence"), ("player", "team"), ("star", "galaxy"), ("link", "chain")]),
            new("the trade and its tool", 5, [("painter", "brush"), ("writer", "pen"), ("carpenter", "saw"), ("gardener", "rake"), ("surgeon", "scalpel"), ("angler", "rod"), ("blacksmith", "anvil"), ("tailor", "needle"), ("lumberjack", "axe"), ("mason", "trowel")]),
            new("the country and its capital", 6, [("France", "Paris"), ("Spain", "Madrid"), ("Italy", "Rome"), ("Germany", "Berlin"), ("Portugal", "Lisbon"), ("Japan", "Tokyo"), ("Egypt", "Cairo"), ("Canada", "Ottawa"), ("Russia", "Moscow"), ("Greece", "Athens"), ("Belgium", "Brussels"), ("Norway", "Oslo")])
        ],
        "Which word does not belong?",
        "{0}: it is {1}, the others are {2}",
        [(EnFruits, EnVegetables, 1), (EnVegetables, EnFruits, 1), (EnFurniture, EnClothes, 1), (EnClothes, EnFurniture, 1), (EnMammals, EnBirds, 2), (EnBirds, EnMammals, 2),
         (EnEurope, EnAsia, 2), (EnAsia, EnEurope, 3), (EnStrings, EnWinds, 3), (EnWinds, EnStrings, 3), (EnFish, EnMammals, 3), (EnInsects, EnBirds, 2), (EnMetals, EnPlanets, 4), (EnPlanets, EnMetals, 4)],
        ["Mary", "Luke", "Paul", "Emma", "Hugo", "Lea", "Nina", "Tom", "Ines", "Jules"],
        [("{0} has more marbles than {1}.", "Who has the most marbles?", "Who has the fewest marbles?", "cannot tell"),
         ("{0} runs faster than {1}.", "Who runs the fastest?", "Who runs the slowest?", "cannot tell")],
        ["blip", "zorg", "fum", "trak", "wib", "gruk", "plon", "snif"],
        [
            ("All {0}s are {1}s. All {1}s are {2}s. So…", ["all {0}s are {2}s", "all {2}s are {0}s", "no {0} is a {2}", "nothing can be concluded"], 0, "the {0}s are among the {1}s, who are among the {2}s"),
            ("No {0} is a {1}. All {2}s are {1}s. So…", ["no {2} is a {0}", "all {2}s are {0}s", "some {0}s are {2}s", "nothing can be concluded"], 0, "every {2} is a {1}, and no {1} is a {0}"),
            ("All {0}s are {1}s. Some {1}s are green. So…", ["all {0}s are green", "some {0}s are green, for sure", "no {0} is green", "we cannot tell whether a {0} is green"], 3, "the green {1}s may not be {0}s"),
            ("Some {0}s are {1}s. All {1}s are {2}s. So…", ["some {0}s are {2}s", "all {0}s are {2}s", "no {0} is a {2}", "all {2}s are {0}s"], 0, "the {0}s that are {1}s are {2}s too"),
            ("If a {0} is red, it sings. This {0} does not sing. So…", ["it is not red", "it is red", "it will sing tomorrow", "nothing can be concluded"], 0, "if it were red, it would sing (contrapositive)"),
            ("If a {0} is red, it sings. This {0} sings. So…", ["it is red", "it is not red", "it may or may not be red", "all {0}s sing"], 2, "a {0} that is not red may sing too")
        ]);

    /// <summary><paramref name="count"/> different questions of words in <paramref name="language"/> (FR or EN).</summary>
    public static List<IqQuestion> Make(Random random, string language, int count)
    {
        var lang = language.Trim().ToUpperInvariant() == "FR" ? French : English;
        var code = lang == French ? "FR" : "EN";
        var made = new List<IqQuestion>();
        var seen = new HashSet<string>();
        var guard = 0;
        while (made.Count < count && guard++ < count * 50)
        {
            // About 40 % analogies, 30 % odd ones out, 20 % orders, 10 % syllogisms.
            var roll = random.Next(10);
            var q = roll < 4 ? Analogy(random, lang) : roll < 7 ? OddOneOut(random, lang) : roll < 9 ? Order(random, lang) : Syllogism(random, lang);
            if (seen.Add(q.Text + "|" + string.Join("|", q.Choices.OrderBy(c => c))))
                made.Add(q with { Language = code });
        }
        return made;
    }

    /// <summary>"a is to b as c is to…": d, the other three words of the same relation, wrong for c.</summary>
    private static IqQuestion Analogy(Random random, Language lang)
    {
        var relation = lang.Relations[random.Next(lang.Relations.Length)];
        var picks = relation.Pairs.OrderBy(_ => random.Next()).Take(5).ToList();
        var (a, b) = picks[0];
        var (c, d) = picks[1];
        // The wrong choices: the second words of other pairs of the relation (right for another word, not for c).
        var wrong = picks.Skip(2).Select(p => p.B).Where(w => w != d).Take(3).ToList();
        return Shuffle(random, IqTestKind.Words, Elide(lang, string.Format(lang.Analogy, a, b, c)), d, wrong, $"{d}: {relation.Name}", relation.Level + random.Next(2));
    }

    /// <summary>French: "que Inès" is "qu'Inès", "ce que abeille" is "ce qu'abeille" (the e dropped before a vowel).</summary>
    private static string Elide(Language lang, string text) => lang == French
        ? System.Text.RegularExpressions.Regex.Replace(text, @"\bque ([AEIOUYÉÈÊÀÂÎÔaeiouyéèêàâîô])", "qu'$1")
        : text;

    /// <summary>Three words of a kind and one of a close kind.</summary>
    private static IqQuestion OddOneOut(Random random, Language lang)
    {
        var (kind, other, level) = lang.Odds[random.Next(lang.Odds.Length)];
        var three = kind.Words.OrderBy(_ => random.Next()).Take(3).ToList();
        var odd = other.Words[random.Next(other.Words.Length)];
        return Shuffle(random, IqTestKind.Words, lang.OddText, odd, three, string.Format(lang.OddWhy, odd, kind.Singular, kind.Plural), level + random.Next(2));
    }

    /// <summary>
    /// Three or four people in an order told by the pairs side by side, given in a mixed order: who is first, or last?
    /// With three, "cannot tell" is a choice too.
    /// </summary>
    private static IqQuestion Order(Random random, Language lang)
    {
        var n = random.Next(2) == 0 ? 3 : 4;
        var people = lang.Names.OrderBy(_ => random.Next()).Take(n).ToList();
        var (more, most, least, unknown) = lang.Orders[random.Next(lang.Orders.Length)];
        string Fact(int i) => Elide(lang, string.Format(more, people[i], people[i + 1]));
        var facts = Enumerable.Range(0, n - 1).Select(Fact).OrderBy(_ => random.Next());
        var askMost = random.Next(2) == 0;
        var answer = askMost ? people[0] : people[^1];
        var wrong = people.Where(p => p != answer).ToList();
        if (n == 3)
            wrong.Add(unknown);
        var why = string.Join(" > ", people);
        return Shuffle(random, IqTestKind.Words, string.Join(" ", facts) + " " + (askMost ? most : least), answer, wrong.Take(3).ToList(), $"{answer}: {why}", n == 3 ? 6 + random.Next(2) : 8 + random.Next(2));
    }

    /// <summary>A syllogism over words that mean nothing: only its logic gives the answer.</summary>
    private static IqQuestion Syllogism(Random random, Language lang)
    {
        var (text, choices, answer, why) = lang.Syllogisms[random.Next(lang.Syllogisms.Length)];
        var words = lang.Nonsense.OrderBy(_ => random.Next()).Take(3).ToArray<object>();
        var filled = choices.Select(c => string.Format(c, words)).ToList();
        var right = filled[answer];
        return Shuffle(random, IqTestKind.Words, string.Format(text, words), right, filled.Where((_, i) => i != answer).ToList(),
            $"{right}: {string.Format(why, words)}", 9 + random.Next(2));
    }

    /// <summary>The right word among the wrong ones, in a random place.</summary>
    private static IqQuestion Shuffle(Random random, IqTestKind kind, string text, string right, List<string> wrong, string why, int level)
    {
        var choices = wrong.Distinct().Where(w => w != right).Take(3).Append(right).OrderBy(_ => random.Next()).ToList();
        return new IqQuestion(kind, text, choices, choices.IndexOf(right), why, Level: Math.Clamp(level, 1, 10));
    }
}
