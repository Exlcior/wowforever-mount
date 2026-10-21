
namespace MiniBox.WowForever;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>Static geometry of one M2 or WMO, in the converter's convention: client (x, y, z) becomes (-y, x, z).</summary>
public sealed class ModelData
{
    public sealed class Part
    {
        public Vector3[] Positions; public Vector3[] Normals; public Vector2[] Uv; public int[] Indices;
        public int[] SourceVertices;
        public uint Texture; public int Blend; public uint Flags; public bool Collision;
        /// <summary>Texture scrolling in UV per second (M2 texture transforms), zero when static.</summary>
        public Vector2 Scroll;
        /// <summary>Non-zero for liquid surfaces: the LiquidType ID.</summary>
        public int Liquid;
        /// <summary>Level of detail: 0 is the full model, then the client's own reduced versions (M2 LOD skins, WMO LOD groups).</summary>
        public int Lod;
        /// <summary>M2 geoset (group * 100 + variant) and replaceable texture type (0 when the texture is fixed).</summary>
        public int Geoset, TextureType;
    }
    /// <summary>A particle emitter in its bind pose (sprites only), values from the first key of each track.</summary>
    public sealed class Emitter
    {
        /// <summary>Position on its bone (bone space, for animated models), and placed by the bone's resting pose.</summary>
        public Vector3 Position, Placed; public uint Texture; public int Blend, Rows = 1, Columns = 1, Bone = -1; public uint Flags;
        public float Speed, SpeedVariation, VerticalRange, HorizontalRange, Gravity, Lifespan, Rate, AreaWidth, AreaLength;
        /// <summary>Start, middle and end of the particle life.</summary>
        public Vector3[] Colors = { Vector3.One, Vector3.One, Vector3.One }; public float[] Alpha = { 1, 1, 1 }; public float[] Scale = { 1, 1, 1 };
    }
    public List<Emitter> Emitters = new();
    /// <summary>A ribbon emitter (sword and spell trails): a strip left behind its bone, values from the first keys.</summary>
    public sealed class Ribbon
    {
        public Vector3 Position, Placed; public uint Texture; public int Blend, Bone = -1;
        public float Above, Below, EdgesPerSecond, Lifetime, Gravity; public Vector3 Color = Vector3.One; public float Alpha = 1;
    }
    public List<Ribbon> Ribbons = new();
    /// <summary>An M2 camera (character creation and login scenes): where it stands, what it looks at, its field of view.</summary>
    public sealed class Camera { public int Type; public Vector3 Position, Target; public float Fov; }
    public List<Camera> Cameras = new();
    /// <summary>An M2 light (scenes light their own characters): type 0 directional, 1 point; colours times intensity.</summary>
    public sealed class Light { public int Type; public Vector3 Position, Ambient, Diffuse; public float Start, End; }
    /// <summary>A card the client turns toward the camera (billboard bone): centre, width, texture and blend.</summary>
    public sealed class Billboard { public Vector3 Position; public float Size, Opacity = 1; public uint Texture; public int Blend; public bool Unlit; }
    public List<Billboard> Billboards = new();
    public List<Light> Lights = new();
    /// <summary>The client's own collision mesh (M2 bounding triangles), in the same convention as the parts.</summary>
    public Vector3[] CollisionPositions = Array.Empty<Vector3>();
    public int[] CollisionIndices = Array.Empty<int>();
    public sealed class Doodad { public int Id; public uint Model; public Vector3 Position; public Quaternion Rotation; public float Scale; public int[] Sets; }
    public string Type;
    public List<Part> Parts = new();
    /// <summary>Pieces the client draws nearly invisible (fog cards, light shafts): not drawn, kept as scene surfaces.</summary>
    public List<Part> Faint = new();
    public List<Doodad> Doodads = new();
    public Vector3 Min, Max;
    public float Radius => Parts.Count == 0 ? 0 : Vector3.Distance(Min, Max) / 2;
    public int Unresolved;

    internal static Vector3 Convert(Vector3 v) => new(-v.Y, v.X, v.Z);

    private bool grown;
    internal void Grow(IEnumerable<Vector3> points)
    {
        foreach (var p in points)
        {
            if (!grown) { Min = Max = p; grown = true; continue; }
            Min = Vector3.Min(Min, p); Max = Vector3.Max(Max, p);
        }
    }
}

/// <summary>M2 static meshes from the first skin profile (adapted from wow.export, MIT).</summary>
public static class M2Reader
{
    // A few doodads reference a replaceable texture that the client fills from DB tables.
    private static readonly Dictionary<uint, uint> TextureFallback = new() { [124904] = 124906, [124903] = 124906, [124166] = 124165, [142631] = 142633 };

