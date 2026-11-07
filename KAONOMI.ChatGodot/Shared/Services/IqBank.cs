using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The bank of the questions of /iq: 1000 questions made once (<see cref="Generate"/>) and kept as JSON
/// (<c>KAONOMI.ChatGodot/Data/iq-questions.json</c>, one question a line): 300 number series, 300 matrices, 300 words
/// (150 in French, 150 in English) and 100 series of digits, each with its level from 1 to 10. A test draws its
/// questions from it at random, from the easiest to the hardest (<see cref="Pick"/>); without the file, the questions
/// are made on the spot.
/// </summary>
public sealed class IqBank
{
    public const int Numbers = 300, Matrices = 300, WordsPerLanguage = 150, Memory = 100;

    // The accents and apostrophes written as they are (qu'Inès, not qu'Inès): the file stays readable.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public IqBank(IReadOnlyList<IqQuestion> questions)
    {
        Questions = questions;
    }

    public IReadOnlyList<IqQuestion> Questions { get; }

    /// <summary>The 1000 questions, all different, from <paramref name="seed"/> (the same seed makes the same bank).</summary>
    public static IqBank Generate(int seed = 2026)
    {
        var random = new Random(seed);
        var all = new List<IqQuestion>();
        void Add(IqTestKind kind, int count, Func<int, IqQuestion> make)
        {
            var seen = new HashSet<string>();
            var made = new List<IqQuestion>();
            for (var guard = 0; made.Count < count && guard < count * 50; guard++)
            {
                // The levels spread evenly: as many questions of level 1 as of level 10.
                var level = made.Count % 10;
                var q = make(level);
                if (seen.Add(Key(q)))
                    made.Add(q with { Level = level + 1 });
            }
            all.AddRange(made);
        }
        Add(IqTestKind.Numbers, Numbers, level => IqTests.NumberSeries(random, level));
        Add(IqTestKind.Matrices, Matrices, level => IqTests.Matrix(random, level));
        Add(IqTestKind.Memory, Memory, level => IqTests.Memory(random, level));
        all.AddRange(IqWords.Make(random, "FR", WordsPerLanguage));
        all.AddRange(IqWords.Make(random, "EN", WordsPerLanguage));
        return new IqBank(all.Select((q, i) => q with { Id = $"{q.Kind.ToString().ToLowerInvariant()}-{i + 1:0000}" }).ToList());
    }

    /// <summary>What makes two questions the same: their text, their choices, their cells, their digits.</summary>
    private static string Key(IqQuestion q) =>
        q.Text + "|" + string.Join("|", q.Choices.OrderBy(c => c)) + "|" + q.Digits + "|" +
        (q.Cells is null ? "" : string.Join(";", q.Cells.Select(c => c?.ToString() ?? "?")));

    /// <summary>
    /// <paramref name="count"/> questions of <paramref name="kind"/> (words: of <paramref name="language"/>), drawn at
    /// random, their levels rising from 1 to 10 along the test; never twice the same question.
    /// </summary>
    public IReadOnlyList<IqQuestion> Pick(IqTestKind kind, int count, Random random, string language = "FR")
    {
        var code = language.Trim().ToUpperInvariant() == "FR" ? "FR" : "EN";
        var pool = Questions.Where(q => q.Kind == kind && (kind != IqTestKind.Words || q.Language == code)).ToList();
        var picked = new List<IqQuestion>();
        for (var i = 0; i < count && pool.Count > 0; i++)
        {
            // The level aimed at: 1 for the first question, 10 for the last; the nearest ones when none is left there.
            var aim = count == 1 ? 5 : 1 + (int)Math.Round(9.0 * i / (count - 1));
            var nearest = pool.Min(q => Math.Abs(q.Level - aim));
            var candidates = pool.Where(q => Math.Abs(q.Level - aim) == nearest).ToList();
            var q = candidates[random.Next(candidates.Count)];
            picked.Add(q);
            pool.Remove(q);
        }
        return picked;
    }

    /// <summary>Writes the bank as a JSON array, one question a line (readable, and its diffs too).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var text = new StringBuilder("[\n");
        for (var i = 0; i < Questions.Count; i++)
            text.Append("  ").Append(JsonSerializer.Serialize(Questions[i], JsonOptions)).Append(i + 1 < Questions.Count ? ",\n" : "\n");
        text.Append("]\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>The bank of <paramref name="path"/>, or null when it is missing or cannot be read.</summary>
    public static IqBank? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var questions = JsonSerializer.Deserialize<List<IqQuestion>>(File.ReadAllText(path), JsonOptions);
            return questions is { Count: > 0 } ? new IqBank(questions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            ActivityLog.Error("IQ", $"The bank of questions {path} could not be read", ex);
            return null;
        }
    }
}
