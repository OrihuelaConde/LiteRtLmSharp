using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Writes small <c>.litertlm</c> containers for the model sync tests, so they need no model file and no download.
/// </summary>
/// <remarks>
/// <para>The layout: <c>LITERTLM</c>, three little-endian <c>uint32</c> version numbers and four bytes of padding,
/// the <c>uint64</c> header end offset at byte 24, then a FlatBuffer <c>LiteRTLMMetaData</c> table, and the
/// sections at the offsets the metadata gives. The schema, in field order:</para>
/// <code>
/// LiteRTLMMetaData { 0: system_metadata: SystemMetadata; 1: section_metadata: SectionMetadata }
/// SystemMetadata   { 0: entries: [KeyValuePair] }
/// SectionMetadata  { 0: objects: [SectionObject] }
/// SectionObject    { 0: items: [KeyValuePair]; 1: begin_offset: ulong; 2: end_offset: ulong; 3: data_type: ubyte }
/// KeyValuePair     { 0: key: string; 1: value_type: ubyte (union tag); 2: value: table }
/// StringValue      { 0: value: string }  (union tag 9)
/// </code>
/// <para>A section's SHA-256 is the item <c>sha256</c> holding the lowercase hex digest of its bytes; the runtime
/// reuses a section and reports a file up to date only for sections that carry one. The runtime accepts these
/// containers (measured on LiteRT-LM v0.20.0, win-x64: a plan of each fixture with section hashes against itself
/// is up to date).</para>
/// </remarks>
internal static class ModelSyncFixtures
{
    /// <summary>The size of the first remote header fetch the runtime makes.</summary>
    public const int InitialHeaderFetch = 16 * 1024;

    private static readonly byte[] Magic = "LITERTLM"u8.ToArray();
    private static readonly int[] DataTypes = [5, 4, 3, 3];
    private static readonly string?[] ModelTypes = [null, null, "tf_lite_prefill_decode", "tf_lite_embedder"];

    /// <summary>The system items of every fixture. Their lengths fix the header size: 1,184 bytes for
    /// <see cref="A"/>.</summary>
    public static readonly (string Key, string Value)[] DefaultSystemItems =
        [("author", "modelsync-test"), ("uuid", "00000000-0000-4000-8000-000000000007")];

    private static readonly Lazy<byte[]> S0 = new(() => Payload(1, 1024));
    private static readonly Lazy<byte[]> S1 = new(() => Payload(2, 40 * 1024));
    private static readonly Lazy<byte[]> S2 = new(() => Payload(3, 8 * 1024));
    private static readonly Lazy<byte[]> S3 = new(() => Payload(4, 30 * 1024));
    private static readonly Lazy<byte[]> S2Changed = new(() => Payload(33, 8 * 1024));
    private static readonly Lazy<byte[]> S2Resized = new(() => Payload(34, 20 * 1024));

    /// <summary>129,024 bytes, header 1,184, four hashed sections at 16 KiB alignment: (16384, 1024),
    /// (32768, 40960), (81920, 8192), (98304, 30720).</summary>
    public static byte[] A => Build([S0.Value, S1.Value, S2.Value, S3.Value]);

    /// <summary><see cref="A"/> with section 2 changed, same size and layout.</summary>
    public static byte[] A1 => Build([S0.Value, S1.Value, S2Changed.Value, S3.Value]);

    /// <summary><see cref="A"/> with section 2 resized to 20,480 bytes: 145,408 bytes.</summary>
    public static byte[] B => Build([S0.Value, S1.Value, S2Resized.Value, S3.Value]);

    /// <summary><see cref="A"/> with sections 0, 1 and 3 changed: above the full-download threshold.</summary>
    public static byte[] M => Build([Payload(11, 1024), Payload(12, 40 * 1024), S2.Value, Payload(14, 30 * 1024)]);

    /// <summary><see cref="A"/> without section hashes (header 664).</summary>
    public static byte[] H => Build([S0.Value, S1.Value, S2.Value, S3.Value], hashes: false);

    /// <summary>A container smaller than the first header fetch: 3,716 bytes, header 912, three sections at
    /// 16-byte alignment.</summary>
    public static byte[] Small => BuildSmall(Payload(22, 2000));

    /// <summary><see cref="Small"/> with section 1 changed.</summary>
    public static byte[] Small1 => BuildSmall(Payload(24, 2000));

