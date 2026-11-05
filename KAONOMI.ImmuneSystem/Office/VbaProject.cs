namespace KAONOMI.ImmuneSystem.Office;

using System.Buffers.Binary;
using System.Text;

/// <summary>A module of a VBA project: its name, its kind (standard module, class, document) and its source.</summary>
public sealed record VbaModule(string Name, string Kind, string Code);

/// <summary>
/// Reads the source of the modules of a VBA project ([MS-OVBA]): the <c>dir</c> stream lists the modules, the stream of a
/// module holds its compiled p-code then, from <c>MODULEOFFSET</c>, its compressed source.
/// </summary>
public static class VbaProject
{
    static VbaProject() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The storages named VBA that hold a <c>dir</c> stream.</summary>
    public static IEnumerable<CompoundFile.Entry> Find(CompoundFile cf) =>
        cf.All().Where(e => e.IsStorage && e.Name.Equals("VBA", StringComparison.OrdinalIgnoreCase) && CompoundFile.Child(e, "dir") is not null);

    public static List<VbaModule> Modules(CompoundFile cf, CompoundFile.Entry vba)
    {
        var dir = Decompress(cf.Read(CompoundFile.Child(vba, "dir")!));
        var codePage = 1252;
        var modules = new List<(string Name, string Stream, uint Offset, string Kind)>();
        string? name = null, stream = null, kind = "module";
        uint offset = 0;

        var span = dir.AsSpan();
        var pos = 0;
        while (pos + 6 <= span.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 2)..]);
            pos += 6;
            if (id == 0x0009)
            {
                size = 6; // PROJECTVERSION: its size field says 4, the record holds 6 bytes.
            }
            if (size < 0 || pos + size > span.Length)
                break;
            var data = span.Slice(pos, size);
            switch (id)
            {
                case 0x0003 when size >= 2:
                    codePage = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    break;
                case 0x0019:
                    name = Decode(data, codePage);
                    stream = null;
                    offset = 0;
                    kind = "module";
                    break;
                case 0x0047:
                    name = Encoding.Unicode.GetString(data);
                    break;
                case 0x001A:
                    stream = Decode(data, codePage);
                    break;
                case 0x0032:
                    stream = Encoding.Unicode.GetString(data);
                    break;
                case 0x0031 when size >= 4:
                    offset = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    break;
                case 0x0021:
                    kind = "module";
                    break;
                case 0x0022:
                    kind = "class or document";
                    break;
                case 0x002B:
                    if (name is not null)
                        modules.Add((name, stream ?? name, offset, kind));
                    name = null;
                    break;
            }
            pos += size;
        }

        var result = new List<VbaModule>();
        foreach (var m in modules)
        {
            var entry = CompoundFile.Child(vba, m.Stream);
            if (entry is null)
                continue;
            var bytes = cf.Read(entry);
            if (m.Offset >= bytes.Length)
                continue;
            var code = Decode(Decompress(bytes.AsSpan((int)m.Offset)), codePage);
            result.Add(new VbaModule(m.Name, m.Kind, code.Replace("\r\n", "\n").TrimEnd('\0', '\n')));
        }
        return result;
    }

    private static string Decode(ReadOnlySpan<byte> data, int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage).GetString(data);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    /// <summary>The decompression of [MS-OVBA] 2.4.1: chunks of 4096 bytes, each a sequence of literals and copy tokens.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data[0] != 1)
            throw new InvalidDataException("Not a compressed VBA container");
        var output = new List<byte>(data.Length * 3);
        var pos = 1;
        while (pos + 2 <= data.Length)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            var chunkEnd = Math.Min(data.Length, pos + (header & 0x0FFF) + 3);
            var compressed = (header & 0x8000) != 0;
            pos += 2;
            var chunkStart = output.Count;
            if (!compressed)
            {
                var n = Math.Min(4096, data.Length - pos);
                for (var i = 0; i < n; i++)
                    output.Add(data[pos + i]);
                pos += n;
                continue;
            }
            while (pos < chunkEnd)
            {
                var flags = data[pos++];
                for (var bit = 0; bit < 8 && pos < chunkEnd; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        output.Add(data[pos++]);
                        continue;
                    }
                    if (pos + 2 > chunkEnd)
                    {
                        pos = chunkEnd;
                        break;
                    }
                    var token = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                    pos += 2;
                    var decompressed = output.Count - chunkStart;
                    var bitCount = 4;
                    while ((1 << bitCount) < decompressed)
                        bitCount++;
                    bitCount = Math.Min(bitCount, 12);
                    var lengthMask = 0xFFFF >> bitCount;
                    var length = (token & lengthMask) + 3;
                    var offset = (token >> (16 - bitCount)) + 1;
                    var from = output.Count - offset;
                    if (from < chunkStart)
                        throw new InvalidDataException("Bad copy token");
                    for (var i = 0; i < length; i++)
                        output.Add(output[from + i]);
                }
            }
            pos = chunkEnd;
        }
        return output.ToArray();
    }
}
