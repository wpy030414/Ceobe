using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ceobe.Core.Client;

/// <summary>
/// NBT（Named Binary Tag）的最小读写实现。
///
/// 为什么自己写而不是拉一个库：ceobe 需要 NBT 的地方只有两处——
/// <c>servers.dat</c>（写服务器列表）与 <c>saves/&lt;world&gt;/level.dat</c>（读存档名/模式/最后游玩时间）。
/// 两者都是「小、结构固定、只碰少数几个键」，为它引入一个第三方依赖不划算；
/// 而写 <c>servers.dat</c> 是**必须**的：启动器要让服务端实例的穿透地址自动出现在多人列表里，
/// 没有这个就只能让用户手输 IP。
///
/// 实现范围：全部 12 种 tag（含 IntArray/LongArray），大端序，支持 gzip 自动识别。
/// 刻意不做 schema 映射（不需要把 NBT 反序列化成 POCO）——DOM + 取值器足够，且不会因为
/// 某个版本新增字段就崩。
/// </summary>
public enum NbtType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>NBT 节点。Compound 的值是 <c>Dictionary&lt;string, NbtTag&gt;</c>，List 是 <c>List&lt;NbtTag&gt;</c>。</summary>
public sealed class NbtTag
{
    public NbtType Type { get; }
    public object? Value { get; }

    /// <summary>
    /// 列表的元素类型。NBT 规定列表元素同型，空列表也必须记住「本来是哪种」——
    /// 否则写回时空列表会退化成 End，读的一方（以及 MC 自己）会判定为损坏。
    /// </summary>
    public NbtType ListElementType { get; }

    internal NbtTag(NbtType type, object? value, NbtType listElementType = NbtType.End)
    {
        Type = type;
        Value = value;
        ListElementType = listElementType;
    }

    // —— 构造 ——

    public static NbtTag List(NbtType elementType, IEnumerable<NbtTag>? items = null) =>
        new(NbtType.List, new List<NbtTag>(items ?? []), elementType);

    public static NbtTag Compound() => new(NbtType.Compound, new Dictionary<string, NbtTag>(StringComparer.Ordinal));

    public static NbtTag String(string? value) => new(NbtType.String, value ?? "");
    public static NbtTag Byte(byte value) => new(NbtType.Byte, value);
    public static NbtTag Short(short value) => new(NbtType.Short, value);
    public static NbtTag Int(int value) => new(NbtType.Int, value);
    public static NbtTag Long(long value) => new(NbtType.Long, value);
    public static NbtTag Float(float value) => new(NbtType.Float, value);
    public static NbtTag Double(double value) => new(NbtType.Double, value);

    // —— 取值 ——

    public IReadOnlyDictionary<string, NbtTag> Children =>
        Value as IReadOnlyDictionary<string, NbtTag> ?? new Dictionary<string, NbtTag>();

    public IReadOnlyList<NbtTag> Items => Value as IReadOnlyList<NbtTag> ?? [];

    /// <summary>取子节点；不存在返回 null（调用方按「可选字段」处理，不抛）。</summary>
    public NbtTag? this[string key] =>
        Value is Dictionary<string, NbtTag> map && map.TryGetValue(key, out var v) ? v : null;

    public string? AsString() => Value as string;

    public int AsInt() => Value switch
    {
        byte b => b,
        short s => s,
        int i => i,
        long l => (int)l,
        float f => (int)f,
        double d => (int)d,
        _ => 0,
    };

    public long AsLong() => Value switch
    {
        byte b => b,
        short s => s,
        int i => i,
        long l => l,
        _ => 0,
    };

    public bool AsBool() => AsInt() != 0;

    /// <summary>写入子节点（仅 Compound 有意义）。</summary>
    public NbtTag Set(string key, NbtTag value)
    {
        if (Value is Dictionary<string, NbtTag> map) map[key] = value;
        return this;
    }

    public NbtTag Add(NbtTag item)
    {
        if (Value is List<NbtTag> list) list.Add(item);
        return this;
    }
}

