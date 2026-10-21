using Sandbox;
using System.Runtime.InteropServices;

namespace MiniBox.WowForever;

/// <summary>Native skinned model from a skeletal M2: bones (identity bind, deformation transforms), joint
/// attachments, meshes per material and sampled sequences. Shared by characters, creatures and mounts.</summary>
internal static class SkinnedModel
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SkinVertex
    {
        public Vector3 Position;
        public Color32 Color;
        public Vector3 Normal;
        public Vector4 TexCoord, Tangent;
        public uint Bones;
        // The engine's skinning_cs reads four UNORM8 weights, regardless of the declared float format.
        public uint Weights;
        public static readonly VertexAttribute[] Layout =
        [
            new(VertexAttributeType.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttributeType.Color, VertexAttributeFormat.UInt8, 4),
            new(VertexAttributeType.Normal, VertexAttributeFormat.Float32, 3),
            new(VertexAttributeType.TexCoord, VertexAttributeFormat.Float32, 4),
            new(VertexAttributeType.Tangent, VertexAttributeFormat.Float32, 4),
            new(VertexAttributeType.BlendIndices, VertexAttributeFormat.UInt8, 4),
            new(VertexAttributeType.BlendWeights, VertexAttributeFormat.UInt8, 4)
        ];
    }

    /// <summary>Geoset groups of a character as body groups ("g&lt;group&gt;"): choice 0 is the default variant (or none
    /// when the default hides the group), then the other variants, then none. Geoset 0, the body, is always shown.</summary>
    public static SortedDictionary<int, List<int>> BodyGroups(ModelData geometry, Func<int, bool> visible)
    {
        var groups = new SortedDictionary<int, List<int>>();
        foreach (var geoset in geometry.Parts.Where(p => p.Lod == 0 && p.Geoset != 0).Select(p => p.Geoset).Distinct().OrderBy(g => g))
        {
            if (!groups.TryGetValue(geoset / 100, out var list)) groups[geoset / 100] = list = new List<int>();
            list.Add(geoset);
        }
        foreach (var list in groups.Values)
        {
            int initial = list.FirstOrDefault(visible);
            list.Remove(initial);
            list.Insert(0, initial); // 0 when nothing shows by default
            if (initial != 0) list.Add(0);
        }
        return groups;
    }

    /// <param name="perGeoset">Dressable characters: each geoset gets its own material, named "_g&lt;geoset&gt;_".</param>
    public static Model Build(WowForeverMount Host, string Path, CharacterData data, Func<uint, Texture> textureFor, bool perGeoset = false)
    {
        var builder = new ModelBuilder().WithName(Path);
        var bounds = new BBox(WowForeverMount.V(data.Geometry.Min)*36, WowForeverMount.V(data.Geometry.Max)*36);
        for (int b=0;b<data.Bones.Length;b++)
        {
            var bone=data.Bones[b]; var pivot=WowForeverMount.V(bone.Pivot)*36;
            // Runtime ModelBuilder meshes have no inverse-bind palette. Feed hierarchical deformation
            // transforms (identity bind) and expose real joint positions as native attachments.
            builder.AddBone("skin_"+b, Vector3.Zero, Rotation.Identity, bounds, bone.Parent>=0?"skin_"+bone.Parent:null);
            builder.AddAttachment("wow_"+b, pivot, Rotation.Identity, "skin_"+b);
        }
        // Named client attachment points: "mount" is where a rider sits, "attach_<id>" the others.
        foreach (var (id, bone, position) in data.Attachments.GroupBy(a => a.Id).Select(g => g.First()))
            builder.AddAttachment(id == 0 ? "mount" : "attach_" + id, WowForeverMount.V(position) * 36, Rotation.Identity, "skin_" + bone);
        var shared=new Dictionary<(uint,int,uint,int,int),Material>();
        foreach(var group in data.Geometry.Parts.Where(p=>p.Lod<=2).GroupBy(p=>(p.Lod,p.Texture,p.Blend,p.Flags,p.TextureType,Geoset:perGeoset?p.Geoset:0)))
        {
            var verts=new List<SkinVertex>();var indices=new List<int>();
            foreach(var part in group)
            {
                int start=verts.Count;
                for(int i=0;i<part.Positions.Length;i++)
                {
                    int src=part.SourceVertices[i]; var weights=data.Weights[src]; var bones=data.BoneIndices[src];
                    float sum=weights.Sum(w=>(float)w);
                    var n=WowForeverMount.V(part.Normals[i]).Normal;
                    var t=Vector3.Cross(MathF.Abs(n.z)>.9f?Vector3.Left:Vector3.Up,n).Normal;
                    verts.Add(new SkinVertex { Position=WowForeverMount.V(part.Positions[i])*36, Color=Color.White, Normal=n,
                        TexCoord=new Vector4(part.Uv[i].X,part.Uv[i].Y,0,0), Tangent=new Vector4(t.x,t.y,t.z,1),
                        Bones=(uint)(bones[0]|bones[1]<<8|bones[2]<<16|bones[3]<<24),
                        Weights=sum>0?(uint)(weights[0]|weights[1]<<8|weights[2]<<16|weights[3]<<24):255u });
                }
                indices.AddRange(part.Indices.Select(i=>i+start));
            }
            // Materials are named by texture type ("_t1" body skin, "_t6" hair...) so a game can dress the character, and
            // on dressable characters by geoset ("_g<geoset>_") so it can hide the variants not worn.
            int geoset=group.Key.Geoset;
            var materialKey=(group.Key.Texture,group.Key.Blend,group.Key.Flags,group.Key.TextureType,geoset);
            if(!shared.TryGetValue(materialKey,out var material))
            {
            if((group.Key.Blend>=2 || (group.Key.Flags&1)!=0) && !perGeoset)
                // Glows, flames and transparent layers keep the client's blend mode (shared prop materials).
                material=Host.PropMaterial(group.Key.Texture,group.Key.Blend,group.Key.Flags);
            else
            {
                material=Material.Create($"{Path}_t{group.Key.TextureType}_g{geoset}_{group.Key.Texture}_{group.Key.Blend}_{group.Key.Flags}.vmat","shaders/complex.shader");
                if(group.Key.Blend==1)material.SetFeature("F_ALPHA_TEST",1);
                if(group.Key.Blend>=2)material.SetFeature("F_TRANSLUCENT",1);
                if(group.Key.Blend is 3 or 4)material.SetFeature("F_ADDITIVE_BLEND",1);
                if((group.Key.Flags&4)!=0)material.SetFeature("F_RENDER_BACKFACES",1);
                material.Set("g_tColor",textureFor(group.Key.Texture));Host.Neutral(material);
                if(group.Key.Blend>=1){material.Set("g_tTranslucency",Host.GetTexture(group.Key.Texture,true));material.Set("g_flAlphaTestReference",0.5f);}
            }
            shared[materialKey]=material;
            }
            var mesh=new Mesh(material){Bounds=bounds,UvDensity=36};
            mesh.CreateVertexBuffer(verts.Count,SkinVertex.Layout,verts.ToArray().AsSpan());
            mesh.CreateIndexBuffer(indices.Count,indices.ToArray().AsSpan());
            builder.AddMesh(mesh,group.Key.Lod);
        }
        if (data.Geometry.Parts.Any(p => p.Lod == 1)) builder.WithLodDistance(1,6000);
        if (data.Geometry.Parts.Any(p => p.Lod == 2)) builder.WithLodDistance(2,12000);
        foreach(var clip in data.Clips)
        {
            var animation=builder.AddAnimation(clip.Name,clip.FrameRate).WithLooping(clip.Loop);
            foreach(var frame in clip.Frames)
                animation.AddFrame(frame.Select((p,b)=>
                {
                    var bone=data.Bones[b];
                    var bind=bone.Pivot-(bone.Parent>=0?data.Bones[bone.Parent].Pivot:System.Numerics.Vector3.Zero);
                    // T(pivot) * T(track translation) * R * S * T(-pivot), in the converted axes.
                    var position=bone.Pivot+p.Position-bind-System.Numerics.Vector3.Transform(bone.Pivot*p.Scale,p.Rotation);
                    return new Transform(WowForeverMount.V(position)*36,
                        new Rotation(p.Rotation.X,p.Rotation.Y,p.Rotation.Z,p.Rotation.W),WowForeverMount.V(p.Scale));
                }).ToArray().AsSpan());
        }
        return builder.Create();
    }
}
