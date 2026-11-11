using System;
using System.IO;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Cuts in a recording: a part taken out, or everything but a part. The file is written again without what is cut
/// (mono, 16 bits, at its rate); before its first cut, the recording as it was recorded is copied beside it
/// (<c>name.wav.bak</c>), so that <see cref="Restore"/> brings it back whatever was cut since.
/// </summary>
public static class WavEditor
{
    /// <summary>The copy of the recording as it was before its first cut.</summary>
    public static string BackupOf(string path) => path + ".bak";

    public static bool HasBackup(string path) => File.Exists(BackupOf(path));

    /// <summary>
    /// Writes <paramref name="path"/> again with only the samples <paramref name="keep"/> says to keep (given the
    /// time of each block of samples, in seconds: its start and its end). Returns the new length, in seconds.
    /// </summary>
    private static double Rewrite(string path, Func<double, bool> keep)
    {
        var temporary = path + ".tmp";
        long written = 0;
        int rate;
        using (var reader = new WavReader(path))
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            rate = reader.Rate;
            Wav.WriteHeader(file, rate, 0);
            var block = new float[8192];
            var bytes = new byte[block.Length * 2];
            long position = 0;
            int read;
            while ((read = reader.Read(block)) > 0)
            {
                var count = 0;
                for (var i = 0; i < read; i++)
                {
                    if (!keep((position + i) / (double)rate))
                        continue;
                    var value = (short)Math.Round(Math.Clamp(block[i], -1f, 1f) * 32767f);
                    bytes[2 * count] = (byte)value;
                    bytes[2 * count + 1] = (byte)(value >> 8);
                    count++;
                }
                file.Write(bytes, 0, count * 2);
                written += count;
                position += read;
            }
            Wav.WriteHeader(file, rate, written);
            file.Flush(true);
        }
        // The recording as it was recorded is kept once, before its first cut.
        if (!HasBackup(path))
            File.Copy(path, BackupOf(path));
        File.Move(temporary, path, overwrite: true);
        return rate > 0 ? written / (double)rate : 0;
    }

    /// <summary>Takes what is between <paramref name="start"/> and <paramref name="end"/> (seconds) out of the recording; returns its new length.</summary>
    public static double Cut(string path, double start, double end) => Rewrite(path, time => time < start || time >= end);

    /// <summary>Keeps only what is between <paramref name="start"/> and <paramref name="end"/> (seconds); returns the new length.</summary>
    public static double Trim(string path, double start, double end) => Rewrite(path, time => time >= start && time < end);

    /// <summary>Brings the recording back as it was before its first cut (false when it was never cut); the copy is then removed.</summary>
    public static bool Restore(string path)
    {
        if (!HasBackup(path))
            return false;
        File.Move(BackupOf(path), path, overwrite: true);
        return true;
    }
}