    public static ModelData Read(uint id, byte[] bytes, Func<uint, byte[]> read, Func<int, uint> replaceableTexture = null, Func<int, bool> geoset = null)
    {
        var c = new Cursor(bytes); int md20 = -1, md20Size = 0; uint[] skins = Array.Empty<uint>(), txid = Array.Empty<uint>();
        if (BitConverter.ToUInt32(bytes, 0) != ChunkId.Lit("MD21")) throw new InvalidDataException("Expected chunked M2 (MD21)");
        while (c.More)
        {
            uint chunk = c.U32(); int size = (int)c.U32(); int next = c.Offset + size;
            if (chunk == ChunkId.Lit("MD21")) { md20 = c.Offset; md20Size = size; }
            else if (chunk == ChunkId.Lit("SFID")) { skins = new uint[size / 4]; for (int i = 0; i < skins.Length; i++) skins[i] = c.U32(); }
            else if (chunk == ChunkId.Lit("TXID")) { txid = new uint[size / 4]; for (int i = 0; i < txid.Length; i++) txid[i] = c.U32(); }
            c.Offset = next;
        }
        if (md20 < 0 || skins.Length == 0) throw new InvalidDataException("M2 without MD21 or skin");
        var m = new Cursor(bytes.AsSpan(md20, md20Size));
        m.Offset = 60; var verts = ((int)m.U32(), (int)m.U32());
        m.Offset = 112; var mats = ((int)m.U32(), (int)m.U32());
        m.Offset = 128; var combos = ((int)m.U32(), (int)m.U32());
        m.Offset = 96; var transforms = ((int)m.U32(), (int)m.U32());
        m.Offset = 80; var textureDefs = ((int)m.U32(), (int)m.U32());
        m.Offset = 152; var transformCombos = ((int)m.U32(), (int)m.U32());
        // Bones' resting transforms (first frame of the first sequence, see CharacterData.RestTransform), cached per bone.
        var md = bytes.AsSpan(md20, md20Size).ToArray();
        var rests = new Dictionary<int, System.Numerics.Matrix4x4>();
        System.Numerics.Matrix4x4 Rest(int bone) => rests.TryGetValue(bone, out var r) ? r : rests[bone] = CharacterData.RestTransform(md, bone);
        Vector3 Placed(Vector3 raw, int bone) => ModelData.Convert(Vector3.Transform(raw, Rest(bone)));
        var pos = new Vector3[verts.Item1]; var nrm = new Vector3[verts.Item1]; var uv = new Vector2[verts.Item1];
        for (int i = 0; i < verts.Item1; i++)
        {
            m.Offset = verts.Item2 + i * 48;
            var raw = m.V3(); m.Offset += 8; var normal = m.V3(); uv[i] = new Vector2(m.F32(), m.F32());
            // Static models are drawn as the client shows them standing: each vertex moved by its main bone's resting
            // pose (a lamppost's lantern hangs where its glow is). Animated models are posed by their own skeleton.
            int bone = md[verts.Item2 + i * 48 + 16];
            if (replaceableTexture is null && md[verts.Item2 + i * 48 + 12] > 0 && !Rest(bone).IsIdentity)
            {
                raw = Vector3.Transform(raw, Rest(bone));
                normal = Vector3.Normalize(Vector3.TransformNormal(normal, Rest(bone)));
            }
            pos[i] = ModelData.Convert(raw); nrm[i] = ModelData.Convert(normal);
        }
        var materials = new (ushort Flags, ushort Blend)[mats.Item1];
        for (int i = 0; i < mats.Item1; i++) { m.Offset = mats.Item2 + i * 4; materials[i] = (m.U16(), m.U16()); }
        var comboList = new ushort[combos.Item1];
        for (int i = 0; i < combos.Item1; i++) { m.Offset = combos.Item2 + i * 2; comboList[i] = m.U16(); }
        // SFID lists the skin profiles first, then the client's reduced LOD skins (_lod01, _lod02...).
        m.Offset = 0x44; int profiles = Math.Clamp((int)m.U32(), 1, skins.Length);
        var model = new ModelData { Type = "m2" };
        // Bones that turn to face the camera (spherical or cylindrical billboards: glows, flames, light cards), themselves or through a parent.
        bool Billboard(int bone)
        {
            // The flag sits on the bone or one of its parents (a glow card's bone hangs under the billboard bone).
            int count = BitConverter.ToInt32(md, 0x2C), offset = BitConverter.ToInt32(md, 0x30);
            for (int guard = 0; bone >= 0 && bone < count && offset + (bone + 1) * 88 <= md.Length && guard < 64; guard++)
            {
                if ((BitConverter.ToUInt32(md, offset + bone * 88 + 4) & 0x78) != 0) return true;
                bone = BitConverter.ToInt16(md, offset + bone * 88 + 8);
            }
            return false;
        }
        var skinFiles = new List<(uint File, int Lod)> { (skins[0], 0) };
        for (int k = profiles; k < skins.Length && k - profiles < 3; k++) skinFiles.Add((skins[k], k - profiles + 1));
        foreach (var (skinFile, lod) in skinFiles)
        {
        var skin = read(skinFile);
        if (skin is null) { if (lod == 0) throw new FileNotFoundException($"Skin {skinFile} is not installed"); continue; }
        var s = new Cursor(skin);
        if (s.U32() != ChunkId.Lit("SKIN")) { if (lod == 0) throw new InvalidDataException("Invalid skin"); continue; }
        int indicesCount = (int)s.U32(), indicesOfs = (int)s.U32(), trianglesCount = (int)s.U32(), trianglesOfs = (int)s.U32();
        s.U32(); s.U32(); int subCount = (int)s.U32(), subOfs = (int)s.U32(), unitCount = (int)s.U32(), unitOfs = (int)s.U32();
        var lookup = new ushort[indicesCount]; s.Offset = indicesOfs; for (int i = 0; i < indicesCount; i++) lookup[i] = s.U16();
        var triangles = new ushort[trianglesCount]; s.Offset = trianglesOfs; for (int i = 0; i < trianglesCount; i++) triangles[i] = s.U16();
        var units = new (ushort Section, ushort Material, ushort Combo, ushort Transform, short Color, ushort Weight)[unitCount];
        for (int i = 0; i < unitCount; i++)
        {
            s.Offset = unitOfs + i * 24 + 4; ushort section = s.U16(); s.U16(); short color = (short)s.U16(); ushort material = s.U16(); s.U16(); s.U16(); ushort combo = s.U16();
            s.Offset = unitOfs + i * 24 + 20; ushort weight = s.U16(); units[i] = (section, material, combo, s.U16(), color, weight);
        }
        for (int i = 0; i < subCount; i++)
        {
            s.Offset = subOfs + i * 48; int geosetId = s.U16(); int level = s.U16(); s.U16(); s.U16(); int triStart = s.U16() + (level << 16); int triCount = s.U16();
            if (geoset is not null && !geoset(geosetId)) continue;
            if (triCount == 0) continue;
            int u = Array.FindIndex(units, x => x.Section == i);
            int texIndex = u >= 0 && units[u].Combo < comboList.Length ? comboList[units[u].Combo] : 0;
            uint tex = texIndex < txid.Length ? txid[texIndex] : 0;
            int textureType = 0;
            if (tex == 0 && replaceableTexture is not null && texIndex < textureDefs.Item1)
            {
                m.Offset = textureDefs.Item2 + texIndex * 16;
                textureType = (int)m.U32();
                tex = replaceableTexture(textureType);
            }
            if (tex == 0 && TextureFallback.TryGetValue(id, out var fallback)) tex = fallback;
            if (tex == 0) { if (lod == 0) model.Unresolved++; continue; }
            var mat = u >= 0 && units[u].Material < materials.Length ? materials[units[u].Material] : ((ushort)0, (ushort)0);
            // Cards the client draws nearly invisible (fog, light shafts, water sheets of the login and creation scenes,
            // through texture weights and colour alpha) would show as solid planes: they are left out.
            float opacity = u >= 0 ? Opacity(bytes.AsSpan(md20, md20Size), units[u].Color, units[u].Weight) : 1;
            bool faint = opacity < 0.05f || (opacity < 0.4f && mat.Item2 >= 2);
            var indices = new int[triCount];
            for (int n = 0; n < triCount; n++) indices[n] = lookup[triangles[triStart + n]];
            // A card on a billboard bone faces the camera in the client: drawn flat it shows its edge and cuts the glow.
            int vertexBone = bytes[md20 + verts.Item2 + indices[0] * 48 + 16];
            // Static world models only: animated ones (characters, creatures, spell effects) keep their cards on their bones.
            if (replaceableTexture is null && mat.Item2 >= 2 && Billboard(vertexBone))
            {
                if (lod == 0 && opacity >= 0.05f)
                {
                    var corners = indices.Select(v => pos[v]).ToArray();
                    var centre = corners.Aggregate(Vector3.Zero, (a, b) => a + b) / corners.Length;
                    var extent = corners.Aggregate(Vector3.Zero, (a, b) => Vector3.Max(a, Vector3.Abs(b - centre)));
                    model.Billboards.Add(new ModelData.Billboard
                    {
                        Position = centre, Size = 2 * Math.Max(extent.X, Math.Max(extent.Y, extent.Z)),
                        Texture = tex, Blend = mat.Item2, Opacity = Math.Min(1, opacity), Unlit = (mat.Item1 & 1) != 0,
                    });
                }
                continue;
            }
            var part = Compact(pos, nrm, uv, indices);
            if (replaceableTexture is not null) part.SourceVertices = indices.Distinct().ToArray();
            part.Texture = tex; part.Blend = mat.Item2; part.Flags = mat.Item1; part.Geoset = geosetId; part.TextureType = textureType;
            if (u >= 0 && units[u].Transform < transformCombos.Item1)
            {
                m.Offset = transformCombos.Item2 + units[u].Transform * 2; int transform = m.I16();
                if (transform >= 0 && transform < transforms.Item1) part.Scroll = Scroll(bytes.AsSpan(md20, md20Size), transforms.Item2 + transform * 60);
            }
            part.Lod = lod;
            if (faint) { if (lod == 0) model.Faint.Add(part); continue; }
            model.Parts.Add(part); if (lod == 0) model.Grow(part.Positions);
        }
        }
        try { ReadEmitters(model, bytes.AsSpan(md20, md20Size), txid, Placed); } catch (ArgumentOutOfRangeException) { model.Emitters.Clear(); }
        try { ReadRibbons(model, bytes.AsSpan(md20, md20Size), txid, materials, Placed); } catch (ArgumentOutOfRangeException) { model.Ribbons.Clear(); }
        try { ReadCameras(model, bytes.AsSpan(md20, md20Size)); } catch (ArgumentOutOfRangeException) { model.Cameras.Clear(); }
        try { ReadLights(model, bytes.AsSpan(md20, md20Size), Placed); } catch (ArgumentOutOfRangeException) { model.Lights.Clear(); }
        try
        {
            m.Offset = 0xD8; int indexCount = (int)m.U32(), indexOfs = (int)m.U32(); int pointCount = (int)m.U32(), pointOfs = (int)m.U32();
            if (indexCount >= 3 && pointCount >= 3)
            {
                var points = new Vector3[pointCount]; m.Offset = pointOfs;
                for (int i = 0; i < pointCount; i++) points[i] = ModelData.Convert(m.V3());
                var tris = new int[indexCount - indexCount % 3]; m.Offset = indexOfs;
                for (int i = 0; i < tris.Length; i++) { tris[i] = m.U16(); if (tris[i] >= pointCount) throw new ArgumentOutOfRangeException(); }
                model.CollisionPositions = points; model.CollisionIndices = tris;
            }
        }
        catch (ArgumentOutOfRangeException) { model.CollisionPositions = Array.Empty<Vector3>(); model.CollisionIndices = Array.Empty<int>(); }
        return model;
    }

