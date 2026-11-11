using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>A note on a moment of a recording.</summary>
public sealed class RecorderAnnotation
{
    /// <summary>Where in the recording, in seconds from its start.</summary>
    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string TimeLabel => RecorderInfo.Clock(Time);
}

/// <summary>A manipulation of a recording: what was done, the annotation the user gave for it, when and by whom.</summary>
public sealed class RecorderAction
{
    /// <summary>recorded, cut, trim, restore, name-selection, save-sample.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("annotation")]
    public string Annotation { get; set; } = string.Empty;

    /// <summary>What it was done on, as a sentence.</summary>
    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("user")]
    public string User { get; set; } = Environment.UserName;
}

/// <summary>A named part of a recording.</summary>
public sealed class RecorderSelection
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("user")]
    public string User { get; set; } = Environment.UserName;
}

/// <summary>The filters a sample was written with.</summary>
public sealed class RecorderFilters
{
    [JsonPropertyName("gainDb")]
    public double GainDb { get; set; }

    [JsonPropertyName("compressor")]
    public bool Compressor { get; set; }

    [JsonPropertyName("thresholdDb")]
    public double ThresholdDb { get; set; }

    [JsonPropertyName("ratio")]
    public double Ratio { get; set; }

    [JsonPropertyName("lowHz")]
    public double LowHz { get; set; }

    [JsonPropertyName("highHz")]
    public double HighHz { get; set; }

    [JsonPropertyName("invertBand")]
    public bool InvertBand { get; set; }

    [JsonPropertyName("equalizerDb")]
    public List<double> EqualizerDb { get; set; } = [];

    [JsonPropertyName("equalizerHz")]
    public List<double> EqualizerHz { get; set; } = [];
}

/// <summary>A sample saved from a recording: a part of it, with the filters applied.</summary>
public sealed class RecorderSample
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("seconds")]
    public double Seconds { get; set; }

    [JsonPropertyName("filters")]
    public RecorderFilters Filters { get; set; } = new();

    [JsonPropertyName("annotation")]
    public string Annotation { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("user")]
    public string User { get; set; } = Environment.UserName;
}

/// <summary>
/// A recording of /recorder and everything done to it: what it is, the notes on its moments, each manipulation with
/// its annotation, its named selections, and the samples saved from it. It is kept twice, the same: beside the
/// recording (<c>name.json</c> next to <c>name.wav</c>) and in the search engine of the server (<c>AudioRecord</c>,
/// index <c>audiorecord</c>, <c>POST /audiorecord/update</c>), written again at each change.
/// </summary>
public sealed class RecorderInfo
{
    /// <summary>The id of its document in the search engine, given when it is recorded.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "KAONOMI.Chat";

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = Environment.UserName;

    [JsonPropertyName("machineName")]
    public string MachineName { get; set; } = Environment.MachineName;

    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>The full path of the WAV file on this machine.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("seconds")]
    public double Seconds { get; set; }

    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("annotations")]
    public List<RecorderAnnotation> Annotations { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<RecorderAction> Actions { get; set; } = [];

    [JsonPropertyName("selections")]
    public List<RecorderSelection> Selections { get; set; } = [];

    [JsonPropertyName("samples")]
    public List<RecorderSample> Samples { get; set; } = [];

    /// <summary>A time as <c>mm:ss.d</c> (hours before when there are).</summary>
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}" : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }
}
