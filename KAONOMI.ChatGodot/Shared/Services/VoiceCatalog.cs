using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>A Piper voice of the catalog (rhasspy/piper-voices on Hugging Face).</summary>
/// <param name="Key">Name of the voice, also the name of its files (<c>fr_FR-siwis-medium</c>).</param>
/// <param name="LanguageCode">Language and region (<c>fr_FR</c>).</param>
/// <param name="LanguageName">Language in English, with its country (<c>French (France)</c>).</param>
/// <param name="Quality">x_low, low, medium or high.</param>
/// <param name="Speakers">Number of speakers of the model.</param>
/// <param name="ModelPath">Path of the .onnx file in the repository (its .onnx.json is next to it).</param>
/// <param name="SizeBytes">Size of the .onnx file.</param>
public sealed record PiperVoice(string Key, string LanguageCode, string LanguageName, string Quality, int Speakers, string ModelPath, long SizeBytes)
{
    /// <summary>Size for display (<c>63 MB</c>).</summary>
    public string Size => $"{SizeBytes / 1_000_000.0:0} MB";
}

/// <summary>
/// The Piper voices that can be downloaded (the catalog <c>voices.json</c> of rhasspy/piper-voices, kept as
/// <c>catalog.json</c> in the voices folder for the next times and offline use) and the ones installed in the voices
/// folder (<c>tts.voicesDirectory</c>): a voice is its <c>.onnx</c> model and its <c>.onnx.json</c> configuration.
/// </summary>
public static class VoiceCatalog
{
    private const string Repository = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
    private const string CatalogFile = "catalog.json";

    private static readonly HttpClient Http = new(HttpHandlers.Create()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Names of the voices installed in <paramref name="directory"/> (the .onnx files with their .onnx.json).</summary>
    public static List<string> Installed(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, "*.onnx")
            .Where(path => File.Exists(path + ".json"))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The catalog of the voices: downloaded from Hugging Face (and kept in <paramref name="directory"/>), or the copy
    /// kept there when the download fails. Sorted by language, then name.
    /// </summary>
    public static async Task<List<PiperVoice>> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var cached = Path.Combine(directory, CatalogFile);
        string json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            json = await Http.GetStringAsync(Repository + "voices.json", timeout.Token);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cached, json, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested && File.Exists(cached))
        {
            ActivityLog.Error("Voices", "Voice catalog not downloaded: the copy kept in the voices folder is used", ex);
            json = await File.ReadAllTextAsync(cached, cancellationToken);
        }

        var voices = new List<PiperVoice>();
        using var document = JsonDocument.Parse(json);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var voice = entry.Value;
            if (!voice.TryGetProperty("files", out var files))
                continue;
            var model = files.EnumerateObject().FirstOrDefault(f => f.Name.EndsWith(".onnx", StringComparison.Ordinal));
            if (model.Value.ValueKind != JsonValueKind.Object)
                continue;

            var language = voice.GetProperty("language");
            voices.Add(new PiperVoice(
                entry.Name,
                language.GetProperty("code").GetString() ?? "",
                $"{language.GetProperty("name_english").GetString()} ({language.GetProperty("country_english").GetString()})",
                voice.GetProperty("quality").GetString() ?? "",
                voice.TryGetProperty("num_speakers", out var speakers) ? speakers.GetInt32() : 1,
                model.Name,
                model.Value.TryGetProperty("size_bytes", out var size) ? size.GetInt64() : 0));
        }
        return voices.OrderBy(v => v.LanguageCode, StringComparer.Ordinal).ThenBy(v => v.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Downloads the model and the configuration of <paramref name="voice"/> into <paramref name="directory"/> (written
    /// as <c>.part</c> files, renamed once complete); <paramref name="progress"/> gets the share done, from 0 to 1.
    /// </summary>
    public static async Task DownloadAsync(PiperVoice voice, string directory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, voice.Key + ".onnx");
        await DownloadFileAsync(voice.ModelPath + ".json", model + ".json", null, cancellationToken);
        await DownloadFileAsync(voice.ModelPath, model, progress, cancellationToken);
    }

    private static async Task DownloadFileAsync(string path, string target, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var part = target + ".part";
        try
        {
            using var response = await Http.GetAsync(Repository + path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = File.Create(part))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    if (total > 0)
                        progress?.Report((double)done / total);
                }
            }
            File.Move(part, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    /// <summary>Deletes the model and the configuration of the voice <paramref name="key"/> from <paramref name="directory"/>.</summary>
    public static void Delete(string directory, string key)
    {
        var model = Path.Combine(directory, key + ".onnx");
        File.Delete(model);
        File.Delete(model + ".json");
    }

    /// <summary>Language code (<c>fr_FR</c>) of an installed voice, read from its name (<c>fr_FR-siwis-medium</c>).</summary>
    public static string LanguageOf(string key) => key.Split('-')[0];

    /// <summary>A sentence to try a voice in its language (English for the languages without one here).</summary>
    public static string Sample(string languageCode) => languageCode.Split('_')[0] switch
    {
        "fr" => "Bonjour, je suis la nouvelle voix de Kaonomi. Comment puis-je vous aider aujourd'hui ?",
        "de" => "Hallo, ich bin die neue Stimme von Kaonomi. Wie kann ich Ihnen heute helfen?",
        "es" => "Hola, soy la nueva voz de Kaonomi. ¿Cómo puedo ayudarte hoy?",
        "it" => "Ciao, sono la nuova voce di Kaonomi. Come posso aiutarti oggi?",
        "pt" => "Olá, eu sou a nova voz do Kaonomi. Como posso ajudar hoje?",
        "nl" => "Hallo, ik ben de nieuwe stem van Kaonomi. Hoe kan ik je vandaag helpen?",
        _ => "Hello, I am the new voice of Kaonomi. How can I help you today?"
    };
}
