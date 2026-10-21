using System.Numerics;

namespace MiniBox.WowForever;

using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>M2 skeleton and sampled local bone poses. Wire layout follows wow.export (MIT),
/// M2Generics/M2Loader/SKELLoader: modern 88-byte bones and 64-byte sequences.</summary>
public sealed class CharacterData
{
    public sealed record Bone(int Parent, Vector3 Pivot);
    public sealed record Pose(Vector3 Position, Quaternion Rotation, Vector3 Scale);
    public sealed record Clip(string Name, float FrameRate, bool Loop, Pose[][] Frames);
    public ModelData Geometry;
    public Bone[] Bones;
    public byte[][] Weights, BoneIndices;
    public List<Clip> Clips = new();
    /// <summary>The client's attachment points (id 0 is where a rider sits on a mount), on their bone.</summary>
    public List<(int Id, int Bone, Vector3 Position)> Attachments = new();

    /// <summary>Animations a player character needs (sequence id, exported name, looping).</summary>
    public static readonly (int Id, string Name, bool Loop)[] Locomotion =
        { (0,"idle",true), (4,"walk",true), (5,"run",true), (13,"walk_back",true), (37,"jump",false), (38,"fall",true), (39,"land",false), (41,"swim_idle",true), (42,"swim",true), (187,"crouch_walk",true), (91,"mount",true), (51,"spell_ready",true), (52,"cast",false) };

    /// <summary>What creatures, NPCs and mounts play: locomotion, combat, casting, death, flight and riding.</summary>
    public static readonly (int Id, string Name, bool Loop)[] Creature =
        { (0,"idle",true), (4,"walk",true), (5,"run",true), (13,"walk_back",true), (11,"turn_left",true), (12,"turn_right",true),
          (16,"attack_unarmed",false), (17,"attack_1h",false), (18,"attack_2h",false), (25,"ready",true), (26,"ready_1h",true),
          (1,"death",false), (6,"dead",true), (51,"spell_ready",true), (52,"cast",false), (53,"cast_omni",false), (54,"channel",true),
          (7,"spawn",false), (37,"jump",false), (38,"fall",true), (39,"land",false), (41,"swim_idle",true), (42,"swim",true),
          (8,"wound",false), (60,"talk",false), (66,"bow",false), (67,"wave",false), (84,"point",false), (32,"spell",false),
          (61,"eat",true), (62,"work",true), (63,"use_standing",true), (69,"dance",true), (97,"sit",true), (100,"sleep",true), (114,"kneel",true),
          (158,"fly_idle",true), (159,"fly",true), (135,"fly_stand",true), (91,"mount",true), (143,"run_mount",true), (124,"emote_idle",true) };

    public static CharacterData Read(uint id, byte[] file, Func<uint, byte[]> read, uint skin, uint hair = 0)
        => Read(id, file, read, type => type == 1 ? skin : type == 6 ? hair : 0, null, Locomotion, true);

    /// <summary>The unequipped character: body, scalp, the x01 variant of each group (not 15 the cloak, 17 and 35), and the head
    /// that the model has (newer files split the full head 3202 from the jaw-only 3201).</summary>
    public static Func<int, bool> DefaultGeosets(bool fullHead) => geoset => geoset / 100 == 32 ? geoset == (fullHead ? 3202 : 3201)
        : geoset is 0 or 1 || (geoset % 100 == 1 && geoset / 100 is not (15 or 17 or 35));

