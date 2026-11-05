namespace KAONOMI.ImmuneSystem.Images;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

/// <summary>
/// The metadata of an image, read without its pixels: the APPn segments of a JPEG (EXIF, XMP, C2PA in APP11), the
/// chunks of a PNG (tEXt, zTXt, iTXt, eXIf, caBX), the chunks of a WebP, the boxes of a HEIC / AVIF / JPEG XL, and the
/// beginning and the end of the other formats.
/// </summary>
public sealed class ImageMetadata
{
    private const int MaxBlock = 8 << 20;

    // Byte arrays, not u8 strings: in a C# string "\x89" is U+0089, two bytes in UTF-8.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JxlSignature = [0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private readonly MemoryStream _raw = new();

    /// <summary>The text chunks of a PNG (keyword → text), decompressed.</summary>
    public Dictionary<string, string> PngText { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a C2PA manifest (JUMBF box labelled c2pa) was found.</summary>
    public bool HasC2pa { get; private set; }

    /// <summary>The format read from the first bytes (jpeg, png, webp, heic, avif, gif, tiff, jxl, bmp), or the extension.</summary>
    public string Format { get; private set; } = "";

    /// <summary>
    /// All the metadata as one text, the bytes read one per character and the zero bytes removed (UTF-16 text such as
    /// the EXIF UserComment becomes readable like UTF-8 and ASCII text).
    /// </summary>
    public string Text { get; private set; } = "";

    public static ImageMetadata Read(string path)
    {
        var m = new ImageMetadata();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        Span<byte> head = stackalloc byte[16];
        var n = fs.ReadAtLeast(head, 16, throwOnEndOfStream: false);
        fs.Position = 0;
        head = head[..n];

        if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8)
            m.Jpeg(fs);
        else if (n >= 8 && head[..8].SequenceEqual(PngSignature))
            m.Png(fs);
        else if (n >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            m.WebP(fs);
        else if (n >= 12 && head[4..8].SequenceEqual("ftyp"u8))
            m.IsoBoxes(fs, Encoding.ASCII.GetString(head[8..12]));
        else if (n >= 12 && head[..12].SequenceEqual(JxlSignature))
            m.IsoBoxes(fs, "jxl");
        else
            m.HeadAndTail(fs, head);

        m.Finish();
        return m;
    }

    private void Finish()
    {
        var bytes = _raw.GetBuffer().AsSpan(0, (int)_raw.Length);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            if (b != 0)
                sb.Append((char)b);
        foreach (var (k, v) in PngText)
            sb.Append('\n').Append(k).Append('\n').Append(v);
        Text = sb.ToString();
        if (Text.Contains("c2pa", StringComparison.Ordinal) && (Text.Contains("jumb", StringComparison.Ordinal) || Text.Contains("c2pa.claim", StringComparison.Ordinal)))
            HasC2pa = true;
    }

    private void Add(ReadOnlySpan<byte> data) => _raw.Write(data.Length > MaxBlock ? data[..MaxBlock] : data);

    private static byte[]? ReadBlock(Stream s, long length)
    {
        if (length < 0 || length > MaxBlock || s.Position + length > s.Length)
            return null;
        var buffer = new byte[length];
        s.ReadExactly(buffer);
        return buffer;
    }

    private void Jpeg(Stream s)
    {
        Format = "jpeg";
        s.Position = 2;
        Span<byte> two = stackalloc byte[2];
        while (s.Position < s.Length - 4)
        {
            if (s.ReadByte() != 0xFF)
                break;
            var marker = s.ReadByte();
            while (marker == 0xFF)
                marker = s.ReadByte();
            if (marker is < 0 or 0xD9 or 0xDA)
                break; // End of image, or start of scan: the metadata are before.
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
                continue;
            s.ReadExactly(two);
            var length = BinaryPrimitives.ReadUInt16BigEndian(two) - 2;
            if (marker is >= 0xE0 and <= 0xEF or 0xFE)
            {
                var block = ReadBlock(s, length);
                if (block is null)
                    break;
                Add(block);
                Add("\n"u8);
            }
            else
            {
                s.Position += length;
            }
        }
    }

    private void Png(Stream s)
    {
        Format = "png";
        s.Position = 8;
        Span<byte> header = stackalloc byte[8];
        while (s.Position + 12 <= s.Length)
        {
            s.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..]);
            if (type is "IEND")
                break;
            if (type is not ("tEXt" or "zTXt" or "iTXt" or "eXIf" or "caBX") || length > MaxBlock)
            {
                s.Position += length + 4L;
                continue;
            }
            var data = ReadBlock(s, length);
            if (data is null)
                break;
            s.Position += 4; // CRC
            switch (type)
            {
                case "tEXt":
                    TextChunk(data, compressed: false, utf8: false);
                    break;
                case "zTXt":
                    TextChunk(data, compressed: true, utf8: false);
                    break;
                case "iTXt":
                    InternationalText(data);
                    break;
                default:
                    Add(data);
                    Add("\n"u8);
                    break;
            }
        }
    }

    private void TextChunk(byte[] data, bool compressed, bool utf8)
    {
        var zero = Array.IndexOf(data, (byte)0);
        if (zero <= 0)
            return;
        var key = Encoding.Latin1.GetString(data, 0, zero);
        var start = zero + 1 + (compressed ? 1 : 0);
        if (start > data.Length)
            return;
        var body = data.AsSpan(start);
        var bytes = compressed ? Inflate(body) : body.ToArray();
        if (bytes is null)
            return;
        PngText[key] = (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(bytes);
    }

    private void InternationalText(byte[] data)
    {
        // keyword \0 compressed-flag method language \0 translated-keyword \0 text
        var zero = Array.IndexOf(data, (byte)0);
        if (zero <= 0 || zero + 3 > data.Length)
            return;
        var key = Encoding.Latin1.GetString(data, 0, zero);
        var compressed = data[zero + 1] == 1;
        var lang = Array.IndexOf(data, (byte)0, zero + 3);
        var translated = lang < 0 ? -1 : Array.IndexOf(data, (byte)0, lang + 1);
        if (translated < 0)
            return;
        var body = data.AsSpan(translated + 1);
        var bytes = compressed ? Inflate(body) : body.ToArray();
        if (bytes is not null)
            PngText[key] = Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? Inflate(ReadOnlySpan<byte> data)
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer)) > 0 && output.Length < MaxBlock)
                output.Write(buffer, 0, read);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private void WebP(Stream s)
    {
        Format = "webp";
        s.Position = 12;
        Span<byte> header = stackalloc byte[8];
        while (s.Position + 8 <= s.Length)
        {
            s.ReadExactly(header);
            var type = Encoding.ASCII.GetString(header[..4]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var padded = length + (length & 1);
            if (type is "VP8 " or "VP8L" or "ALPH" or "ANMF" or "ANIM" or "VP8X" or "ICCP")
            {
                s.Position += padded;
                continue;
            }
            var data = ReadBlock(s, length);
            if (data is null)
                break;
            Add(data);
            Add("\n"u8);
            s.Position += padded - length;
        }
    }

    /// <summary>HEIC, AVIF, JPEG XL: every top-level box but the image data (mdat, jxlc, jxlp).</summary>
    private void IsoBoxes(Stream s, string brand)
    {
        Format = brand switch
        {
            "avif" or "avis" => "avif",
            "jxl" or "jxl " => "jxl",
            _ => "heic",
        };
        s.Position = 0;
        Span<byte> header = stackalloc byte[16];
        while (s.Position + 8 <= s.Length)
        {
            var start = s.Position;
            s.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..8]);
            var headerSize = 8;
            if (size == 1)
            {
                s.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = s.Length - start;
            }
            if (size < headerSize)
                break;
            var bodyLength = size - headerSize;
            if (type is "mdat" or "jxlc" or "jxlp" or "idat" || bodyLength > MaxBlock)
            {
                s.Position = start + size;
                continue;
            }
            var data = ReadBlock(s, bodyLength);
            if (data is null)
                break;
            Add(data);
            Add("\n"u8);
        }
    }

    private void HeadAndTail(Stream s, ReadOnlySpan<byte> head)
    {
        Format = head switch
        {
            [(byte)'G', (byte)'I', (byte)'F', ..] => "gif",
            [(byte)'I', (byte)'I', 42, 0, ..] or [(byte)'M', (byte)'M', 0, 42, ..] => "tiff",
            [(byte)'B', (byte)'M', ..] => "bmp",
            [0xFF, 0x0A, ..] => "jxl",
            _ => "unknown",
        };
        if (Format == "bmp")
            return;
        const int Head = 2 << 20, Tail = 512 << 10;
        var first = ReadBlock(s, Math.Min(Head, s.Length));
        if (first is not null)
            Add(first);
        if (s.Length > Head)
        {
            s.Position = Math.Max(Head, s.Length - Tail);
            var last = ReadBlock(s, s.Length - s.Position);
            if (last is not null)
                Add(last);
        }
    }
}