    // M2Particle (Wrath+ layout, wowdev.wiki): the record size depends on the version, so it is found from particleId == -1.
    private static void ReadEmitters(ModelData model, ReadOnlySpan<byte> md20, uint[] txid, Func<Vector3, int, Vector3> placed)
    {
        var c = new Cursor(md20, 0x128); int count = (int)c.U32(), offset = (int)c.U32();
        if (count <= 0 || count > 64 || offset <= 0) return;
        int stride = 0;
        foreach (int candidate in new[] { 508, 492, 476 })
        {
            bool ok = true;
            for (int i = 0; ok && i < count; i++) ok = offset + i * candidate + 4 <= md20.Length && BitConverter.ToInt32(md20.Slice(offset + i * candidate, 4)) == -1;
            if (ok) { stride = candidate; break; }
        }
        if (stride == 0) return;
        for (int i = 0; i < count; i++)
        {
            int b = offset + i * stride;
            c.Offset = b + 4; uint flags = c.U32(); var pos = c.V3(); int bone = c.U16(); ushort texField = c.U16();
            int texIndex = (flags & 0x10000000) != 0 ? texField & 31 : texField;
            c.Offset = b + 0x28; int blend = c.U8(); int emitterType = c.U8();
            if (emitterType == 3 || emitterType == 4) continue; // splines and bone generators need the skeleton
            c.Offset = b + 0x30; int rows = c.U16(), columns = c.U16();
            // A model name of one character is the empty string: only longer names are model particles.
            c.Offset = b + 0x18; bool hasModel = c.U32() > 1;
            if (hasModel || texIndex >= txid.Length || txid[texIndex] == 0) continue; // model particles are not drawn
            var e = new ModelData.Emitter
            {
                Position = ModelData.Convert(pos), Placed = placed(pos, bone), Texture = txid[texIndex], Blend = blend, Flags = flags, Bone = bone,
                Rows = Math.Clamp(rows, 1, 16), Columns = Math.Clamp(columns, 1, 16),
                Speed = First(md20, b + 0x34), SpeedVariation = First(md20, b + 0x48), VerticalRange = First(md20, b + 0x5C), HorizontalRange = First(md20, b + 0x70),
                Gravity = (flags & 0x800000) != 0 ? 0 : First(md20, b + 0x84), Lifespan = First(md20, b + 0x98), Rate = First(md20, b + 0xB0),
                AreaWidth = First(md20, b + 0xC8), AreaLength = First(md20, b + 0xDC),
            };
            var colors = Fake(md20, b + 0x104, 12); var alpha = Fake(md20, b + 0x114, 2); var scale = Fake(md20, b + 0x124, 8);
            for (int k = 0; k < 3; k++)
            {
                if (colors.Count > 0) { int at = colors[Math.Min(k, colors.Count - 1)]; e.Colors[k] = new Vector3(BitConverter.ToSingle(md20.Slice(at, 4)), BitConverter.ToSingle(md20.Slice(at + 4, 4)), BitConverter.ToSingle(md20.Slice(at + 8, 4))) / 255f; }
                if (alpha.Count > 0) e.Alpha[k] = BitConverter.ToInt16(md20.Slice(alpha[Math.Min(k, alpha.Count - 1)], 2)) / 32767f;
                if (scale.Count > 0) e.Scale[k] = BitConverter.ToSingle(md20.Slice(scale[Math.Min(k, scale.Count - 1)], 4));
            }
            if (!float.IsFinite(e.Lifespan) || e.Lifespan <= 0 || e.Lifespan > 60 || !float.IsFinite(e.Rate) || e.Rate <= 0) continue;
            model.Emitters.Add(e);
        }
    }

