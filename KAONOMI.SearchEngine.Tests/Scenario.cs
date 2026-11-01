using KAONOMI.Core.Data;
using KAONOMI.Core.Models;

namespace KAONOMI.SearchEngine.Tests;

/// <summary>
/// What KAONOMI does with Elasticsearch, through its own repository (<see cref="ElasticRepository"/>, NEST 7.17): saves,
/// loads, deletes, the fuzzy / wildcard searches on one or several fields (with <c>and</c> / <c>or</c>, without the
/// deleted ones), the last documents, the documents since a date. The results are written as lines, to be compared
/// between Elasticsearch and the engine.
/// </summary>
public static class Scenario
{
    public static readonly DateTime Start = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Texts =
    [
        "Le cassoulet de Castelnaudary est TOP",
        "Les cassoulets toulousains, une recette maison",
        "Café crème à Paris avant la réunion",
        "Rendez-vous chez le dentiste mardi",
        "Cassoulet, confit de canard et haricots tarbais",
        "Analyse du marché des semi-conducteurs",
        "kaonomi relation graph between entities",
        "Réunion d'équipe : budget et planning",
        "Promenade au bord de la Garonne",
        "Le chat dort sur le canapé",
        "Recette de la soupe à l'oignon",
        "Un cassoulet sans haricots n'est pas un cassoulet",
    ];

    public static List<string> Run(ElasticRepository repository)
    {
        var lines = new List<string>();
        void Add(string label, IEnumerable<EntityBase?> found) =>
            lines.Add($"{label}: {string.Join(",", found.Select(e => e?.id ?? "null"))}");

        // Saves: the diaries (one in three deleted), the last one searchable at once.
        var diaries = new List<Diary>();
        for (var i = 0; i < Texts.Length * 2; i++)
        {
            var diary = new Diary
            {
                text = Texts[i % Texts.Length] + (i >= Texts.Length ? " (bis)" : string.Empty),
                feeling = i % 2 == 0 ? "joy" : "calm",
                date = Start.AddHours(i).AddTicks(i * 1234),
                comment = "entry " + i,
                isDeleted = i % 3 == 2
            };
            diary.id = $"diary-{i:00}";
            diaries.Add(diary);
            if (i == Texts.Length * 2 - 1)
                lines.Add("save searchable: " + repository.SaveSearchable(diary));
            else
                lines.Add("save: " + repository.Save(diary));
        }

        // Load (all the fields come back).
        var loaded = (Diary?)repository.Load<Diary>("diary-04");
        lines.Add($"load: {loaded?.id} {loaded?.text} {loaded?.feeling} {loaded?.date:O} {loaded?.isDeleted}");
        lines.Add($"load missing: {repository.Load<Diary>("nope") ?? "null"}");

        // The searches of the chat (SessionManager): several fields, without the deleted ones.
        string[] fields = ["text", "comment"];
        Add("search cassoulet", repository.Search<Diary>("cassoulet", fields, 100, nameof(Diary.isDeleted)));
        Add("search typo", repository.Search<Diary>("casoulet", fields, 100, nameof(Diary.isDeleted)));
        Add("search accents", repository.Search<Diary>("cafe creme", fields, 100, nameof(Diary.isDeleted)));
        Add("search part of a word", repository.Search<Diary>("tities", fields, 100, nameof(Diary.isDeleted)));
        Add("search part of a text", repository.Search<Diary>("de canard et", fields, 100, nameof(Diary.isDeleted)));
        Add("search and/or", repository.Search<Diary>("cassoulet and (maison or canard)", fields, 100, nameof(Diary.isDeleted)));
        Add("search or", repository.Search<Diary>("dentiste or garonne", fields, 100, nameof(Diary.isDeleted)));
        Add("search all", repository.Search<Diary>("*", fields, 7, nameof(Diary.isDeleted)));
        Add("search empty", repository.Search<Diary>("", fields, 3));
        Add("search deleted kept", repository.Search<Diary>("cassoulet", fields, 100));
        Add("search size", repository.Search<Diary>("cassoulet", fields, 2));

        // The search on one field (ontology, cache, message flows) and the old fuzzy search on the comment.
        Add("field search", repository.Search<Diary>("recete", "text", 10));
        Add("field search default size", repository.Search<Diary>("le", "text"));
        Add("comment fuzzy", repository.Search<Diary>("entri"));

        // The last ones and the ones since a date.
        Add("last", repository.Last<Diary>(5));
        Add("last not deleted", repository.Last<Diary>(5, nameof(Diary.isDeleted)));
        Add("since", repository.Since<Diary>(Start.AddHours(18)));
        Add("since all", repository.SinceAll<Diary>(Start.AddHours(20)));

        // Delete (a diary is never erased: marked deleted, it stays in its index), and the searches after.
        lines.Add($"delete: {repository.Delete<Diary>("diary-00")}");
        lines.Add($"delete again: {repository.Delete<Diary>("diary-00")}");
        lines.Add($"load deleted: {(repository.Load<Diary>("diary-00") is Diary deleted ? "isDeleted " + deleted.isDeleted : "null")}");
        diaries[1].text = "Texte modifié : choucroute";
        repository.SaveSearchable(diaries[1]);
        Add("search after update", repository.Search<Diary>("choucroute", fields, 10));
        Add("search after delete", repository.Search<Diary>("castelnaudary", fields, 10, nameof(Diary.isDeleted)));
        Add("search after delete kept", repository.Search<Diary>("castelnaudary", fields, 10));

        // A type never saved: the searches give nothing (no index yet).
        Add("no index last", repository.Last<Idea>(5));
        Add("no index search", repository.Search<Idea>("x", ["ideaTitle"], 5));
        Add("no index since", repository.Since<Idea>(Start));
        lines.Add($"no index load: {repository.Load<Idea>("x") ?? "null"}");
        return lines;
    }
}
