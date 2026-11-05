namespace KAONOMI.ImmuneSystem.Office;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Reader of the Compound File Binary format ([MS-CFB]): the files of Office 97-2003 (.doc, .xls, .ppt), the VBA
/// projects of the Office 2007+ files (vbaProject.bin), the embedded OLE objects (oleObject1.bin).
/// </summary>
public sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint MaxRegular = 0xFFFFFFFA;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;

    public sealed class Entry
    {
        public required string Name { get; init; }
        public byte Type { get; init; }
        public uint Left { get; init; }
        public uint Right { get; init; }
        public uint Child { get; init; }
        public uint Start { get; init; }
        public long Size { get; init; }
        public List<Entry> Children { get; } = [];
        public string Path { get; set; } = "";
        public bool IsStorage => Type is 1 or 5;
        public bool IsStream => Type == 2;
    }

    public Entry Root { get; }
    public IReadOnlyList<Entry> Entries { get; }

    public static bool IsCompoundFile(ReadOnlySpan<byte> data, bool headerOnly = false) =>
        data.Length >= (headerOnly ? 8 : 512) && data[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

    public CompoundFile(byte[] data)
    {
        if (!IsCompoundFile(data))
            throw new InvalidDataException("Not a compound file");
        _data = data;
        var span = data.AsSpan();
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(span[0x1E..]);
        if (sectorShift is not (9 or 12))
            throw new InvalidDataException("Bad sector size");
        _sectorSize = 1 << sectorShift;
        var fatCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0x2C..]);
        var dirStart = BinaryPrimitives.ReadUInt32LittleEndian(span[0x30..]);
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(span[0x38..]);
        var miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);
        var difatStart = BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..]);
        var difatCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0x48..]);

        // The sectors of the FAT: 109 in the header, the others in the DIFAT chain.
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var s = BinaryPrimitives.ReadUInt32LittleEndian(span[(0x4C + i * 4)..]);
            if (s < MaxRegular)
                fatSectors.Add(s);
        }
        var perSector = _sectorSize / 4;
        var difat = difatStart;
        for (var n = 0; n < difatCount && difat < MaxRegular && n < 100_000; n++)
        {
            var sector = Sector(difat);
            for (var i = 0; i < perSector - 1; i++)
            {
                var s = BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]);
                if (s < MaxRegular)
                    fatSectors.Add(s);
            }
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[((perSector - 1) * 4)..]);
        }
        _fat = new uint[Math.Min(fatSectors.Count, (int)Math.Max(fatCount, 1)) * perSector];
        for (var f = 0; f * perSector < _fat.Length; f++)
        {
            var sector = Sector(fatSectors[f]);
            for (var i = 0; i < perSector; i++)
                _fat[f * perSector + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]);
        }

        var dir = Chain(dirStart, long.MaxValue);
        var entries = new List<Entry>();
        for (var off = 0; off + 128 <= dir.Length; off += 128)
        {
            var e = dir.AsSpan(off, 128);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[64..]), 2, 64);
            entries.Add(new Entry
            {
                Name = Encoding.Unicode.GetString(e[..(nameLength - 2)]),
                Type = e[66],
                Left = BinaryPrimitives.ReadUInt32LittleEndian(e[68..]),
                Right = BinaryPrimitives.ReadUInt32LittleEndian(e[72..]),
                Child = BinaryPrimitives.ReadUInt32LittleEndian(e[76..]),
                Start = BinaryPrimitives.ReadUInt32LittleEndian(e[116..]),
                Size = _sectorSize == 512 ? BinaryPrimitives.ReadUInt32LittleEndian(e[120..]) : (long)BinaryPrimitives.ReadUInt64LittleEndian(e[120..]),
            });
        }
        if (entries.Count == 0)
            throw new InvalidDataException("No directory");
        Entries = entries;
        Root = entries[0];
        _miniStream = Root.Size > 0 ? Chain(Root.Start, Root.Size) : [];
        var miniFat = Chain(miniFatStart, long.MaxValue);
        _miniFat = new uint[miniFat.Length / 4];
        for (var i = 0; i < _miniFat.Length; i++)
            _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFat.AsSpan(i * 4));

        Link(Root, new HashSet<uint> { 0 });
    }

    private void Link(Entry storage, HashSet<uint> seen)
    {
        var stack = new Stack<uint>();
        stack.Push(storage.Child);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id >= Entries.Count || !seen.Add(id))
                continue;
            var e = Entries[(int)id];
            if (e.Type == 0)
                continue;
            e.Path = storage == Root ? e.Name : $"{storage.Path}/{e.Name}";
            storage.Children.Add(e);
            stack.Push(e.Left);
            stack.Push(e.Right);
            if (e.IsStorage)
                Link(e, seen);
        }
    }

    private Span<byte> Sector(uint id)
    {
        var offset = (long)(id + 1) * _sectorSize;
        if (offset + _sectorSize > _data.Length)
        {
            // The last sector of a file may be short.
            if (offset >= _data.Length)
                throw new InvalidDataException($"Sector {id} is out of the file");
            var copy = new byte[_sectorSize];
            _data.AsSpan((int)offset).CopyTo(copy);
            return copy;
        }
        return _data.AsSpan((int)offset, _sectorSize);
    }

    private byte[] Chain(uint start, long size)
    {
        using var ms = new MemoryStream();
        var id = start;
        var guard = 0;
        while (id < MaxRegular && ms.Length < size)
        {
            if (++guard > _fat.Length + 1)
                throw new InvalidDataException("Loop in the FAT");
            ms.Write(Sector(id));
            id = id < _fat.Length ? _fat[id] : EndOfChain;
        }
        var bytes = ms.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    private byte[] MiniChain(uint start, long size)
    {
        var result = new byte[size];
        var id = start;
        var written = 0;
        var guard = 0;
        while (id < MaxRegular && written < size)
        {
            if (++guard > _miniFat.Length + 1)
                throw new InvalidDataException("Loop in the mini FAT");
            var offset = (long)id * 64;
            if (offset >= _miniStream.Length)
                break;
            var n = (int)Math.Min(Math.Min(64, size - written), _miniStream.Length - offset);
            _miniStream.AsSpan((int)offset, n).CopyTo(result.AsSpan(written));
            written += n;
            id = id < _miniFat.Length ? _miniFat[id] : EndOfChain;
        }
        return written == size ? result : result[..written];
    }

    public byte[] Read(Entry stream)
    {
        if (stream.Size <= 0)
            return [];
        if (stream.Size > int.MaxValue / 2)
            throw new InvalidDataException("Stream too large");
        return stream.Size < _miniCutoff ? MiniChain(stream.Start, stream.Size) : Chain(stream.Start, stream.Size);
    }

    public static Entry? Child(Entry storage, string name) =>
        storage.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every entry under the root, depth first.</summary>
    public IEnumerable<Entry> All()
    {
        var stack = new Stack<Entry>(Root.Children);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            foreach (var c in e.Children)
                stack.Push(c);
        }
    }
}