    // M2RibbonEmitter (Wrath+ layout, 176 bytes, ribbonId == -1): bone, position, texture and material, then tracks.
    private static void ReadRibbons(ModelData model, ReadOnlySpan<byte> md20, uint[] txid, (ushort Flags, ushort Blend)[] materials, Func<Vector3, int, Vector3> placed)
    {
        const int Stride = 0xB0;
        var c = new Cursor(md20, 0x120); int count = (int)c.U32(), offset = (int)c.U32();
        if (count <= 0 || count > 32 || offset <= 0 || offset + count * Stride > md20.Length) return;
        for (int i = 0; i < count; i++)
        {
            int b = offset + i * Stride;
            if (BitConverter.ToInt32(md20.Slice(b, 4)) != -1) return;
            c.Offset = b + 4; int bone = (int)c.U32(); var pos = c.V3();
            c.Offset = b + 0x14; int texCount = (int)c.U32(), texOfs = (int)c.U32(); int matCount = (int)c.U32(), matOfs = (int)c.U32();
            if (texCount == 0) continue;
            c.Offset = texOfs; int tex = c.U16();
            int blend = 0;
            if (matCount > 0) { c.Offset = matOfs; int mat = c.U16(); if (mat < materials.Length) blend = materials[mat].Blend; }
            if (tex >= txid.Length || txid[tex] == 0) continue;
            var r = new ModelData.Ribbon
            {
                Position = ModelData.Convert(pos), Placed = placed(pos, bone), Texture = txid[tex], Blend = blend, Bone = bone,
                Above = First(md20, b + 0x4C), Below = First(md20, b + 0x60),
                EdgesPerSecond = BitConverter.ToSingle(md20.Slice(b + 0x74, 4)), Lifetime = BitConverter.ToSingle(md20.Slice(b + 0x78, 4)),
                Gravity = BitConverter.ToSingle(md20.Slice(b + 0x7C, 4)),
            };
            var color = Keys(md20, b + 0x24, 12);
            if (color >= 0) r.Color = new Vector3(BitConverter.ToSingle(md20.Slice(color, 4)), BitConverter.ToSingle(md20.Slice(color + 4, 4)), BitConverter.ToSingle(md20.Slice(color + 8, 4)));
            var alpha = Keys(md20, b + 0x38, 2);
            if (alpha >= 0) r.Alpha = Math.Clamp(BitConverter.ToInt16(md20.Slice(alpha, 2)) / 32767f, 0, 1);
            if (!float.IsFinite(r.Lifetime) || r.Lifetime <= 0 || r.Lifetime > 30 || r.Above + r.Below <= 0) continue;
            model.Ribbons.Add(r);
        }
    }

