using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>What YouTube says about a video: its title, description and channel (null when unknown).</summary>
public sealed record YoutubeVideoInfo(string VideoId, string? Title, string? Description, string? Author);

/// <summary>
/// Addresses of YouTube videos (id of a link, watch link, thumbnail) and their title and description, read without an
/// API key: the title and channel from the oEmbed service, the description from the page of the video
/// (<c>shortDescription</c>, or its <c>description</c> meta tag, shorter, when the page changes).
/// </summary>
public static partial class YoutubeInfo
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        // Without consent cookies, Europe gets the consent page instead of the video page.
        handler.CookieContainer.Add(new Cookie("SOCS", "CAI", "/", ".youtube.com"));
        handler.CookieContainer.Add(new Cookie("CONSENT", "YES+cb", "/", ".youtube.com"));
        var client = new HttpClient(HttpHandlers.OffUiThread(handler)) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en;q=0.8");
        return client;
    }

    /// <summary>Id of a video: the 11 characters after <c>v=</c>, <c>youtu.be/</c>, <c>shorts/</c>, <c>embed/</c> or <c>live/</c>, or the id itself.</summary>
    [GeneratedRegex(@"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/|/v/)(?<id>[A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])|^(?<id>[A-Za-z0-9_-]{11})$")]
    private static partial Regex VideoIdPattern();

    [GeneratedRegex(@"""shortDescription"":""(?<text>(?:\\.|[^""\\])*)""")]
    private static partial Regex ShortDescriptionPattern();

    [GeneratedRegex(@"<meta name=""description"" content=""(?<text>[^""]*)""")]
    private static partial Regex MetaDescriptionPattern();

    [GeneratedRegex(@"<meta name=""title"" content=""(?<text>[^""]*)""")]
    private static partial Regex MetaTitlePattern();

    /// <summary>The id of the video of <paramref name="url"/> (null when it is not a YouTube video address or id).</summary>
    public static string? VideoId(string? url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return null;
        var match = VideoIdPattern().Match(text);
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>The watch address of a video.</summary>
    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>The medium thumbnail of a video (320 × 180).</summary>
    public static string ThumbnailUrl(string videoId) => $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    /// <summary>The bytes of the thumbnail of a video (null when it cannot be downloaded).</summary>
    public static async Task<byte[]?> ThumbnailAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            return await Http.GetByteArrayAsync(ThumbnailUrl(videoId), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the title, description and channel of the video <paramref name="videoId"/>; throws
    /// <see cref="HttpRequestException"/> when YouTube cannot be reached or the video does not exist.
    /// </summary>
    public static async Task<YoutubeVideoInfo> FetchAsync(string videoId, CancellationToken cancellationToken)
    {
        string? title = null, author = null, description = null;

        using (var response = await Http.GetAsync($"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(WatchUrl(videoId))}", cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                var oembed = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                title = oembed.TryGetProperty("title", out var t) ? t.GetString() : null;
                author = oembed.TryGetProperty("author_name", out var a) ? a.GetString() : null;
            }
            else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                throw new HttpRequestException("This video does not exist on YouTube", null, response.StatusCode);
            }
        }

        try
        {
            var page = await Http.GetStringAsync(WatchUrl(videoId), cancellationToken);
            if (ShortDescriptionPattern().Match(page) is { Success: true } full)
                description = JsonSerializer.Deserialize<string>("\"" + full.Groups["text"].Value + "\"");
            else if (MetaDescriptionPattern().Match(page) is { Success: true } meta)
                description = WebUtility.HtmlDecode(meta.Groups["text"].Value);
            if (title is null && MetaTitlePattern().Match(page) is { Success: true } metaTitle)
                title = WebUtility.HtmlDecode(metaTitle.Groups["text"].Value);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The title alone is still useful.
            if (title is null)
                throw new HttpRequestException($"YouTube could not be read: {ex.Message}", ex);
        }

        return new YoutubeVideoInfo(videoId, title, description, author);
    }
}