/// <summary>NBT 文件读写（gzip 自动识别）。</summary>
public static class NbtFile
{
    public static NbtTag Read(string path)
    {
        try
        {
            using var raw = File.OpenRead(path);
            return Read(raw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Util.CeobeException($"读取 NBT 文件失败: {path}", e);
        }
    }

    public static NbtTag Read(Stream stream)
    {
        Stream input = stream;
        // gzip 头 1F 8B：level.dat 与 servers.dat 都是 gzip，但不能假设——明文 NBT 也合法
        Span<byte> magic = stackalloc byte[2];
        var read = 0;
        while (read < 2 && stream.Read(magic[read..]) is var n && n > 0) read += n;
        if (read < 2) throw new Util.CeobeException("NBT 文件为空");
        stream.Seek(0, SeekOrigin.Begin);

        GZipStream? gz = null;
        if (magic[0] == 0x1F && magic[1] == 0x8B)
        {
            gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            input = gz;
        }
        try
        {
            using var reader = new NbtReader(input);
            var type = (NbtType)reader.ReadByte();
            if (type != NbtType.Compound) throw new Util.CeobeException("NBT 根节点必须是 Compound");
            _ = reader.ReadString();          // 根节点名（通常为空）
            return reader.ReadPayload(type)!;
        }
        finally
        {
            gz?.Dispose();
        }
    }

    public static void Write(string path, NbtTag root, bool gzip = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            using var file = File.Create(path);
            if (gzip)
            {
                using var gz = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true);
                WritePayload(gz, root);
            }
            else
            {
                WritePayload(file, root);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Util.CeobeException($"写入 NBT 文件失败: {path}", e);
        }
    }

    private static void WritePayload(Stream stream, NbtTag root)
    {
        using var w = new NbtWriter(stream);
        w.WriteByte((byte)NbtType.Compound);
        w.WriteString("");        // 根名
        w.WriteTagPayload(root);
        w.Flush();
    }

    // —— 读取器 ——

    private sealed class NbtReader(Stream stream) : IDisposable
    {
        /// <summary>不拥有底层流（由 NbtFile.Read 负责生命周期），这里是空实现以配合 using。</summary>
        public void Dispose() { }

        private readonly byte[] _buf = new byte[8];

        public byte ReadByte()
        {
            var b = stream.ReadByte();
            if (b < 0) throw new Util.CeobeException("NBT 数据意外结束");
            return (byte)b;
        }

        public void ReadExactly(Span<byte> dest)
        {
            var read = 0;
            while (read < dest.Length)
            {
                var n = stream.Read(dest[read..]);
                if (n <= 0) throw new Util.CeobeException("NBT 数据意外结束");
                read += n;
            }
        }

        public string ReadString()
        {
            ReadExactly(_buf.AsSpan(0, 2));
            var len = BinaryPrimitives.ReadUInt16BigEndian(_buf);
            if (len == 0) return "";
            var bytes = new byte[len];
            ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        public NbtTag? ReadPayload(NbtType type) => type switch
        {
            NbtType.End => null,
            NbtType.Byte => NbtTag.Byte(ReadByte()),
            NbtType.Short => ReadShort(),
            NbtType.Int => ReadInt(),
            NbtType.Long => ReadLong(),
            NbtType.Float => NbtTag.Float(BitConverter.Int32BitsToSingle(ReadIntRaw())),
            NbtType.Double => NbtTag.Double(BitConverter.Int64BitsToDouble(ReadLongRaw())),
            NbtType.ByteArray => ReadByteArray(),
            NbtType.String => NbtTag.String(ReadString()),
            NbtType.List => ReadList(),
            NbtType.Compound => ReadCompound(),
            NbtType.IntArray => ReadIntArray(),
            NbtType.LongArray => ReadLongArray(),
            _ => throw new Util.CeobeException($"未知 NBT 类型: {(byte)type}"),
        };

        private NbtTag ReadShort()
        {
            ReadExactly(_buf.AsSpan(0, 2));
            return NbtTag.Short(BinaryPrimitives.ReadInt16BigEndian(_buf));
        }

        private NbtTag ReadInt() => NbtTag.Int(ReadIntRaw());

        private int ReadIntRaw()
        {
            ReadExactly(_buf.AsSpan(0, 4));
            return BinaryPrimitives.ReadInt32BigEndian(_buf);
        }

        private NbtTag ReadLong() => NbtTag.Long(ReadLongRaw());

        private long ReadLongRaw()
        {
            ReadExactly(_buf.AsSpan(0, 8));
            return BinaryPrimitives.ReadInt64BigEndian(_buf);
        }

        private NbtTag ReadByteArray()
        {
            ReadExactly(_buf.AsSpan(0, 4));
            var len = BinaryPrimitives.ReadInt32BigEndian(_buf);
            if (len < 0) throw new Util.CeobeException("NBT ByteArray 长度非法");
            var bytes = new byte[len];
            ReadExactly(bytes);
            return new NbtTag(NbtType.ByteArray, bytes);
        }

        private NbtTag ReadIntArray()
        {
            ReadExactly(_buf.AsSpan(0, 4));
            var len = BinaryPrimitives.ReadInt32BigEndian(_buf);
            if (len < 0) throw new Util.CeobeException("NBT IntArray 长度非法");
            var arr = new int[len];
            for (var i = 0; i < len; i++)
            {
                ReadExactly(_buf.AsSpan(0, 4));
                arr[i] = BinaryPrimitives.ReadInt32BigEndian(_buf);
            }
            return new NbtTag(NbtType.IntArray, arr);
        }

        private NbtTag ReadLongArray()
        {
            ReadExactly(_buf.AsSpan(0, 4));
            var len = BinaryPrimitives.ReadInt32BigEndian(_buf);
            if (len < 0) throw new Util.CeobeException("NBT LongArray 长度非法");
            var arr = new long[len];
            for (var i = 0; i < len; i++)
            {
                ReadExactly(_buf.AsSpan(0, 8));
                arr[i] = BinaryPrimitives.ReadInt64BigEndian(_buf);
            }
            return new NbtTag(NbtType.LongArray, arr);
        }

        private NbtTag ReadList()
        {
            var elementType = (NbtType)ReadByte();
            ReadExactly(_buf.AsSpan(0, 4));
            var len = BinaryPrimitives.ReadInt32BigEndian(_buf);
            if (len < 0) throw new Util.CeobeException("NBT List 长度非法");
            var list = NbtTag.List(elementType);
            for (var i = 0; i < len; i++)
            {
                var item = ReadPayload(elementType);
                if (item is not null) list.Add(item);
            }
            return list;
        }

        private NbtTag ReadCompound()
        {
            var compound = NbtTag.Compound();
            while (true)
            {
                var type = (NbtType)ReadByte();
                if (type == NbtType.End) break;
                var name = ReadString();
                var value = ReadPayload(type);
                if (value is not null) compound.Set(name, value);
            }
            return compound;
        }
    }