    /// <summary>Resting opacity of a texture unit: its texture weight (transparency lookup → texture weights) times its
    /// colour's alpha, first keys of the first sequences.</summary>
    private static float Opacity(ReadOnlySpan<byte> md20, short color, ushort weightCombo)
    {
        try
        {
            float alpha = 1;
            var c = new Cursor(md20, 0x90); int lookups = (int)c.U32(), lookupOfs = (int)c.U32();
            c.Offset = 0x58; int weights = (int)c.U32(), weightOfs = (int)c.U32();
            if (weightCombo < lookups)
            {
                int index = BitConverter.ToUInt16(md20.Slice(lookupOfs + weightCombo * 2, 2));
                if (index < weights && Keys(md20, weightOfs + index * 20, 2) is var at and >= 0) alpha *= Math.Clamp(BitConverter.ToInt16(md20.Slice(at, 2)) / 32767f, 0, 1);
            }
            c.Offset = 0x48; int colors = (int)c.U32(), colorOfs = (int)c.U32();
            if (color >= 0 && color < colors && Keys(md20, colorOfs + color * 40 + 20, 2) is var a and >= 0) alpha *= Math.Clamp(BitConverter.ToInt16(md20.Slice(a, 2)) / 32767f, 0, 1);
            return alpha;
        }
        catch (ArgumentOutOfRangeException) { return 1; }
    }

    // M2Camera (Cataclysm+ layout, 116 bytes): type, far and near clips, position track (+12) and base (+32), target
    // track (+44) and base (+64), roll (+76), field of view (+96).
    private static void ReadCameras(ModelData model, ReadOnlySpan<byte> md20)
    {
        var c = new Cursor(md20, 0x110); int count = (int)c.U32(), offset = (int)c.U32();
        if (count <= 0 || count > 8 || offset <= 0 || offset + count * 116 > md20.Length) return;
        for (int i = 0; i < count; i++)
        {
            int b = offset + i * 116;
            c.Offset = b + 32; var positionBase = c.V3(); c.Offset = b + 64; var targetBase = c.V3();
            int fov = Keys(md20, b + 96, 4);
            model.Cameras.Add(new ModelData.Camera
            {
                Type = BitConverter.ToInt32(md20.Slice(b, 4)),
                // Scenes fly their camera in: the resting view is the last key of the first animation (spline keys, 36 bytes).
                Position = ModelData.Convert(positionBase + LastSplineKey(md20, b + 12)), Target = ModelData.Convert(targetBase + LastSplineKey(md20, b + 44)),
                Fov = fov < 0 ? 0.7f : BitConverter.ToSingle(md20.Slice(fov, 4)),
            });
        }
    }

    // M2Light (156 bytes): type, bone, position, then tracks: ambient colour and intensity, diffuse colour and intensity,
    // attenuation start and end, visibility. First keys of the first sequences.
    private static void ReadLights(ModelData model, ReadOnlySpan<byte> md20, Func<Vector3, int, Vector3> placed)
    {
        var c = new Cursor(md20, 0x108); int count = (int)c.U32(), offset = (int)c.U32();
        if (count <= 0 || count > 16 || offset <= 0 || offset + count * 156 > md20.Length) return;
        for (int i = 0; i < count; i++)
        {
            int b = offset + i * 156;
            c.Offset = b + 4; var position = c.V3();
            model.Lights.Add(new ModelData.Light
            {
                Type = BitConverter.ToUInt16(md20.Slice(b, 2)), Position = placed(position, BitConverter.ToInt16(md20.Slice(b + 2, 2))),
                Ambient = TrackColor(md20, b + 16) * TrackValue(md20, b + 36, 1), Diffuse = TrackColor(md20, b + 56) * TrackValue(md20, b + 76, 1),
                Start = TrackValue(md20, b + 96, 0), End = TrackValue(md20, b + 116, 10),
            });
        }
    }

    private static Vector3 TrackColor(ReadOnlySpan<byte> md20, int track)
    {
        int at = Keys(md20, track, 12);
        return at < 0 ? Vector3.Zero : new Vector3(BitConverter.ToSingle(md20.Slice(at, 4)), BitConverter.ToSingle(md20.Slice(at + 4, 4)), BitConverter.ToSingle(md20.Slice(at + 8, 4)));
    }

    private static float TrackValue(ReadOnlySpan<byte> md20, int track, float fallback)
    {
        int at = Keys(md20, track, 4);
        return at < 0 ? fallback : BitConverter.ToSingle(md20.Slice(at, 4));
    }

