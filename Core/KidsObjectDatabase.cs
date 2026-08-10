using System.Numerics;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Typed reader for Koei Tecmo KIDS object/scene databases. Supports both
/// locally defined IDOK objects and inherited/reference RDOK objects.
/// </summary>
public static class KidsObjectDatabase
{
    public sealed record Property(uint NameHash, uint Type, IReadOnlyList<object> Values)
    {
        public IEnumerable<uint> UInt32Values => Values.OfType<uint>();
        public IEnumerable<int> Int32Values => Values.OfType<int>();
        public IEnumerable<float> FloatValues => Values.OfType<float>();
    }

    public sealed record Record(uint ObjectHash, uint TypeHash, IReadOnlyDictionary<uint, Property> Properties,
        string Database, uint? ParentDatabaseHash = null, uint? ParentObjectHash = null)
    {
        public bool IsReference => ParentDatabaseHash.HasValue;
        public Property? Get(uint hash) => Properties.GetValueOrDefault(hash);
        public IEnumerable<uint> References => Properties.Values.SelectMany(x => x.UInt32Values).Where(x => x != 0);
    }

    private static readonly byte[] DokMagic = "_DOK0000"u8.ToArray();
    private static readonly byte[] IdokMagic = "IDOK0000"u8.ToArray();
    private static readonly byte[] RdokMagic = "RDOK0000"u8.ToArray();

    public static IReadOnlyList<Record> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 28 || !data.AsSpan(0, 8).SequenceEqual(DokMagic))
            throw new InvalidDataException($"不是有效的 KIDS DOK 数据库：{Path.GetFileName(path)}");
        var headerSize = CheckedInt(ReadU32(data, 8), data.Length);
        if (headerSize < 28) throw new InvalidDataException("KIDS DOK 头部大小无效");
        var result = new List<Record>();
        for (var offset = headerSize; offset + 12 <= data.Length;)
        {
            var recordSize = CheckedInt(ReadU32(data, offset + 8), data.Length - offset);
            if (recordSize < 12) throw new InvalidDataException("KIDS DOK 记录大小无效");
            if (data.AsSpan(offset, 8).SequenceEqual(IdokMagic))
                result.Add(ReadIdok(data, offset, recordSize, Path.GetFileName(path)));
            else if (data.AsSpan(offset, 8).SequenceEqual(RdokMagic))
                result.Add(ReadRdok(data, offset, recordSize, Path.GetFileName(path)));
            else
                throw new InvalidDataException($"KIDS DOK 记录标记无效：0x{offset:x}");
            offset += (recordSize + 3) & ~3;
        }
        return result;
    }

    private static Record ReadIdok(byte[] data, int start, int recordSize, string database)
    {
        if (recordSize < 24 || start + recordSize > data.Length)
            throw new InvalidDataException("KIDS IDOK 记录不完整");
        return ReadProperties(data, start, recordSize, database, ReadU32(data, start + 12),
            ReadU32(data, start + 16), 20, 24, null, null);
    }

    private static Record ReadRdok(byte[] data, int start, int recordSize, string database)
    {
        if (recordSize < 28 || start + recordSize > data.Length)
            throw new InvalidDataException("KIDS RDOK 记录不完整");
        return ReadProperties(data, start, recordSize, database, ReadU32(data, start + 12), 0,
            24, 28, ReadU32(data, start + 16), ReadU32(data, start + 20));
    }

    private static Record ReadProperties(byte[] data, int start, int recordSize, string database,
        uint objectHash, uint typeHash, int propertyCountOffset, int descriptorOffset,
        uint? parentDatabaseHash, uint? parentObjectHash)
    {
        var propertyCount = CheckedInt(ReadU32(data, start + propertyCountOffset), 4096);
        var descriptorEnd = checked(start + descriptorOffset + propertyCount * 12);
        if (descriptorEnd > start + recordSize) throw new InvalidDataException("KIDS 属性描述表越界");
        var valueOffset = descriptorEnd;
        var properties = new Dictionary<uint, Property>();
        for (var i = 0; i < propertyCount; i++)
        {
            var descriptor = start + descriptorOffset + i * 12;
            var type = ReadU32(data, descriptor);
            var count = CheckedInt(ReadU32(data, descriptor + 4), 2_000_000);
            var nameHash = ReadU32(data, descriptor + 8);
            var values = new object[count];
            for (var n = 0; n < count; n++) values[n] = ReadValue(data, ref valueOffset, start + recordSize, type);
            properties[nameHash] = new(nameHash, type, values);
        }
        return new(objectHash, typeHash, properties, database, parentDatabaseHash, parentObjectHash);
    }

    private static object ReadValue(byte[] data, ref int offset, int end, uint type)
    {
        var size = type switch
        {
            0 or 1 => 1, 2 or 3 => 2, 4 or 5 or 8 => 4, 6 or 7 => 8,
            10 => 16, 12 => 8, 13 => 12, _ => 0
        };
        if (size == 0) throw new InvalidDataException($"未知 KIDS 属性类型：{type}");
        if (offset + size > end) throw new InvalidDataException("KIDS 属性值越界");
        object value = type switch
        {
            0 => unchecked((sbyte)data[offset]), 1 => data[offset],
            2 => BitConverter.ToInt16(data, offset), 3 => BitConverter.ToUInt16(data, offset),
            4 => BitConverter.ToInt32(data, offset), 5 => ReadU32(data, offset),
            6 => BitConverter.ToInt64(data, offset), 7 => BitConverter.ToUInt64(data, offset),
            8 => BitConverter.ToSingle(data, offset),
            10 => new Vector4(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8), BitConverter.ToSingle(data, offset + 12)),
            12 => new Vector2(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4)),
            13 => new Vector3(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8)),
            _ => throw new InvalidDataException($"未知 KIDS 属性类型：{type}")
        };
        offset += size;
        return value;
    }

    private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static int CheckedInt(uint value, int maximum)
    {
        if (value > maximum) throw new InvalidDataException("KIDS 数值超出允许范围");
        return checked((int)value);
    }
}
