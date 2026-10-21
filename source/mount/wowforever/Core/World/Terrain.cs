using System.Buffers.Binary;

namespace MiniBox.WowForever;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Little-endian cursor over one chunked client file.</summary>
public ref struct Cursor
{
    private readonly ReadOnlySpan<byte> data;
    public int Offset;
    public Cursor(ReadOnlySpan<byte> data, int offset = 0) { this.data = data; Offset = offset; }
    public int Length => data.Length;
    public bool More => Offset + 8 <= data.Length;
    public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(data[Offset..]); Offset += 4; return v; }
    public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(data[Offset..]); Offset += 4; return v; }
    public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(data[Offset..]); Offset += 2; return v; }
    public short I16() { var v = BinaryPrimitives.ReadInt16LittleEndian(data[Offset..]); Offset += 2; return v; }
    public byte U8() => data[Offset++];
    public sbyte I8() => (sbyte)data[Offset++];
    public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(data[Offset..]); Offset += 4; return v; }
    public Vector3 V3() => new(F32(), F32(), F32());
    public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(data[Offset..]); Offset += 8; return v; }
    public ReadOnlySpan<byte> Bytes(int n) { var s = data.Slice(Offset, n); Offset += n; return s; }
    public ReadOnlySpan<byte> Span => data;
}

public static class ChunkId
{
    public static uint Of(string fourCc) => (uint)(fourCc[3] | fourCc[2] << 8 | fourCc[1] << 16 | fourCc[0] << 24);
    /// <summary>M2 and skin identifiers are stored in reading order.</summary>
    public static uint Lit(string fourCc) => (uint)(fourCc[0] | fourCc[1] << 8 | fourCc[2] << 16 | fourCc[3] << 24);
}

/// <summary>WDT: which tiles exist and the FileDataIDs of their ADT parts.</summary>
public sealed class Wdt
{
    public uint Flags;
    public readonly Dictionary<(int Row, int Col), TileFiles> Tiles = new();
    public uint GlobalWmo; public Vector3 GlobalWmoPosition, GlobalWmoRotation; public ushort GlobalWmoDoodadSet;

    public struct TileFiles { public uint Root, Obj0, Obj1, Tex0, Lod, MapTexture, MapTextureN, Minimap; public string Id; }

    public static Wdt Read(byte[] bytes)
    {
        var w = new Wdt(); var c = new Cursor(bytes); var present = new bool[4096];
        while (c.More)
        {
            uint id = c.U32(); int size = (int)c.U32(); int next = c.Offset + size;
            if (id == ChunkId.Of("MPHD")) w.Flags = c.U32();
            else if (id == ChunkId.Of("MAIN"))
                for (int k = 0; k < 4096; k++) { present[k] = (c.U32() & 1) != 0; c.U32(); }
            else if (id == ChunkId.Of("MAID"))
                for (int k = 0; k < 4096; k++)
                {
                    var t = new TileFiles { Root = c.U32(), Obj0 = c.U32(), Obj1 = c.U32(), Tex0 = c.U32(), Lod = c.U32(), MapTexture = c.U32(), MapTextureN = c.U32(), Minimap = c.U32() };
                    // Same ordering and naming as the converted catalog: "<k % 64>_<k / 64>".
                    t.Id = $"{k % 64}_{k / 64}";
                    if (present[k] && t.Root != 0) w.Tiles[(k % 64, k / 64)] = t;
                }
            else if (id == ChunkId.Of("MODF"))
            {
                w.GlobalWmo = c.U32(); c.U32(); w.GlobalWmoPosition = c.V3(); w.GlobalWmoRotation = c.V3();
                c.V3(); c.V3(); c.U16(); w.GlobalWmoDoodadSet = c.U16();
            }
            c.Offset = next;
        }
        return w;
    }
}

/// <summary>ADT root, texture and object files (adapted from wow.export's ADT reader, MIT).</summary>
public sealed class AdtRoot
{
    public sealed class Chunk
    {
        public uint Flags; public byte[] HolesHigh = new byte[8]; public ushort HolesLow; public ulong NoEffectDoodad; public Vector3 Position; public uint AreaId;
        public float[] Heights; public sbyte[] Normals;
    }
    public sealed class Liquid
    {
        public int Chunk; public ushort Type, Object; public float Min, Max; public int X, Y, Width, Height;
        public byte[] Bitmap; public float[] Heights;
    }
    public readonly Chunk[] Chunks = new Chunk[256];
    public readonly List<Liquid> Liquids = new();