    private static Vector3 LastSplineKey(ReadOnlySpan<byte> md20, int track)
    {
        var c = new Cursor(md20, track + 12); int n = (int)c.U32(), ofs = (int)c.U32();
        for (int sequence = 0; sequence < n && sequence < 8; sequence++)
        {
            c.Offset = ofs + sequence * 8; int k = (int)c.U32(), values = (int)c.U32();
            if (k <= 0 || values <= 0 || values + k * 36 > md20.Length) continue;
            int at = values + (k - 1) * 36;
            return new Vector3(BitConverter.ToSingle(md20.Slice(at, 4)), BitConverter.ToSingle(md20.Slice(at + 4, 4)), BitConverter.ToSingle(md20.Slice(at + 8, 4)));
        }
        return Vector3.Zero;
    }

    /// <summary>Offset of the first key of an M2Track (first sequence that has one), -1 when none.</summary>
    private static int Keys(ReadOnlySpan<byte> md20, int track, int size)
    {
        var c = new Cursor(md20, track + 12); int n = (int)c.U32(), ofs = (int)c.U32();
        for (int sequence = 0; sequence < n && sequence < 8; sequence++)
        {
            c.Offset = ofs + sequence * 8; int k = (int)c.U32(), values = (int)c.U32();
            if (k > 0 && values > 0 && values + size <= md20.Length) return values;
        }
        return -1;
    }

    /// <summary>First value of an M2Track&lt;float&gt; (global sequence or first animation), 0 when empty.</summary>
    private static float First(ReadOnlySpan<byte> md20, int track)
    {
        var c = new Cursor(md20, track + 12); int n = (int)c.U32(), ofs = (int)c.U32();
        if (n == 0) return 0;
        // Many spell effects animate their values per sequence and start at zero in the first one:
        // the first non-zero key of the first sequences stands for the emitter.
        for (int sequence = 0; sequence < n && sequence < 8; sequence++)
        {
            c.Offset = ofs + sequence * 8; int k = (int)c.U32(), values = (int)c.U32();
            for (int key = 0; key < k && key < 16; key++)
            {
                if (values + key * 4 + 4 > md20.Length) break;
                float v = BitConverter.ToSingle(md20.Slice(values + key * 4, 4));
                if (float.IsFinite(v) && v != 0) return v;
            }
        }
        return 0;
    }

    /// <summary>Offsets of the keys of a fake animation block (count, timestamps, count, keys).</summary>
    private static List<int> Fake(ReadOnlySpan<byte> md20, int block, int size)
    {
        var c = new Cursor(md20, block + 8); int n = (int)c.U32(), ofs = (int)c.U32(); var list = new List<int>();
        for (int i = 0; i < n && i < 3; i++) if (ofs + (i + 1) * size <= md20.Length) list.Add(ofs + i * size);
        return list;
    }

    /// <summary>Mean speed of a texture transform's translation track (UV per second): the client's
    /// scrolling waterfalls, lava and glows are linear loops, from the global sequence or the first animation.</summary>
    private static Vector2 Scroll(ReadOnlySpan<byte> md20, int track)
    {
        try
        {
            var c = new Cursor(md20, track + 4);
            int timesCount = (int)c.U32(), timesOfs = (int)c.U32(), valuesCount = (int)c.U32(), valuesOfs = (int)c.U32();
            if (timesCount == 0 || valuesCount == 0) return Vector2.Zero;
            c.Offset = timesOfs; int n = (int)c.U32(), times = (int)c.U32();
            c.Offset = valuesOfs; int nv = (int)c.U32(), values = (int)c.U32();
            if (n < 2 || nv < n || times <= 0 || values <= 0 || times + n * 4 > md20.Length || values + n * 12 > md20.Length) return Vector2.Zero;
            c.Offset = times; uint t0 = c.U32(); c.Offset = times + (n - 1) * 4; uint t1 = c.U32();
            if (t1 <= t0) return Vector2.Zero;
            c.Offset = values; var v0 = c.V3(); c.Offset = values + (n - 1) * 12; var v1 = c.V3();
            var speed = new Vector2(v1.X - v0.X, v1.Y - v0.Y) * (1000f / (t1 - t0));
            return float.IsFinite(speed.X) && float.IsFinite(speed.Y) && speed.Length() < 50 ? speed : Vector2.Zero;
        }
        catch (ArgumentOutOfRangeException) { return Vector2.Zero; }
    }

    internal static ModelData.Part Compact(Vector3[] pos, Vector3[] nrm, Vector2[] uv, IList<int> indices)
    {
        var map = new Dictionary<int, int>(); var p = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<Vector2>(); var idx = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            int v = indices[i];
            if (!map.TryGetValue(v, out int k)) { k = p.Count; map[v] = k; p.Add(pos[v]); n.Add(nrm is null || v >= nrm.Length ? Vector3.UnitZ : nrm[v]); t.Add(uv is null || v >= uv.Length ? Vector2.Zero : uv[v]); }
            idx[i] = k;
        }
        return new ModelData.Part { Positions = p.ToArray(), Normals = n.ToArray(), Uv = t.ToArray(), Indices = idx };
    }
}

