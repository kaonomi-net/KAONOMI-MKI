using System;
using System.Collections.Generic;
using System.Linq;

namespace KAONOMI.Chat.Services;

/// <summary>The tests of /iq.</summary>
public enum IqTestKind
{
    Numbers,
    Matrices,
    Words,
    Memory
}

/// <summary>A shape of a cell of a matrix.</summary>
public enum IqShape
{
    Circle,
    Square,
    Triangle,
    Diamond,
    Hexagon
}

/// <summary>A cell of a matrix: its shape, how many, filled or not, turned by a quarter, a third… (degrees).</summary>
public sealed record IqCell(IqShape Shape, int Count, bool Filled, int Rotation);

/// <summary>
/// A question of /iq: its text, its choices (a matrix: <see cref="Cells"/> and <see cref="CellChoices"/>; memory: the
/// digits to remember and no choice, the answer typed), the index of the right choice, and why it is right; in the bank
/// (<see cref="IqBank"/>) also its id, its level (1 the easiest, 10 the hardest) and, for words, its language.
/// </summary>
public sealed record IqQuestion(
    IqTestKind Kind,
    string Text,
    IReadOnlyList<string> Choices,
    int Answer,
    string Explanation,
    IReadOnlyList<IqCell?>? Cells = null,
    IReadOnlyList<IqCell>? CellChoices = null,
    string? Digits = null,
    int Level = 1,
    string? Language = null,
    string Id = "");

/// <summary>
/// The questions of /iq, made at random (from a seed) and harder along a test: number series, matrices of shapes in the
/// manner of Raven's progressive matrices, analogies and odd ones out (French or English), series of digits to
/// remember. And the indicative score: a share of right answers turned into a number on the scale of the IQ (mean
/// 100, deviation 15), which is not a measured IQ: that needs a standardized test given by a psychologist.
/// </summary>
public static class IqTests
{
    /// <summary>Questions in each test.</summary>
    public const int QuestionsPerTest = 10;

    /// <summary>Seconds given to each question (memory: none, its digits are shown for a while).</summary>
    public const int SecondsPerQuestion = 60;

    public static string Name(IqTestKind kind) => kind switch
    {
        IqTestKind.Numbers => "Number series",
        IqTestKind.Matrices => "Matrices",
        IqTestKind.Words => "Words",
        _ => "Memory"
    };

    /// <summary>The <see cref="QuestionsPerTest"/> questions of a test, from the easiest to the hardest.</summary>
    public static IReadOnlyList<IqQuestion> Make(IqTestKind kind, int seed, string language = "FR")
    {
        var random = new Random(seed);
        return kind switch
        {
            IqTestKind.Numbers => Enumerable.Range(0, QuestionsPerTest).Select(i => NumberSeries(random, i)).ToList(),
            IqTestKind.Matrices => Enumerable.Range(0, QuestionsPerTest).Select(i => Matrix(random, i)).ToList(),
            IqTestKind.Words => Words(random, language),
            _ => Enumerable.Range(0, QuestionsPerTest).Select(i => Memory(random, i)).ToList()
        };
    }

    /// <summary>
    /// The indicative IQ of a share of right answers: 50 % gives 100, each 20 % more or less gives 15 more or less,
    /// kept between 55 and 145.
    /// </summary>
    public static int Score(double share) => (int)Math.Round(Math.Clamp(100 + 15 * (share - 0.5) / 0.2, 55, 145));

    // ── Number series ──