    public static AdtRoot Read(byte[] bytes)
    {
        var adt = new AdtRoot(); var c = new Cursor(bytes); int index = 0;
        while (c.More)
        {
            uint id = c.U32(); int size = (int)c.U32(); int start = c.Offset, next = start + size;
            if (id == ChunkId.Of("MCNK") && index < 256)
            {
                var ch = adt.Chunks[index++] = new Chunk();
                ch.Flags = c.U32(); c.Offset += 16; ch.HolesHigh = c.Bytes(8).ToArray();
                c.Offset = start + 0x3C; ch.HolesLow = c.U16(); c.Offset = start + 0x50; ch.NoEffectDoodad = c.U64();
                c.Offset = start + 0x68; ch.Position = c.V3();
                c.Offset = start + 0x34; ch.AreaId = c.U32();
                c.Offset = start + 0x80;
                while (c.Offset + 8 <= next)
                {
                    uint sub = c.U32(); int subSize = (int)c.U32(); int subNext = c.Offset + subSize;
                    if (sub == ChunkId.Of("MCVT")) { ch.Heights = new float[145]; for (int i = 0; i < 145; i++) ch.Heights[i] = c.F32(); }
                    else if (sub == ChunkId.Of("MCNR")) { ch.Normals = new sbyte[435]; for (int i = 0; i < 435; i++) ch.Normals[i] = c.I8(); }
                    c.Offset = subNext;
                }
            }
            else if (id == ChunkId.Of("MH2O")) ReadLiquids(adt, bytes, start, size);
            c.Offset = next;
        }
        if (index != 256 || adt.Chunks.Any(x => x?.Heights is null)) throw new InvalidDataException("Incomplete ADT");
        return adt;
    }

    private static void ReadLiquids(AdtRoot adt, byte[] bytes, int baseOffset, int size)
    {
        var c = new Cursor(bytes, baseOffset);
        var headers = new (uint Instances, uint Count, uint Attributes)[256];
        var offsets = new SortedSet<uint>();
        for (int i = 0; i < 256; i++) { headers[i] = (c.U32(), c.U32(), c.U32()); if (headers[i].Attributes > 0) offsets.Add(headers[i].Attributes); }
        var all = new List<(Liquid L, uint Bitmap, uint Vertex)>();
        for (int i = 0; i < 256; i++)
        {
            if (headers[i].Count == 0) continue;
            c.Offset = baseOffset + (int)headers[i].Instances;
            for (int j = 0; j < headers[i].Count; j++)
            {
                var l = new Liquid { Chunk = i, Type = c.U16(), Object = c.U16(), Min = c.F32(), Max = c.F32(), X = c.U8(), Y = c.U8(), Width = c.U8(), Height = c.U8() };
                uint bitmap = c.U32(), vertex = c.U32();
                if (l.Object <= 41) { l.X = 0; l.Y = 0; l.Width = 8; l.Height = 8; }
                if (bitmap > 0) offsets.Add(bitmap); if (vertex > 0) offsets.Add(vertex);
                all.Add((l, bitmap, vertex));
            }
        }
        var sorted = offsets.ToList();
        foreach (var (l, bitmap, vertex) in all)
        {
            if (bitmap > 0) l.Bitmap = bytes.AsSpan(baseOffset + (int)bitmap, (l.Width * l.Height + 7) / 8).ToArray();
            int count = (l.Width + 1) * (l.Height + 1);
            float level = l.Min == 0 && l.Max == 0 ? 0 : (l.Min + l.Max) / 2;
            l.Heights = Enumerable.Repeat(level, count).ToArray();
            if (vertex > 0)
            {
                int k = sorted.IndexOf(vertex);
                int dataSize = k < sorted.Count - 1 ? (int)(sorted[k + 1] - vertex) : size - (int)vertex;
                int per = dataSize / count;
                if (per is 5 or 8 or 9)
                {
                    var v = new Cursor(bytes, baseOffset + (int)vertex);
                    for (int n = 0; n < count; n++) l.Heights[n] = v.F32();
                }
            }
            adt.Liquids.Add(l);
        }
    }
}

public sealed class AdtTex
{
    public sealed class Layer { public uint Texture, Flags, AlphaOffset; public int Effect; }
    public sealed class Chunk { public Layer[] Layers = Array.Empty<Layer>(); public byte[][] Alpha = Array.Empty<byte[]>(); }
    public uint[] Diffuse = Array.Empty<uint>(), Height = Array.Empty<uint>();
    public (uint Flags, float Height, float Offset)[] Params = Array.Empty<(uint, float, float)>();
    public readonly Chunk[] Chunks = new Chunk[256];