/// <summary>WMO root and groups with their render batches, doodads and interior liquids (adapted from wow.export, MIT).</summary>
public static class WmoReader
{
    public static ModelData Read(uint id, byte[] bytes, Func<uint, byte[]> read)
    {
        var c = new Cursor(bytes); uint flags = 0; int groupCount = 0, lodCount = 1; var materials = new List<(uint Flags, uint Blend, uint Texture)>();
        uint[] groups = Array.Empty<uint>(), doodadFiles = Array.Empty<uint>();
        var sets = new List<(int First, int Count)>(); var doodads = new List<(uint Offset, Vector3 Position, Quaternion Rotation, float Scale)>();
        while (c.More)
        {
            uint chunk = c.U32(); int size = (int)c.U32(); int next = c.Offset + size;
            if (chunk == ChunkId.Of("MOHD")) { int at = c.Offset; c.Offset = at + 4; groupCount = (int)c.U32(); c.Offset = at + 60; flags = c.U16(); lodCount = Math.Max(1, (int)c.U16()); }
            else if (chunk == ChunkId.Of("MOMT")) for (int i = 0; i < size / 64; i++) { uint f = c.U32(); c.U32(); uint blend = c.U32(); uint tex = c.U32(); c.Offset += 48; materials.Add((f, blend, tex)); }
            else if (chunk == ChunkId.Of("MODS")) for (int i = 0; i < size / 32; i++) { c.Offset += 20; sets.Add(((int)c.U32(), (int)c.U32())); c.U32(); }
            else if (chunk == ChunkId.Of("MODI")) { doodadFiles = new uint[size / 4]; for (int i = 0; i < doodadFiles.Length; i++) doodadFiles[i] = c.U32(); }
            else if (chunk == ChunkId.Of("MODD"))
                for (int i = 0; i < size / 40; i++)
                {
                    uint word = c.U32(); var p = c.V3(); var q = new Quaternion(c.F32(), c.F32(), c.F32(), c.F32()); float scale = c.F32(); c.U32();
                    doodads.Add((word & 0xFFFFFF, p, q, scale));
                }
            else if (chunk == ChunkId.Of("GFID")) { groups = new uint[size / 4]; for (int i = 0; i < groups.Length; i++) groups[i] = c.U32(); }
            c.Offset = next;
        }
        var model = new ModelData { Type = "wmo" };
        // GFID holds the full groups, then the client's LOD versions of them (lod k at k * groupCount + g).
        if (groupCount <= 0 || groupCount * lodCount > groups.Length) { groupCount = groups.Length; lodCount = 1; }
        for (int lod = 0; lod < lodCount && lod < 4; lod++)
            for (int g = 0; g < groupCount; g++)
            {
                uint file = groups[lod * groupCount + g];
                // A distant WMO level can omit individual groups. Keep the full group there instead of
                // making a roof, bridge or whole building disappear when switching levels.
                var data = file == 0 ? null : read(file);
                if (data is null && lod > 0 && groups[g] != 0) data = read(groups[g]);
                if (data is null) { if (lod == 0) model.Unresolved++; continue; }
                ReadGroup(model, data, flags, materials, lod);
            }
        for (int i = 0; i < doodads.Count; i++)
        {
            var d = doodads[i];
            uint file = d.Offset < doodadFiles.Length ? doodadFiles[d.Offset] : 0;
            if (file == 0) continue;
            var inSets = Enumerable.Range(0, sets.Count).Where(s => i >= sets[s].First && i < sets[s].First + sets[s].Count).ToArray();
            model.Doodads.Add(new ModelData.Doodad { Id = i, Model = file, Position = ModelData.Convert(d.Position),
                Rotation = new Quaternion(-d.Rotation.Y, d.Rotation.X, d.Rotation.Z, d.Rotation.W), Scale = d.Scale, Sets = inSets });
        }
        return model;
    }