    /// <summary>Any skinned M2: textures from the replaceable resolver, visible geosets (null: the character rule),
    /// the requested sequences; locomotion is only mandatory for player characters.</summary>
    public static CharacterData Read(uint id, byte[] file, Func<uint, byte[]> read, Func<int, uint> textures, Func<int, bool> geosets,
        (int Id, string Name, bool Loop)[] selected, bool requireLocomotion, Func<int, string> otherNames = null)
    {
        var md = Chunk(file, "MD21") ?? throw new InvalidDataException("Character has no MD21");
        var skeleton = md;
        var sequenceData = md;
        int boneHeader = 44, sequenceHeader = 28, globalHeader = 20;
        var afid = Chunk(file, "AFID");
        var sk = Chunk(file, "SKID");
        var visited = new HashSet<uint>();
        byte[] skeletonAttachments = null;
        while (sk is { Length: >= 4 })
        {
            uint skId = U(sk, 0);
            if (!visited.Add(skId) || visited.Count > 8) throw new InvalidDataException("Skeleton parent cycle");
            var data = read(skId) ?? throw new FileNotFoundException($"Skeleton {skId}");
            // Parent skeleton supplies shared animations; the child keeps its own bones when present.
            if (Chunk(data, "SKB1") is { } bones && (boneHeader == 44 || skeleton == md)) { skeleton = bones; boneHeader = 0; }
            if (Chunk(data, "SKS1") is { } sequences) { sequenceData = sequences; sequenceHeader = 8; globalHeader = 0; }
            afid = Chunk(data, "AFID") ?? afid;
            skeletonAttachments ??= Chunk(data, "SKA1"); // newer models keep their attachment points in the skeleton
            var parent = Chunk(data, "SKPD");
            sk = parent is { Length: >= 12 } && U(parent, 8) != 0 ? parent[8..12] : null;
        }
        var (boneCount, boneOffset) = Array(skeleton, boneHeader, 88);
        if (boneCount == 0 || boneCount > 1024) throw new InvalidDataException("Invalid skeleton size");
        var result = new CharacterData { Bones = new Bone[boneCount] };
        for (int b = 0; b < boneCount; b++)
        {
            int o = boneOffset + b * 88;
            int parent = I16(skeleton, o + 8);
            if (parent >= b || parent < -1) throw new InvalidDataException("Unordered or cyclic skeleton");
            result.Bones[b] = new Bone(parent, ModelData.Convert(V(skeleton, o + 76)));
        }
        var attachmentData = md; var (attachmentCount, attachments) = Array(md, 0xF0, 40);
        if (attachmentCount == 0 && skeletonAttachments is { Length: >= 8 }) { attachmentData = skeletonAttachments; (attachmentCount, attachments) = Array(skeletonAttachments, 0, 40); }
        for (int a = 0; a < attachmentCount; a++)
        {
            int o = attachments + a * 40; int bone = U16(attachmentData, o + 4);
            if (bone < boneCount) result.Attachments.Add(((int)U(attachmentData, o), bone, ModelData.Convert(V(attachmentData, o + 8))));
        }
        var (vertexCount, vertices) = Array(md, 60, 48);
        result.Weights = new byte[vertexCount][]; result.BoneIndices = new byte[vertexCount][];
        for (int v = 0; v < vertexCount; v++)
        {
            result.Weights[v] = md[(vertices + v * 48 + 12)..(vertices + v * 48 + 16)];
            result.BoneIndices[v] = md[(vertices + v * 48 + 16)..(vertices + v * 48 + 20)];
            for (int k = 0; k < 4; k++)
                if (result.Weights[v][k] > 0 && result.BoneIndices[v][k] >= boneCount) throw new InvalidDataException("Vertex bone outside skeleton");
        }
        var skinFiles = Chunk(file, "SFID") ?? throw new InvalidDataException("Missing skin profiles");
        var profile = read(U(skinFiles, 0)) ?? throw new FileNotFoundException("Character skin");
        var (sections, sectionOffset) = Array(profile, 28, 48);
        // 01 is the unequipped choice; choosing the smallest existing entry also enabled armor-only skirts.
        // Geoset 1 closes the scalp on the classic bald appearance. Eyes/equipment overlays need extra textures.
        // Newer character files split the full head (3202) from the small jaw-only variant (3201).
        bool fullHead = Enumerable.Range(0, sections).Any(i => U16(profile, sectionOffset + i * 48) == 3202);
        result.Geometry = M2Reader.Read(id, file, read, textures, geosets ?? DefaultGeosets(fullHead));
        if (result.Geometry.Parts.Count == 0 && requireLocomotion) throw new InvalidDataException("Character has no textured body");

        var (sequenceCount, sequenceOffset) = Array(sequenceData, sequenceHeader, 64);
        foreach (var (animationId, name, loop) in selected)
        {
            int sequence = -1;
            for (int a = 0; a < sequenceCount; a++) if (U16(sequenceData, sequenceOffset + a * 64) == animationId && U16(sequenceData, sequenceOffset + a * 64 + 2) == 0) { sequence = a; break; }
            if (sequence >= 0) Bake(sequence, name, loop);
        }
        // Every other animation of the model (first variation), named by otherNames, looping: the sandbox plays them all.
        if (otherNames is not null)
        {
            var known = selected.Select(s => s.Id).ToHashSet();
            for (int a = 0; a < sequenceCount; a++)
            {
                int animation = U16(sequenceData, sequenceOffset + a * 64);
                if (U16(sequenceData, sequenceOffset + a * 64 + 2) != 0 || !known.Add(animation)) continue;
                Bake(a, otherNames(animation), true);
            }
        }
        if (requireLocomotion)
            foreach (string required in new[]{"idle","walk","run"})
                if (!result.Clips.Any(c=>c.Name==required)) throw new InvalidDataException("Missing locomotion: " + required);
        return result;

        void Bake(int sequence, string name, bool loop)
        {
            var aliases = new HashSet<int>();
            while ((U(sequenceData, sequenceOffset + sequence * 64 + 12) & 64) != 0)
            {
                if (!aliases.Add(sequence)) throw new InvalidDataException("Animation alias cycle");
                sequence = U16(sequenceData, sequenceOffset + sequence * 64 + 62);
                if (sequence >= sequenceCount) throw new InvalidDataException("Animation alias outside sequences");
            }
            int seq = sequenceOffset + sequence * 64;
            uint duration = U(sequenceData, seq + 4);
            if (duration == 0 || duration > 60000) return;
            byte[] external = null;
            if ((U(sequenceData, seq + 12) & 32) == 0)
            {
                if (afid is null) return;
                for (int a = 0; a + 8 <= afid.Length; a += 8)
                    if (U16(afid,a) == U16(sequenceData,seq) && U16(afid,a+2) == U16(sequenceData,seq+2))
                    { external = read(U(afid,a+4)); break; }
                if (external is null) return;
                external = Chunk(external,"AFSB") ?? external;
            }
            int count = Math.Max(2, (int)Math.Ceiling(duration / 1000f * 24) + 1);
            var frames = new Pose[count][];
            for (int f = 0; f < count; f++)
            {
                float ms = duration * f / (float)(count - 1); frames[f] = new Pose[boneCount];
                for (int b = 0; b < boneCount; b++)
                {
                    int o = boneOffset + b * 88;
                    Vector3 bind = result.Bones[b].Pivot - (result.Bones[b].Parent >= 0 ? result.Bones[result.Bones[b].Parent].Pivot : Vector3.Zero);
                    var translation = Track(skeleton,o+16,sequence,ms,false,external,sequenceData,globalHeader,Vector4.Zero);
                    var rotation = Track(skeleton,o+36,sequence,ms,true,external,sequenceData,globalHeader,new Vector4(0,0,0,1));
                    var scale = Track(skeleton,o+56,sequence,ms,false,external,sequenceData,globalHeader,Vector4.One);
                    frames[f][b] = new Pose(bind + ModelData.Convert(new Vector3(translation.X,translation.Y,translation.Z)),
                        Quaternion.Normalize(new Quaternion(-rotation.Y,rotation.X,rotation.Z,rotation.W)), new Vector3(scale.Y,scale.X,scale.Z));
                }
            }
            result.Clips.Add(new Clip(name,(count-1)*1000f/duration,loop,frames));
        }
    }