    /// <summary>A series of 5 to 6 numbers and its next one; <paramref name="level"/> 0 to 9 picks harder rules.</summary>
    public static IqQuestion NumberSeries(Random random, int level)
    {
        var kinds = level switch
        {
            < 2 => new[] { 0, 1 },
            < 4 => new[] { 1, 2, 3 },
            < 7 => new[] { 2, 3, 4, 5 },
            _ => new[] { 4, 5, 6, 7 }
        };
        var kind = kinds[random.Next(kinds.Length)];
        List<long> terms;
        string rule;
        switch (kind)
        {
            case 0:
            {
                long a = random.Next(1, 20), d = random.Next(2, 10);
                terms = Enumerable.Range(0, 6).Select(i => a + d * i).ToList();
                rule = $"+{d} each time";
                break;
            }
            case 1:
            {
                long a = random.Next(1, 6), r = random.Next(2, 4);
                terms = Enumerable.Range(0, 6).Select(i => a * (long)Math.Pow(r, i)).ToList();
                rule = $"×{r} each time";
                break;
            }
            case 2:
            {
                // Growing differences: +d, +d+k, +d+2k…
                long a = random.Next(1, 15), d = random.Next(1, 5), k = random.Next(1, 4);
                terms = [a];
                for (var i = 0; i < 5; i++)
                    terms.Add(terms[^1] + d + k * i);
                rule = $"the difference grows by {k} each time (+{d}, +{d + k}, +{d + 2 * k}…)";
                break;
            }
            case 3:
            {
                // Two series in turn.
                long a = random.Next(1, 10), da = random.Next(2, 6), b = random.Next(20, 40), db = -random.Next(1, 5);
                terms = Enumerable.Range(0, 7).Select(i => i % 2 == 0 ? a + da * (i / 2) : b + db * (i / 2)).ToList();
                rule = $"two series in turn: one +{da}, the other {db}";
                break;
            }
            case 4:
            {
                long c = random.Next(-3, 6), start = random.Next(1, 4);
                terms = Enumerable.Range(0, 6).Select(i => (start + i) * (start + i) + c).ToList();
                rule = c == 0 ? "the squares" : $"the squares {(c > 0 ? "+" : "−")}{Math.Abs(c)}";
                break;
            }
            case 5:
            {
                long a = random.Next(1, 5), b = random.Next(1, 6);
                terms = [a, b];
                for (var i = 0; i < 5; i++)
                    terms.Add(terms[^1] + terms[^2]);
                rule = "each number is the sum of the two before";
                break;
            }
            case 6:
            {
                long a = random.Next(1, 6), m = random.Next(2, 4), p = random.Next(-2, 4);
                terms = [a];
                for (var i = 0; i < 5; i++)
                    terms.Add(terms[^1] * m + p);
                rule = $"×{m} then {(p >= 0 ? "+" : "−")}{Math.Abs(p)}";
                break;
            }
            default:
            {
                // Multiplied by 2, 3, 4, 5… in turn.
                long a = random.Next(1, 4);
                terms = [a];
                for (var i = 2; i <= 6; i++)
                    terms.Add(terms[^1] * i);
                rule = "×2, ×3, ×4, ×5…";
                break;
            }
        }
        var answer = terms[^1];
        var shown = terms.Take(terms.Count - 1);
        var choices = Distractors(random, answer, terms[^2]);
        var index = random.Next(4);
        choices.Insert(index, answer);
        return new IqQuestion(IqTestKind.Numbers, string.Join("   ", shown) + "   ?", choices.Select(c => c.ToString()).ToList(), index,
            $"{answer}: {rule}");
    }

    /// <summary>Three wrong numbers near <paramref name="answer"/>, all different.</summary>
    private static List<long> Distractors(Random random, long answer, long previous)
    {
        var step = Math.Max(1, Math.Abs(answer - previous));
        var candidates = new List<long> { answer + 1, answer - 1, answer + step, answer - step / 2, answer + step / 2, answer * 2, previous + step + 2, answer + 2, answer - 2 };
        var picked = new List<long>();
        foreach (var c in candidates.OrderBy(_ => random.Next()))
            if (c != answer && !picked.Contains(c) && picked.Count < 3)
                picked.Add(c);
        return picked;
    }

    // ── Matrices ──