    private static void ReadGroup(ModelData model, byte[] data, uint rootFlags, List<(uint Flags, uint Blend, uint Texture)> materials, int lod)
    {
        var c = new Cursor(data); Vector3[] pos = null, nrm = null; Vector2[] uv = null; int[] indices = null;
        ushort[] polygonFlags = null;
        var batches = new List<(int First, int Count, int Material)>(); uint groupFlags = 0, liquidType = 0;
        (int VX, int VY, int TX, int TY, Vector3 Corner, float[] Heights, byte[] Tiles)? liquid = null;
        while (c.More)
        {
            uint chunk = c.U32(); int size = (int)c.U32(); int next = c.Offset + size;
            if (chunk == ChunkId.Of("MOGP"))
            {
                int start = c.Offset; c.U32(); c.U32(); groupFlags = c.U32(); c.Offset = start + 52; liquidType = c.U32();
                c.Offset = start + 68;
                while (c.Offset + 8 <= next)
                {
                    uint sub = c.U32(); int subSize = (int)c.U32(); int subNext = c.Offset + subSize;
                    if (sub == ChunkId.Of("MOVT")) { pos = new Vector3[subSize / 12]; for (int i = 0; i < pos.Length; i++) pos[i] = ModelData.Convert(c.V3()); }
                    else if (sub == ChunkId.Of("MONR")) { nrm = new Vector3[subSize / 12]; for (int i = 0; i < nrm.Length; i++) nrm[i] = ModelData.Convert(c.V3()); }
                    else if (sub == ChunkId.Of("MOTV") && uv is null) { uv = new Vector2[subSize / 8]; for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(c.F32(), c.F32()); }
                    else if (sub == ChunkId.Of("MOVI")) { indices = new int[subSize / 2]; for (int i = 0; i < indices.Length; i++) indices[i] = c.U16(); }
                    else if (sub == ChunkId.Of("MOVX")) { indices = new int[subSize / 4]; for (int i = 0; i < indices.Length; i++) indices[i] = checked((int)c.U32()); }
                    else if (sub == ChunkId.Of("MOPY")) { polygonFlags = new ushort[subSize / 2]; for (int i = 0; i < polygonFlags.Length; i++) { polygonFlags[i] = c.U8(); c.U8(); } }
                    // Forever's rebuilt cities use the expanded 16-bit flag/material pair (MPY2).
                    else if (sub == ChunkId.Of("MPY2")) { polygonFlags = new ushort[subSize / 4]; for (int i = 0; i < polygonFlags.Length; i++) { polygonFlags[i] = c.U16(); c.U16(); } }
                    else if (sub == ChunkId.Of("MOBA"))
                        for (int i = 0; i < subSize / 24; i++)
                        {
                            int at = c.Offset; c.Offset = at + 10; ushort box2 = c.U16(); uint first = c.U32(); ushort count = c.U16(); c.U16(); c.U16(); byte f = c.U8(); byte mat = c.U8();
                            batches.Add(((int)first, count, (f & 2) != 0 ? box2 : mat)); c.Offset = at + 24;
                        }
                    else if (sub == ChunkId.Of("MLIQ"))
                    {
                        int vx = (int)c.U32(), vy = (int)c.U32(), tx = (int)c.U32(), ty = (int)c.U32(); var corner = c.V3(); c.U16();
                        var heights = new float[vx * vy]; for (int i = 0; i < heights.Length; i++) { c.U32(); heights[i] = c.F32(); }
                        liquid = (vx, vy, tx, ty, corner, heights, c.Bytes(tx * ty).ToArray());
                    }
                    c.Offset = subNext;
                }
            }
            c.Offset = next;
        }
        // Collision-only triangles are absent from render batches. Preserve the actual WMO collision
        // flags instead of turning every visible decoration into a wall (MOPY: collision=8, render=32, detail=4).
        if (lod == 0 && pos is not null && indices is not null && polygonFlags is not null && (groupFlags & 0x4000080) == 0)
        {
            var solid = new List<int>();
            for (int t = 0; t < polygonFlags.Length && t * 3 + 2 < indices.Length; t++)
            {
                ushort f = polygonFlags[t];
                if ((f & 8) == 0 && ((f & 32) == 0 || (f & 4) != 0)) continue;
                for (int k = 0; k < 3; k++) solid.Add(indices[t * 3 + k]);
            }
            var unique = solid.Distinct().ToArray();
            int start = model.CollisionPositions.Length;
            var remap = unique.Select((index, local) => (index, local)).ToDictionary(x => x.index, x => start + x.local);
            model.CollisionPositions = model.CollisionPositions.Concat(unique.Select(i => pos[i])).ToArray();
            model.CollisionIndices = model.CollisionIndices.Concat(solid.Select(i => remap[i])).ToArray();
        }
        if (pos is not null && indices is not null)
            foreach (var b in batches)
            {
                if (b.Count == 0 || b.Material >= materials.Count) continue;
                var m = materials[b.Material];
                if (m.Texture == 0) { model.Unresolved++; continue; }
                var part = M2Reader.Compact(pos, nrm, uv, indices.Skip(b.First).Take(b.Count).Select(x => (int)x).ToArray());
                part.Texture = m.Texture; part.Blend = (int)m.Blend; part.Flags = m.Flags; part.Collision = lod == 0; part.Lod = lod;
                model.Parts.Add(part); if (lod == 0) model.Grow(part.Positions);
            }
        if (liquid is { } l && lod == 0)
        {
            var meshes = new Dictionary<int, (List<Vector3> P, List<Vector2> T, List<int> I)>();
            for (int y = 0; y < l.TY; y++)
                for (int x = 0; x < l.TX; x++)
                {
                    int tile = l.Tiles[y * l.TX + x]; if ((tile & 15) == 15) continue;
                    int type = LiquidType(rootFlags, groupFlags, liquidType, tile);
                    if (!meshes.TryGetValue(type, out var mesh)) meshes[type] = mesh = (new(), new(), new());
                    int b0 = mesh.P.Count;
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                    {
                        int vx = x + dx, vy = y + dy;
                        float rx = l.Corner.X + vx * TerrainTile.Unit, ry = l.Corner.Y + vy * TerrainTile.Unit;
                        mesh.P.Add(new Vector3(-ry, rx, l.Heights[vy * l.VX + vx])); mesh.T.Add(new Vector2(vx / 4f, vy / 4f));
                    }
                    mesh.I.AddRange(new[] { b0, b0 + 1, b0 + 2, b0 + 1, b0 + 3, b0 + 2 });
                }
            foreach (var (type, mesh) in meshes)
                model.Parts.Add(new ModelData.Part { Positions = mesh.P.ToArray(), Normals = Enumerable.Repeat(Vector3.UnitZ, mesh.P.Count).ToArray(), Uv = mesh.T.ToArray(), Indices = mesh.I.ToArray(), Liquid = type });
        }
    }

    /// <summary>Same mapping as the client's WMO liquid rules (also in TrinityCore's vmap reader).</summary>
    public static int LiquidType(uint rootFlags, uint groupFlags, uint groupLiquid, int tile)
    {
        int id = (rootFlags & 4) != 0 ? (int)groupLiquid : groupLiquid == 15 ? 0 : (int)groupLiquid + 1;
        if (id == 0) id = (tile & 15) + 1;
        if (id > 0 && id < 21)
        {
            int category = (id - 1) & 3;
            id = category == 0 ? 13 + ((groupFlags & 0x80000) != 0 ? 1 : 0) : category == 1 ? 14 : category == 2 ? 19 : 20;
        }
        return id;
    }
}
