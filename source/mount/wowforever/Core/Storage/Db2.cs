namespace MiniBox.WowForever;

/// <summary>Client database tables (WDC5) read at run time from the installation: numeric fields only.
/// Field indices follow the table's DBD layout for the installed build. Sections whose key is not
/// public (encrypted, zero-filled) are skipped. Adapted from the WDC3/4/5 description of wowdev.wiki.</summary>
public sealed class Db2
{
    private sealed record Field(int OffsetBits, int SizeBits, int StorageType, uint V1, uint V2, uint V3, int PalletOffset, int CommonOffset, int AdditionalSize);
    private readonly byte[] data;
    private readonly Field[] fields;
    private readonly int recordSize, palletStart, commonStart;
    private readonly Dictionary<uint, int> rows = new();
    private readonly Dictionary<int, Dictionary<uint, uint>> common = new();
    private readonly Dictionary<uint, uint> relations = new();
    // Strings: offsets count from the field's place in all sections' records laid end to end, into all
    // sections' string tables laid end to end after them.
    private readonly Dictionary<uint, int> globalRows = new();
    private readonly byte[] strings = Array.Empty<byte>();
    private readonly int recordsTotal;
    /// <summary>Offset-map ("sparse") tables, like ItemSparse: variable-size records, strings inline.</summary>
    private readonly bool sparse;
    public IReadOnlyCollection<uint> Ids => rows.Keys;

    public Db2(byte[] file)
    {
        data = file;
        if (BitConverter.ToUInt32(file, 0) != 0x35434457) throw new InvalidDataException("Not a WDC5 table");
        int h = 136;
        int fieldCount = I(h + 4); recordSize = I(h + 8); int flags = BitConverter.ToUInt16(file, h + 36), idIndex = BitConverter.ToUInt16(file, h + 38);
        int storageSize = I(h + 52), commonSize = I(h + 56), palletSize = I(h + 60), sectionCount = I(h + 64);
        int sections = 204, structures = sections + sectionCount * 40, storage = structures + fieldCount * 4;
        palletStart = storage + storageSize; commonStart = palletStart + palletSize;
        fields = new Field[storageSize / 24];
        int pallet = 0, commonOffset = 0;
        for (int f = 0; f < fields.Length; f++)
        {
            int o = storage + f * 24; int type = I(o + 8);
            fields[f] = new Field(BitConverter.ToUInt16(file, o), BitConverter.ToUInt16(file, o + 2), type, U(o + 12), U(o + 16), U(o + 20), pallet, commonOffset, I(o + 4));
            if (type is 3 or 4) pallet += I(o + 4);
            if (type == 2) commonOffset += I(o + 4);
        }
        if ((flags & 1) != 0)
        {
            // Records sit at the offsets of the offset map, their ids in the id list or the offset map id list.
            sparse = true;
            for (int s = 0; s < sectionCount; s++)
            {
                int o = sections + s * 40;
                int end = I(o + 20), idListSize = I(o + 24), relationSize = I(o + 28), mapCount = I(o + 32), copyCount = I(o + 36);
                if (mapCount == 0) continue;
                int idList = end, copies = idList + idListSize, map = copies + copyCount * 8, mapIds = map + mapCount * 6 + relationSize;
                for (int r = 0; r < mapCount; r++)
                {
                    uint id = idListSize > 0 ? U(idList + r * 4) : U(mapIds + r * 4);
                    int at = I(map + r * 6);
                    if (BitConverter.ToUInt16(file, map + r * 6 + 4) > 0 && at > 0) rows[id] = at;
                }
                for (int c = 0; c < copyCount; c++) if (rows.TryGetValue(U(copies + c * 8 + 4), out int source)) rows[U(copies + c * 8)] = source;
            }
            return;
        }
        var blob = new List<byte>();
        for (int s = 0; s < sectionCount; s++)
        {
            int o = sections + s * 40, count = I(o + 12), size = I(o + 16);
            recordsTotal += count * recordSize;
            if (size > 0) blob.AddRange(file.AsSpan(I(o + 8) + count * recordSize, size).ToArray());
        }
        strings = blob.ToArray();
        int globalBase = 0;
        for (int s = 0; s < sectionCount; s++)
        {
            int o = sections + s * 40;
            int offset = I(o + 8), count = I(o + 12), strings = I(o + 16), idListSize = I(o + 24), relationSize = I(o + 28), copyCount = I(o + 36), offsetMapCount = I(o + 32);
            int sectionBase = globalBase; globalBase += count * recordSize;
            if (count == 0) continue;
            int records = offset, idList = records + count * recordSize + strings;
            // A section the client could not decrypt is zero-filled: skip it.
            if (U(o) != 0 && file.AsSpan(records, Math.Min(count * recordSize, 256)).IndexOfAnyExcept((byte)0) < 0) continue;
            var sectionIds = new uint[count];
            for (int r = 0; r < count; r++)
            {
                int row = records + r * recordSize;
                uint id = idListSize > 0 ? U(idList + r * 4) : Value(row, idIndex, 0, 0);
                rows[id] = row; sectionIds[r] = id; globalRows[id] = sectionBase + r * recordSize;
            }
            int copies = idList + idListSize;
            // Relationship map: a foreign key kept outside the records ($relation$ fields), by record index.
            int relation = copies + copyCount * 8 + offsetMapCount * 6;
            if (relationSize >= 12)
                for (int e = 0, n = I(relation); e < n && relation + 12 + e * 8 + 8 <= relation + relationSize; e++)
                {
                    int index = I(relation + 12 + e * 8 + 4);
                    if (index >= 0 && index < count) relations[sectionIds[index]] = U(relation + 12 + e * 8);
                }
            for (int c = 0; c < copyCount; c++) if (rows.TryGetValue(U(copies + c * 8 + 4), out int source)) rows[U(copies + c * 8)] = source;
        }
    }