    /// <summary>A bone's resting transform in the model's own space (first frame of its first sequence, parents
    /// included), M2 axes. Emitters, ribbons and lights ride on bones: unanimated models still place them through
    /// it (a lamp's glow at its lantern, a scene's torches where its bones hold them). Identity when the first
    /// sequence lives in an external file or the bone is unreadable.</summary>
    internal static Matrix4x4 RestTransform(byte[] md, int bone)
    {
        try
        {
            var (sequenceCount, sequenceOffset) = Array(md, 28, 64);
            if (sequenceCount == 0 || (U(md, sequenceOffset + 12) & 32) == 0) return Matrix4x4.Identity;
            var (boneCount, boneOffset) = Array(md, 44, 88);
            var result = Matrix4x4.Identity;
            for (int guard = 0; bone >= 0 && bone < boneCount && guard < 64; guard++)
            {
                int o = boneOffset + bone * 88;
                var pivot = V(md, o + 76);
                var t = Track(md, o + 16, 0, 0, false, null, md, 20, Vector4.Zero);
                var r = Track(md, o + 36, 0, 0, true, null, md, 20, new Vector4(0, 0, 0, 1));
                var sc = Track(md, o + 56, 0, 0, false, null, md, 20, Vector4.One);
                var q = Quaternion.Normalize(new Quaternion(r.X, r.Y, r.Z, r.W));
                var local = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(sc.X, sc.Y, sc.Z) * Matrix4x4.CreateFromQuaternion(q)
                    * Matrix4x4.CreateTranslation(new Vector3(t.X, t.Y, t.Z)) * Matrix4x4.CreateTranslation(pivot);
                result *= local; // row vectors: the child's transform first, then each parent's
                bone = I16(md, o + 8);
            }
            return result;
        }
        catch (Exception) { return Matrix4x4.Identity; }
    }