    public static AdtTex Read(byte[] bytes, uint wdtFlags)
    {
        var t = new AdtTex(); var c = new Cursor(bytes); int index = 0;
        while (c.More)
        {
            uint id = c.U32(); int size = (int)c.U32(); int next = c.Offset + size;
            if (id == ChunkId.Of("MDID")) { t.Diffuse = new uint[size / 4]; for (int i = 0; i < t.Diffuse.Length; i++) t.Diffuse[i] = c.U32(); }
            else if (id == ChunkId.Of("MHID")) { t.Height = new uint[size / 4]; for (int i = 0; i < t.Height.Length; i++) t.Height[i] = c.U32(); }
            else if (id == ChunkId.Of("MTXP")) { t.Params = new (uint, float, float)[size / 16]; for (int i = 0; i < t.Params.Length; i++) { t.Params[i] = (c.U32(), c.F32(), c.F32()); c.U32(); } }
            else if (id == ChunkId.Of("MCNK") && index < 256)
            {
                var ch = t.Chunks[index++] = new Chunk();
                while (c.Offset + 8 <= next)
                {
                    uint sub = c.U32(); int subSize = (int)c.U32(); int subNext = c.Offset + subSize;
                    if (sub == ChunkId.Of("MCLY"))
                    {
                        ch.Layers = new Layer[subSize / 16];
                        for (int i = 0; i < ch.Layers.Length; i++) ch.Layers[i] = new Layer { Texture = c.U32(), Flags = c.U32(), AlphaOffset = c.U32(), Effect = c.I32() };
                    }
                    else if (sub == ChunkId.Of("MCAL")) ch.Alpha = ReadAlpha(ref c, ch.Layers, wdtFlags, subNext);
                    c.Offset = subNext;
                }
            }
            c.Offset = next;
        }
        for (int i = 0; i < 256; i++) t.Chunks[i] ??= new Chunk();
        return t;
    }

    private static byte[][] ReadAlpha(ref Cursor c, Layer[] layers, uint wdtFlags, int end)
    {
        var alpha = new byte[layers.Length][];
        int start = c.Offset;
        for (int i = 1; i < layers.Length; i++)
        {
            var a = alpha[i] = new byte[4096];
            c.Offset = start + (int)layers[i].AlphaOffset;
            if ((layers[i].Flags & 512) != 0)
            {
                int o = 0;
                while (o < 4096 && c.Offset < end)
                {
                    byte info = c.U8(); int count = info & 127;
                    if ((info & 128) != 0) { byte v = c.U8(); while (count-- > 0 && o < 4096) a[o++] = v; }
                    else while (count-- > 0 && o < 4096) a[o++] = c.U8();
                }
            }
            else if ((wdtFlags & 4) != 0 || (wdtFlags & 128) != 0) c.Bytes(4096).CopyTo(a);
            else
            {
                var raw = c.Bytes(2048);
                for (int j = 0; j < 2048; j++) { a[2 * j] = (byte)((raw[j] & 15) * 17); a[2 * j + 1] = (byte)((raw[j] >> 4) * 17); }
            }
        }
        return alpha;
    }
}

public sealed class AdtObj
{
    public sealed class Placement { public bool Wmo; public uint Model; public uint UniqueId; public Vector3 Position, Rotation; public float Scale = 1; public ushort Flags; public ushort DoodadSet; public int[] DoodadSets = Array.Empty<int>(); }
    public readonly List<Placement> Placements = new();

    public static AdtObj Read(byte[] bytes)
    {
        var o = new AdtObj(); var c = new Cursor(bytes); ushort[] wmoSets = Array.Empty<ushort>();
        while (c.More)
        {
            uint id = c.U32(); int size = (int)c.U32(); int next = c.Offset + size;
            if (id == ChunkId.Of("MDDF"))
                for (int i = 0; i < size / 36; i++)
                {
                    var p = new Placement { Model = c.U32(), UniqueId = c.U32(), Position = c.V3(), Rotation = c.V3() };
                    p.Scale = c.U16() / 1024f; p.Flags = c.U16();
                    o.Placements.Add(p); // Forever's object files reference models by FileDataID
                }
            else if (id == ChunkId.Of("MODF"))
                for (int i = 0; i < size / 64; i++)
                {
                    var p = new Placement { Wmo = true, Model = c.U32(), UniqueId = c.U32(), Position = c.V3(), Rotation = c.V3() };
                    c.V3(); c.V3(); p.Flags = c.U16(); p.DoodadSet = c.U16(); c.U16(); ushort scale = c.U16();
                    p.Scale = scale != 0 ? scale / 1024f : 1;
                    o.Placements.Add(p);
                }
            else if (id == ChunkId.Of("MWDS")) { wmoSets = new ushort[size / 2]; for (int i = 0; i < wmoSets.Length; i++) wmoSets[i] = c.U16(); }
            c.Offset = next;
        }
        foreach (var p in o.Placements.Where(p => p.Wmo))
            p.DoodadSets = (p.Flags & 128) != 0 ? wmoSets.Select(s => (int)s).ToArray() : new[] { (int)p.DoodadSet };
        return o;
    }
}
