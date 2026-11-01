using System.Text.Encodings.Web;
using System.Text.Json;

namespace KAONOMI.SearchEngine.Http;

/// <summary>The JSON of the answers: the text as it is (UTF-8, not escaped to <c>é</c>), as Elasticsearch writes it.</summary>
public static class EsJson
{
    public static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
