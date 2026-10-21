using MiniBox.WowForever;
using System.Text.Json;

// Synthetic fixtures contain no game data. Verify old/new WMO layouts and collision-only faces.
byte[] Bytes(Action<BinaryWriter> write)
{
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    write(writer); return stream.ToArray();
}
byte[] Chunk(string name, byte[] data) => Bytes(w => { w.Write(ChunkId.Of(name)); w.Write(data.Length); w.Write(data); });
byte[] Join(params byte[][] pieces) => pieces.SelectMany(x => x).ToArray();
var header = new byte[64]; BitConverter.GetBytes(1).CopyTo(header, 4);
var material = new byte[64]; BitConverter.GetBytes(42u).CopyTo(material, 12);
var root = Join(Chunk("MOHD", header), Chunk("MOMT", material), Chunk("GFID", BitConverter.GetBytes(1u)));
byte[] Group(bool expandedFlags, bool expandedIndices, uint groupFlags = 0)
{
    var groupHeader = new byte[68]; BitConverter.GetBytes(groupFlags).CopyTo(groupHeader, 8);
    var flags = Bytes(w => { foreach (ushort f in new ushort[] { 32, 8, 36 })
        if (expandedFlags) { w.Write(f); w.Write((ushort)0); } else { w.Write((byte)f); w.Write((byte)0); } });
    var indices = Bytes(w => { for (int i = 0; i < 9; i++) if (expandedIndices) w.Write(i); else w.Write((ushort)i); });
    var positions = Bytes(w => { for (int i = 0; i < 9; i++) { w.Write((float)(i % 3)); w.Write((float)(i / 3)); w.Write((float)(i % 2)); } });
    var batch = new byte[24]; BitConverter.GetBytes((ushort)3).CopyTo(batch, 16);
    return Chunk("MOGP", Join(groupHeader, Chunk(expandedFlags ? "MPY2" : "MOPY", flags),
        Chunk(expandedIndices ? "MOVX" : "MOVI", indices), Chunk("MOVT", positions), Chunk("MOBA", batch)));
}
var checks = new List<object>();
foreach (bool flags in new[] { false, true }) foreach (bool indices in new[] { false, true })
{
    var group = Group(flags, indices); var model = WmoReader.Read(100, root, id => id == 1 ? group : null);
    if (model.CollisionIndices.Length != 6 || model.CollisionPositions.Length != 6 || model.Parts.Sum(p => p.Indices.Length) != 3)
        throw new Exception("Collision-only triangle missing or non-solid triangle incorrectly included");
    checks.Add(new { Flags = flags ? "MPY2" : "MOPY", Indices = indices ? "MOVX" : "MOVI", CollisionTriangles = 2, RenderTriangles = 1 });
}
foreach (uint mask in new uint[] { 0x80, 0x4000000 })
{
    var group = Group(true, true, mask); var model = WmoReader.Read(100, root, _ => group);
    if (model.CollisionIndices.Length != 0) throw new Exception("Unreachable/antiportal group became solid");
}
object actual = null;
if (args.Length > 0)
{
    byte[] Read(uint id) { var path = Path.Combine(args[0], id + ".bin"); return File.Exists(path) ? File.ReadAllBytes(path) : null; }
    var city = WmoReader.Read(7964229, Read(7964229), Read);
    if (city.CollisionIndices.Length < 10000) throw new Exception("Forever Orgrimmar collision regression");
    actual = new { Model = 7964229, CollisionTriangles = city.CollisionIndices.Length / 3, CollisionVertices = city.CollisionPositions.Length };
}
Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, SyntheticLayouts = checks, ActualCity = actual }, new JsonSerializerOptions { WriteIndented = true }));