    /// <summary>
    /// A 3 × 3 matrix whose last cell is missing: each attribute (shape, count, fill, rotation) is the same along a row,
    /// grows along it, or takes each of three values once per row; the harder the level, the more attributes move.
    /// Six choices: the right cell and cells differing from it by one or two attributes.
    /// </summary>
    public static IqQuestion Matrix(Random random, int level)
    {
        var moving = level switch { < 3 => 1, < 7 => 2, _ => 3 };
        var attributes = new[] { 0, 1, 2, 3 }.OrderBy(_ => random.Next()).Take(moving).ToHashSet();
        // A rule per attribute: 0 same along a row (changes down the column), 1 grows along the row, 2 three values once per row.
        int RuleFor(int attribute) => attributes.Contains(attribute) ? random.Next(1, 3) : 0;
        var rules = Enumerable.Range(0, 4).Select(RuleFor).ToArray();

        // A moving angle has to be seen: not on a circle, nor on a hexagon (60° looks like 0°), and in steps of 30°
        // (a square turned by 90° is the same square).
        var pool = rules[3] == 0 ? Enum.GetValues<IqShape>() : [IqShape.Square, IqShape.Triangle, IqShape.Diamond];
        var shapes = pool.OrderBy(_ => random.Next()).Take(3).ToArray();
        var counts = new[] { 1, 2, 3 };
        var fills = new[] { false, true, false };
        var rotations = new[] { 0, 30, 60 };
        var shift = random.Next(3);

        int Pick(int rule, int row, int column) => rule switch
        {
            0 => (row + shift) % 3,
            1 => column,
            _ => (row + column + shift) % 3
        };
        IqCell At(int row, int column) => new(
            shapes[Pick(rules[0], row, column)],
            counts[Pick(rules[1], row, column)],
            // Fill has two values: "three values" alternates it.
            rules[2] == 0 ? fills[(row + shift) % 2] : (row + column + shift) % 2 == 1,
            rules[3] == 0 ? 0 : rotations[Pick(rules[3], row, column)]);

        var cells = new List<IqCell?>();
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                cells.Add(row == 2 && column == 2 ? null : At(row, column));
        var answer = At(2, 2);

        var choices = new List<IqCell> { answer };
        var guard = 0;
        while (choices.Count < 6 && guard++ < 200)
        {
            var wrong = answer;
            var changes = random.Next(1, 3);
            for (var k = 0; k < changes; k++)
                wrong = random.Next(4) switch
                {
                    0 => wrong with { Shape = shapes[(Array.IndexOf(shapes, wrong.Shape) + 1 + random.Next(2)) % 3] },
                    1 => wrong with { Count = wrong.Count % 3 + 1 },
                    2 => wrong with { Filled = !wrong.Filled },
                    _ => wrong with { Rotation = (wrong.Rotation + 30) % 90 }
                };
            // A square or a circle turned by 90° looks the same: such a choice would be right too.
            if (!choices.Any(c => Same(c, wrong)))
                choices.Add(wrong);
        }
        choices = choices.OrderBy(_ => random.Next()).ToList();
        var index = choices.FindIndex(c => ReferenceEquals(c, answer) || c == answer);
        var said = string.Join("; ", Enumerable.Range(0, 4).Where(a => rules[a] != 0).Select(a =>
            a == 2 ? "the fill alternates from one cell to the next"
            : $"{new[] { "the shape", "the number", "the fill", "the angle" }[a]} {(rules[a] == 1 ? "changes the same way along each row" : "takes each value once in each row")}"));
        return new IqQuestion(IqTestKind.Matrices, "Which cell completes the matrix?", choices.Select((_, i) => ((char)('A' + i)).ToString()).ToList(), index,
            said.Length == 0 ? "each row repeats one cell" : char.ToUpper(said[0]) + said[1..], cells, choices);
    }

    /// <summary>True when two cells are drawn alike (a circle has no angle; a square turned by 90° is a square).</summary>
    public static bool Same(IqCell a, IqCell b)
    {
        if (a.Shape != b.Shape || a.Count != b.Count || a.Filled != b.Filled)
            return false;
        var symmetry = a.Shape switch { IqShape.Circle => 1, IqShape.Square or IqShape.Diamond => 90, IqShape.Hexagon => 60, _ => 120 };
        return symmetry == 1 || (a.Rotation - b.Rotation) % symmetry == 0;
    }

    // ── Words ──

    private sealed record WordItem(string Text, string[] Choices, int Answer, string Why);

    private static readonly WordItem[] French =
    [
        new("Chaud est à froid ce que haut est à…", ["bas", "chaud", "grand", "loin"], 0, "des contraires"),
        new("Livre est à lire ce que fourchette est à…", ["couteau", "manger", "cuisine", "assiette"], 1, "l'objet et ce qu'on en fait"),
        new("Oiseau est à nid ce que abeille est à…", ["miel", "fleur", "ruche", "reine"], 2, "l'animal et sa maison"),
        new("Main est à gant ce que pied est à…", ["orteil", "chaussette", "jambe", "marcher"], 1, "ce qui recouvre"),
        new("Jour est à semaine ce que mois est à…", ["an", "heure", "lundi", "calendrier"], 0, "la partie et le tout, d'un cran plus grand"),
        new("Peintre est à pinceau ce que écrivain est à…", ["livre", "stylo", "lecteur", "page"], 1, "l'artisan et son outil"),
        new("Eau est à soif ce que nourriture est à…", ["faim", "repas", "cuisine", "manger"], 0, "ce qui apaise un besoin"),
        new("Graine est à arbre ce que œuf est à…", ["coquille", "poule", "nid", "jaune"], 1, "ce qui devient"),
        new("Pilote est à avion ce que capitaine est à…", ["mer", "port", "navire", "équipage"], 2, "celui qui conduit et l'engin"),
        new("Seconde est à minute ce que centimètre est à…", ["kilo", "mètre", "règle", "longueur"], 1, "cent ou soixante fois plus"),
        new("Quel mot ne va pas avec les autres ?", ["pomme", "poire", "carotte", "cerise"], 2, "la carotte est un légume, les autres des fruits"),
        new("Quel mot ne va pas avec les autres ?", ["violon", "guitare", "harpe", "trompette"], 3, "la trompette est un cuivre, les autres ont des cordes"),
        new("Quel mot ne va pas avec les autres ?", ["Paris", "Lyon", "Madrid", "Marseille"], 2, "Madrid n'est pas en France"),
        new("Quel mot ne va pas avec les autres ?", ["rapide", "vite", "lent", "prompt"], 2, "lent est le contraire des trois autres"),
        new("Quel mot ne va pas avec les autres ?", ["requin", "dauphin", "baleine", "orque"], 0, "le requin est un poisson, les autres des mammifères"),
        new("Quel mot ne va pas avec les autres ?", ["triangle", "carré", "cube", "cercle"], 2, "le cube a trois dimensions"),
        new("Si tous les Blips sont des Zorgs et que certains Zorgs sont verts, alors…", ["tous les Blips sont verts", "certains Blips sont peut-être verts", "aucun Blip n'est vert", "tous les Zorgs sont des Blips"], 1, "rien ne dit lesquels des Zorgs sont verts"),
        new("Marie est plus grande que Luc, Luc est plus grand que Paul. Qui est le plus petit ?", ["Marie", "Luc", "Paul", "on ne sait pas"], 2, "Marie > Luc > Paul")
    ];