    // —— 写入器 ——

    private sealed class NbtWriter(Stream stream) : IDisposable
    {
        /// <summary>不拥有底层流（由 NbtFile.Write 负责 Flush/释放），空实现以配合 using。</summary>
        public void Dispose() { }

        private readonly byte[] _buf = new byte[8];

        public void WriteByte(byte value) => stream.WriteByte(value);

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt16BigEndian(_buf, checked((ushort)bytes.Length));
            stream.Write(_buf.AsSpan(0, 2));
            stream.Write(bytes);
        }

        public void WriteTagPayload(NbtTag tag)
        {
            switch (tag.Type)
            {
                case NbtType.Byte:
                    WriteByte((byte)tag.AsInt());
                    break;
                case NbtType.Short:
                    WriteShort((short)tag.AsInt());
                    break;
                case NbtType.Int:
                    WriteInt(tag.AsInt());
                    break;
                case NbtType.Long:
                    WriteLong(tag.AsLong());
                    break;
                case NbtType.Float:
                    WriteInt(BitConverter.SingleToInt32Bits((float)(tag.Value ?? 0f)));
                    break;
                case NbtType.Double:
                    WriteLong(BitConverter.DoubleToInt64Bits((double)(tag.Value ?? 0d)));
                    break;
                case NbtType.ByteArray:
                {
                    var arr = tag.Value as byte[] ?? [];
                    WriteInt(arr.Length);
                    stream.Write(arr);
                    break;
                }
                case NbtType.String:
                    WriteString(tag.AsString() ?? "");
                    break;
                case NbtType.List:
                {
                    var items = tag.Items;
                    var elementType = tag.ListElementType != NbtType.End
                        ? tag.ListElementType
                        : items.Count > 0 ? items[0].Type : NbtType.End;
                    WriteByte((byte)elementType);
                    WriteInt(items.Count);
                    foreach (var item in items) WriteTagPayload(item);
                    break;
                }
                case NbtType.Compound:
                    foreach (var (key, value) in tag.Children)
                    {
                        WriteByte((byte)value.Type);
                        WriteString(key);
                        WriteTagPayload(value);
                    }
                    WriteByte((byte)NbtType.End);
                    break;
                case NbtType.IntArray:
                {
                    var arr = tag.Value as int[] ?? [];
                    WriteInt(arr.Length);
                    foreach (var i in arr) WriteInt(i);
                    break;
                }
                case NbtType.LongArray:
                {
                    var arr = tag.Value as long[] ?? [];
                    WriteInt(arr.Length);
                    foreach (var l in arr) WriteLong(l);
                    break;
                }
            }
        }

        private void WriteShort(short value)
        {
            BinaryPrimitives.WriteInt16BigEndian(_buf, value);
            stream.Write(_buf.AsSpan(0, 2));
        }

        private void WriteInt(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(_buf, value);
            stream.Write(_buf.AsSpan(0, 4));
        }

        private void WriteLong(long value)
        {
            BinaryPrimitives.WriteInt64BigEndian(_buf, value);
            stream.Write(_buf.AsSpan(0, 8));
        }

        public void Flush() => stream.Flush();
    }
}