    public bool Has(uint id) => rows.ContainsKey(id);

    /// <summary>Sparse records: the <paramref name="index"/>-th of the leading inline strings.</summary>
    public string SparseString(uint id, int index)
    {
        if (!sparse || !rows.TryGetValue(id, out int at)) return "";
        for (int k = 0; k < index; k++) at = Array.IndexOf(data, (byte)0, at) + 1;
        int end = Array.IndexOf(data, (byte)0, at);
        return end < 0 ? "" : System.Text.Encoding.UTF8.GetString(data, at, end - at);
    }

    /// <summary>Sparse records: a numeric field after <paramref name="strings"/> leading strings, fields packed
    /// one after the other at their storage size.</summary>
    public uint SparseUInt(uint id, int strings, int field)
    {
        if (!sparse || !rows.TryGetValue(id, out int at)) return 0;
        for (int k = 0; k < strings; k++) at = Array.IndexOf(data, (byte)0, at) + 1;
        for (int f = strings; f < field; f++) at += fields[f].SizeBits / 8;
        int size = Math.Min(4, fields[field].SizeBits / 8);
        uint value = 0;
        for (int b = 0; b < size; b++) value |= (uint)data[at + b] << (8 * b);
        return value;
    }

    /// <summary>An inline string field (a name), or "" when absent.</summary>
    public string String(uint id, int field)
    {
        if (!rows.TryGetValue(id, out int row) || !globalRows.TryGetValue(id, out int global)) return "";
        var f = fields[field];
        long at = (long)global + f.OffsetBits / 8 + Bits(row, f.OffsetBits, 32) - recordsTotal;
        if (at < 0 || at >= strings.Length) return "";
        int end = Array.IndexOf(strings, (byte)0, (int)at);
        return System.Text.Encoding.UTF8.GetString(strings, (int)at, (end < 0 ? strings.Length : end) - (int)at);
    }
    /// <summary>The record's $relation$ foreign key (for example TextureFileData.MaterialResourcesID).</summary>
    public uint Relation(uint id) => relations.TryGetValue(id, out var value) ? value : 0;
    public uint UInt(uint id, int field, int index = 0) => rows.TryGetValue(id, out int row) ? Value(row, field, index, id) : 0;
    public int Int(uint id, int field, int index = 0) => unchecked((int)UInt(id, field, index));
    public float Float(uint id, int field, int index = 0) => BitConverter.Int32BitsToSingle(Int(id, field, index));

    private uint Value(int row, int field, int index, uint id)
    {
        var f = fields[field];
        switch (f.StorageType)
        {
            case 0: // inline, arrays of equal elements
            {
                int count = Math.Max(1, f.SizeBits / 32 > 0 && f.SizeBits % 32 == 0 ? f.SizeBits / 32 : 1);
                int element = count > 1 ? 32 : f.SizeBits;
                return Bits(row, f.OffsetBits + index * element, element);
            }
            case 1: return Bits(row, f.OffsetBits, (int)f.V2);
            case 5:
            {
                uint v = Bits(row, f.OffsetBits, (int)f.V2); int bits = (int)f.V2;
                return bits < 32 && (v >> (bits - 1) & 1) != 0 ? v | (uint.MaxValue << bits) : v;
            }
            case 2:
            {
                if (!common.TryGetValue(field, out var map))
                {
                    map = new Dictionary<uint, uint>();
                    for (int o = commonStart + f.CommonOffset; o < commonStart + f.CommonOffset + f.AdditionalSize; o += 8) map[U(o)] = U(o + 4);
                    common[field] = map;
                }
                return map.TryGetValue(id, out var value) ? value : f.V1;
            }
            case 3: return U(palletStart + f.PalletOffset + (int)Bits(row, f.OffsetBits, (int)f.V2) * 4);
            case 4: return U(palletStart + f.PalletOffset + ((int)Bits(row, f.OffsetBits, (int)f.V2) * (int)f.V3 + index) * 4);
            default: return 0;
        }
    }

    private uint Bits(int row, int offset, int size)
    {
        if (size <= 0) return 0;
        ulong value = 0; int first = row + offset / 8, shift = offset % 8, bytes = (shift + size + 7) / 8;
        for (int b = 0; b < bytes && b < 8; b++) value |= (ulong)data[first + b] << (8 * b);
        value >>= shift;
        return size >= 32 ? (uint)value : (uint)(value & ((1UL << size) - 1));
    }

    private int I(int o) => BitConverter.ToInt32(data, o);
    private uint U(int o) => BitConverter.ToUInt32(data, o);
}