    private static Vector4 Track(byte[] bones,int header,int sequence,float time,bool rotation,byte[] external,byte[] sequenceData,int globalHeader,Vector4 fallback)
    {
        int global = I16(bones,header+2);
        if (global >= 0)
        {
            sequence = 0; external = null;
            var (globalCount,globalOffset) = Array(sequenceData,globalHeader,4);
            if (global < globalCount && U(sequenceData,globalOffset+global*4) > 0) time %= U(sequenceData,globalOffset+global*4);
        }
        int nTimes=(int)U(bones,header+4), timesOffset=(int)U(bones,header+8);
        int nValues=(int)U(bones,header+12), valuesOffset=(int)U(bones,header+16);
        if (sequence >= nTimes || sequence >= nValues) return fallback;
        int n=(int)U(bones,timesOffset+sequence*8), to=(int)U(bones,timesOffset+sequence*8+4);
        int vn=(int)U(bones,valuesOffset+sequence*8), vo=(int)U(bones,valuesOffset+sequence*8+4);
        n=Math.Min(n,vn); if(n==0)return fallback;
        var data=external??bones; int stride=rotation?8:12;
        Check(data,to,n,4); Check(data,vo,n,stride);
        int lo=0, hi=n-1;
        while(lo<hi) {int mid=(lo+hi+1)/2;if(U(data,to+mid*4)<=time)lo=mid;else hi=mid-1;}
        Vector4 Read(int i) => rotation ? new Vector4(Q(data,vo+i*8),Q(data,vo+i*8+2),Q(data,vo+i*8+4),Q(data,vo+i*8+6)) : new Vector4(V(data,vo+i*12),0);
        var a=Read(lo); if(lo==n-1 || U16(bones,header)==0)return a;
        float dt=U(data,to+(lo+1)*4)-U(data,to+lo*4);
        float t=dt>0?Math.Clamp((time-U(data,to+lo*4))/dt,0,1):0;
        var b=Read(lo+1);
        if(!rotation)return Vector4.Lerp(a,b,t);
        var qa=new Quaternion(a.X,a.Y,a.Z,a.W); var qb=new Quaternion(b.X,b.Y,b.Z,b.W);
        if(qa.LengthSquared()<0.001f||qb.LengthSquared()<0.001f)return fallback;
        var q=Quaternion.Slerp(Quaternion.Normalize(qa),Quaternion.Normalize(qb),t);
        return new Vector4(q.X,q.Y,q.Z,q.W);
    }
    private static float Q(byte[] b,int o)=>(U16(b,o)-32767)/32768f;
    private static Vector3 V(byte[] b,int o)=>new(BitConverter.ToSingle(b,o),BitConverter.ToSingle(b,o+4),BitConverter.ToSingle(b,o+8));
    private static uint U(byte[] b,int o)=>BitConverter.ToUInt32(b,o);
    private static ushort U16(byte[] b,int o)=>BitConverter.ToUInt16(b,o);
    private static short I16(byte[] b,int o)=>BitConverter.ToInt16(b,o);
    private static (int,int) Array(byte[] b,int h,int stride) {int n=checked((int)U(b,h)),o=checked((int)U(b,h+4));Check(b,o,n,stride);return(n,o);}
    private static void Check(byte[] b,int o,int n,int stride) {if(o<0||n<0||o+(long)n*stride>b.Length)throw new InvalidDataException("Character array out of bounds");}
    internal static byte[] Chunk(byte[] file,string name)
    {
        for(int o=0;o+8<=file.Length;)
        {
            string tag=System.Text.Encoding.ASCII.GetString(file,o,4);int n=BitConverter.ToInt32(file,o+4);o+=8;
            if(n<0||n>file.Length-o)return null;
            if(tag==name)return file[o..(o+n)];o+=n;
        }
        return null;
    }
}
