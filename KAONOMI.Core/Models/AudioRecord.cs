using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An audio recording of the recorder of the chat (/recorder) and everything done to it: what it is (its project,
    /// file, length, source), the notes on its moments, each manipulation with its annotation (who, when, what), the
    /// parts of it that were named, and the samples saved from it with the filters applied to them. One document per
    /// recording (index <c>audiorecord</c>), its id given by the chat and kept beside the file; the chat writes it again
    /// at each change.
    /// </summary>
    public class AudioRecord : EntityBase, ISoftDeletable
    {
        /// <summary>The project: the folder of <c>~/kaonomi/audio</c> the recording is in.</summary>
        public string project { get; set; } = string.Empty;

        /// <summary>The name of the WAV file, and its full path on the machine that recorded it.</summary>
        public string file { get; set; } = string.Empty;

        public string path { get; set; } = string.Empty;

        /// <summary>Its length now (after the cuts), in seconds, and its sample rate, in Hz.</summary>
        public double seconds { get; set; }

        public int rate { get; set; }

        /// <summary>The microphone it was recorded from.</summary>
        public string source { get; set; } = string.Empty;

        /// <summary>The notes on its moments.</summary>
        public List<AudioRecordNote> annotations { get; set; } = new List<AudioRecordNote>();

        /// <summary>What was done to it, the oldest first: each manipulation with the annotation given for it.</summary>
        public List<AudioRecordAction> actions { get; set; } = new List<AudioRecordAction>();

        /// <summary>The parts of it that were named.</summary>
        public List<AudioRecordSelection> selections { get; set; } = new List<AudioRecordSelection>();

        /// <summary>The samples saved from it (a part of it, with filters applied).</summary>
        public List<AudioRecordSample> samples { get; set; } = new List<AudioRecordSample>();

        /// <summary>Soft-delete flag.</summary>
        public bool isDeleted { get; set; } = false;
    }

    /// <summary>A note on a moment of a recording.</summary>
    public class AudioRecordNote
    {
        /// <summary>Where in the recording, in seconds from its start.</summary>
        public double time { get; set; }

        public string text { get; set; } = string.Empty;

        public DateTime created { get; set; }
    }

    /// <summary>A manipulation of a recording: what was done, the annotation given for it, when and by whom.</summary>
    public class AudioRecordAction
    {
        /// <summary>recorded, cut, trim, restore, name-selection, save-sample…</summary>
        public string action { get; set; } = string.Empty;

        /// <summary>What the user wrote in the popup of the manipulation.</summary>
        public string annotation { get; set; } = string.Empty;

        /// <summary>What it was done on (the part cut, the sample written…), as a sentence.</summary>
        public string details { get; set; } = string.Empty;

        public DateTime date { get; set; }

        public string user { get; set; } = string.Empty;
    }

    /// <summary>A named part of a recording.</summary>
    public class AudioRecordSelection
    {
        public string name { get; set; } = string.Empty;

        /// <summary>Its start and its end in the recording, in seconds.</summary>
        public double start { get; set; }

        public double end { get; set; }

        public DateTime date { get; set; }

        public string user { get; set; } = string.Empty;
    }

    /// <summary>A sample saved from a recording: a part of it, with the filters it was written with.</summary>
    public class AudioRecordSample
    {
        public string name { get; set; } = string.Empty;

        /// <summary>The WAV file of the sample (name, and full path on the machine that wrote it).</summary>
        public string file { get; set; } = string.Empty;

        public string path { get; set; } = string.Empty;

        /// <summary>The part of the recording it was taken from, in seconds, and its own length.</summary>
        public double start { get; set; }

        public double end { get; set; }

        public double seconds { get; set; }

        /// <summary>The filters applied: gain, compressor, band, equalizer.</summary>
        public AudioRecordFilters filters { get; set; } = new AudioRecordFilters();

        /// <summary>What the user wrote when saving it.</summary>
        public string annotation { get; set; } = string.Empty;

        public DateTime date { get; set; }

        public string user { get; set; } = string.Empty;
    }

    /// <summary>The filters of a sample, as the recorder applies them.</summary>
    public class AudioRecordFilters
    {
        public double gainDb { get; set; }

        public bool compressor { get; set; }

        public double thresholdDb { get; set; }

        public double ratio { get; set; }

        /// <summary>The band kept: from <see cref="lowHz"/> to <see cref="highHz"/>; with <see cref="invertBand"/>, the band taken out.</summary>
        public double lowHz { get; set; }

        public double highHz { get; set; }

        public bool invertBand { get; set; }

        /// <summary>The equalizer: the gain (dB) of each of its bands, and the frequency of each band (Hz).</summary>
        public List<double> equalizerDb { get; set; } = new List<double>();

        public List<double> equalizerHz { get; set; } = new List<double>();
    }
}