    /// <summary><see cref="A"/> with a header longer than the first header fetch (a 20,000-character system
    /// item): 145,408 bytes, header 21,152.</summary>
    public static byte[] LongHeader => Build([S0.Value, S1.Value, S2.Value, S3.Value], systemItems: LongHeaderItems);

    /// <summary><see cref="LongHeader"/> with section 2 changed.</summary>
    public static byte[] LongHeader1 => Build([S0.Value, S1.Value, S2Changed.Value, S3.Value], systemItems: LongHeaderItems);

    private static readonly (string, string)[] LongHeaderItems = [("author", "modelsync-test"), ("padding", new string('p', 20000))];

    /// <summary>The header end offset a container declares at byte 24.</summary>
    public static long HeaderEnd(byte[] container) => (long)BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(24, 8));

    /// <summary>The bytes a first download from a source that serves ranges receives: the first header fetch,
    /// the rest of a longer header, then <c>[header end, size)</c> (measured on LiteRT-LM v0.20.0, win-x64).</summary>
    public static long FirstDownloadBytes(byte[] container)
    {
        long size = container.Length, headerEnd = HeaderEnd(container);
        long header = Math.Max(Math.Min(InitialHeaderFetch, size), headerEnd);
        return header + (size - headerEnd);
    }

    /// <summary>Deterministic pseudo-random bytes.</summary>
    public static byte[] Payload(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] BuildSmall(byte[] middle) =>
        Build([Payload(21, 300), middle, Payload(23, 500)], align: 16, dataTypes: [5, 4, 3],
            modelTypes: [null, null, "tf_lite_prefill_decode"]);

    /// <summary>Builds a container. Each section gets the data type and model type of its index, and a
    /// <c>sha256</c> item when <paramref name="hashes"/> is <c>true</c>; <paramref name="hashOverride"/> replaces
    /// the digest of the given sections.</summary>
    public static byte[] Build(
        byte[][] payloads, bool hashes = true, int align = 16384, int[]? dataTypes = null, string?[]? modelTypes = null,
        (string Key, string Value)[]? systemItems = null, IReadOnlyDictionary<int, string>? hashOverride = null)
    {
        dataTypes ??= DataTypes;
        modelTypes ??= ModelTypes;
        systemItems ??= DefaultSystemItems;

        byte[] MetadataFor((long Begin, long End)[] offsets)
        {
            var sections = new List<Section>();
            for (int i = 0; i < payloads.Length; i++)
            {
                var items = new List<(string, string)>();
                if (modelTypes[i] is { } modelType)
                    items.Add(("model_type", modelType));
                if (hashes)
                    items.Add(("sha256", hashOverride is not null && hashOverride.TryGetValue(i, out string? h)
                        ? h : Convert.ToHexStringLower(SHA256.HashData(payloads[i]))));
                sections.Add(new Section(offsets[i].Begin, offsets[i].End, (byte)dataTypes[i], items));
            }
            return BuildMetadata(systemItems, sections);
        }

        // The offsets have a fixed width in the FlatBuffer, so the header size does not depend on their values.
        int headerLength = 32 + MetadataFor(new (long, long)[payloads.Length]).Length;
        var placed = new (long Begin, long End)[payloads.Length];
        long position = headerLength;
        for (int i = 0; i < payloads.Length; i++)
        {
            if (align > 1)
                position = (position + align - 1) / align * align;
            placed[i] = (position, position + payloads[i].Length);
            position += payloads[i].Length;
        }
        byte[] metadata = MetadataFor(placed);
        if (32 + metadata.Length != headerLength)
            throw new InvalidOperationException("The header size changed with the section offsets.");

        byte[] container = new byte[position];
        Magic.CopyTo(container, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(12), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan(24), (ulong)headerLength);
        metadata.CopyTo(container, 32);
        for (int i = 0; i < payloads.Length; i++)
            payloads[i].CopyTo(container, placed[i].Begin);
        return container;
    }

    private sealed record Section(long Begin, long End, byte DataType, List<(string Key, string Value)> Items);

    private static byte[] BuildMetadata((string Key, string Value)[] systemItems, List<Section> sections)
    {
        var b = new FlatBufferBuilder();
        var sectionObjects = new List<int>();
        foreach (Section s in sections)
        {
            var kvs = s.Items.Select(item => KeyValueString(b, item.Key, item.Value)).ToList();
            int items = b.VectorOfOffsets(kvs);
            b.Start(4);
            b.FieldUInt64(1, (ulong)s.Begin);
            b.FieldUInt64(2, (ulong)s.End);
            b.FieldOffset(0, items);
            b.FieldByte(3, s.DataType);
            sectionObjects.Add(b.End());
        }
        int objects = b.VectorOfOffsets(sectionObjects);
        b.Start(1);
        b.FieldOffset(0, objects);
        int sectionMetadata = b.End();
        var entries = systemItems.Select(item => KeyValueString(b, item.Key, item.Value)).ToList();
        int entryVector = b.VectorOfOffsets(entries);
        b.Start(1);
        b.FieldOffset(0, entryVector);
        int systemMetadata = b.End();
        b.Start(2);
        b.FieldOffset(0, systemMetadata);
        b.FieldOffset(1, sectionMetadata);
        int root = b.End();
        return b.Finish(root);
    }

    private static int KeyValueString(FlatBufferBuilder b, string key, string value)
    {
        int k = b.String(key);
        int v = b.String(value);
        b.Start(1);
        b.FieldOffset(0, v);
        int stringValue = b.End();
        b.Start(3);
        b.FieldOffset(0, k);
        b.FieldByte(1, 9);
        b.FieldOffset(2, stringValue);
        return b.End();
    }

    /// <summary>A back-to-front FlatBuffer builder, reduced to what the container schema needs. Offsets count
    /// from the end of the buffer, as in the reference builder.</summary>
    private sealed class FlatBufferBuilder
    {
        private byte[] _buffer = new byte[1024];
        private int _head = 1024;
        private int _minAlign = 1;
        private int[]? _vtable;
        private int _objectEnd;

        private int Offset => _buffer.Length - _head;

        private void Prepend(ReadOnlySpan<byte> bytes)
        {
            while (_head < bytes.Length)
            {
                byte[] grown = new byte[_buffer.Length * 2];
                int used = Offset;
                _buffer.AsSpan(_head).CopyTo(grown.AsSpan(grown.Length - used));
                _head = grown.Length - used;
                _buffer = grown;
            }
            _head -= bytes.Length;
            bytes.CopyTo(_buffer.AsSpan(_head));
        }

        private void Pad(int count)
        {
            if (count > 0)
                Prepend(new byte[count]);
        }

        private void Prep(int size, int additional)
        {
            _minAlign = Math.Max(_minAlign, size);
            int remainder = (Offset + additional) % size;
            Pad(remainder == 0 ? 0 : size - remainder);
        }

        private void PutUInt16(int value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
            Prepend(b);
        }

        private void PutUInt32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            Prepend(b);
        }

        private void AddUOffset(int target)
        {
            Prep(4, 0);
            PutUInt32((uint)(Offset - target + 4));
        }

        public int String(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            Prep(4, data.Length + 1);
            Prepend([.. data, 0]);
            Prep(4, 0);
            PutUInt32((uint)data.Length);
            return Offset;
        }

        public int VectorOfOffsets(List<int> offsets)
        {
            Prep(4, 4 * offsets.Count);
            for (int i = offsets.Count - 1; i >= 0; i--)
                AddUOffset(offsets[i]);
            Prep(4, 0);
            PutUInt32((uint)offsets.Count);
            return Offset;
        }

        public void Start(int fieldCount)
        {
            _vtable = new int[fieldCount];
            _objectEnd = Offset;
        }

        private void Slot(int index) => _vtable![index] = Offset;

        public void FieldByte(int index, byte value)
        {
            Prep(1, 0);
            Prepend([value]);
            Slot(index);
        }

        public void FieldUInt64(int index, ulong value)
        {
            Prep(8, 0);
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            Prepend(b);
            Slot(index);
        }

        public void FieldOffset(int index, int target)
        {
            AddUOffset(target);
            Slot(index);
        }

        public int End()
        {
            // A placeholder for the offset from the table to its vtable.
            Prep(4, 0);
            PutUInt32(0);
            int obj = Offset;
            int[] vtable = _vtable!;
            int count = vtable.Length;
            while (count > 0 && vtable[count - 1] == 0)
                count--;
            for (int i = count - 1; i >= 0; i--)
                PutUInt16(vtable[i] != 0 ? obj - vtable[i] : 0);
            PutUInt16(obj - _objectEnd);
            PutUInt16(4 + 2 * count);
            int vt = Offset;
            // The vtable precedes the table in the finished buffer: the stored value is the table position minus
            // the vtable position.
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_buffer.Length - obj), vt - obj);
            _vtable = null;
            return obj;
        }

        public byte[] Finish(int root)
        {
            Prep(_minAlign, 4);
            AddUOffset(root);
            return _buffer.AsSpan(_head).ToArray();
        }
    }
}