    private static readonly WordItem[] English =
    [
        new("Hot is to cold as high is to…", ["low", "hot", "tall", "far"], 0, "opposites"),
        new("Book is to read as fork is to…", ["knife", "eat", "kitchen", "plate"], 1, "the thing and what it is used for"),
        new("Bird is to nest as bee is to…", ["honey", "flower", "hive", "queen"], 2, "the animal and its home"),
        new("Hand is to glove as foot is to…", ["toe", "sock", "leg", "walk"], 1, "what covers it"),
        new("Day is to week as month is to…", ["year", "hour", "Monday", "calendar"], 0, "the part and the whole, one size up"),
        new("Painter is to brush as writer is to…", ["book", "pen", "reader", "page"], 1, "the maker and the tool"),
        new("Water is to thirst as food is to…", ["hunger", "meal", "kitchen", "eat"], 0, "what eases a need"),
        new("Seed is to tree as egg is to…", ["shell", "hen", "nest", "yolk"], 1, "what it becomes"),
        new("Pilot is to plane as captain is to…", ["sea", "harbour", "ship", "crew"], 2, "who steers and what"),
        new("Second is to minute as centimetre is to…", ["kilo", "metre", "ruler", "length"], 1, "a hundred or sixty times more"),
        new("Which word does not belong?", ["apple", "pear", "carrot", "cherry"], 2, "a carrot is a vegetable, the others fruit"),
        new("Which word does not belong?", ["violin", "guitar", "harp", "trumpet"], 3, "the trumpet is brass, the others have strings"),
        new("Which word does not belong?", ["London", "Manchester", "Madrid", "Leeds"], 2, "Madrid is not in England"),
        new("Which word does not belong?", ["quick", "fast", "slow", "swift"], 2, "slow is the opposite of the other three"),
        new("Which word does not belong?", ["shark", "dolphin", "whale", "orca"], 0, "the shark is a fish, the others mammals"),
        new("Which word does not belong?", ["triangle", "square", "cube", "circle"], 2, "the cube has three dimensions"),
        new("If all Blips are Zorgs and some Zorgs are green, then…", ["all Blips are green", "some Blips may be green", "no Blip is green", "all Zorgs are Blips"], 1, "nothing says which Zorgs are green"),
        new("Mary is taller than Luke, Luke is taller than Paul. Who is the shortest?", ["Mary", "Luke", "Paul", "cannot tell"], 2, "Mary > Luke > Paul")
    ];

    /// <summary><see cref="QuestionsPerTest"/> items of the bank of the language, their choices shuffled.</summary>
    private static List<IqQuestion> Words(Random random, string language)
    {
        var bank = language.Trim().ToUpperInvariant() == "FR" ? French : English;
        return bank.OrderBy(_ => random.Next()).Take(QuestionsPerTest).Select(item =>
        {
            var order = Enumerable.Range(0, item.Choices.Length).OrderBy(_ => random.Next()).ToList();
            return new IqQuestion(IqTestKind.Words, item.Text, order.Select(i => item.Choices[i]).ToList(), order.IndexOf(item.Answer),
                $"{item.Choices[item.Answer]}: {item.Why}");
        }).ToList();
    }

    // ── Memory ──

    /// <summary>A series of 4 to 13 digits (longer along the test), no digit twice in a row.</summary>
    public static IqQuestion Memory(Random random, int level)
    {
        var length = 4 + level;
        var digits = new char[length];
        for (var i = 0; i < length; i++)
        {
            char d;
            do
                d = (char)('0' + random.Next(10));
            while (i > 0 && d == digits[i - 1]);
            digits[i] = d;
        }
        var text = new string(digits);
        return new IqQuestion(IqTestKind.Memory, $"Remember these {length} digits, then type them", [], -1, text, Digits: text);
    }

    /// <summary>How long the digits of a memory question are shown: 0.8 s a digit, 3 s at least.</summary>
    public static double ShowSeconds(IqQuestion question) => Math.Max(3, (question.Digits?.Length ?? 0) * 0.8);
}
