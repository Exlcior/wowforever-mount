namespace MiniBox.WowForever;

/// <summary>Classic character appearance. FileDataIDs and layout 1 are read from the local client.</summary>
public sealed class CharacterPreset
{
    public string Key { get; set; }
    public string Title { get; set; }
    public uint Model { get; set; }
    public uint Skin { get; set; }
    public uint FaceLower { get; set; }
    public uint FaceUpper { get; set; }
    public uint Pelvis { get; set; }
    public uint Torso { get; set; }

    public BlpImage Compose(Func<uint, byte[]> read)
    {
        var baseImage = BlpImage.Read(read(Skin));
        int w=baseImage.Width,h=baseImage.Height;var rgba=baseImage.ToRgba();
        // CharComponentTextureSections: layout 1 has a stacked face; layout 2 gives the face its right half.
        bool hd = w == h * 2;
        foreach(var (id,x,y) in new[]{(FaceUpper,0,640),(FaceLower,0,768),(Pelvis,512,384),(Torso,512,0)})
        {
            if(id==0)continue;
            var bytes=read(id);if(bytes is null)continue;
            var layer=BlpImage.Read(bytes);var pixels=layer.ToRgba();
            bool face=id==FaceUpper||id==FaceLower;
            int dx=hd ? (face ? h : x*h/1024) : x*w/1024;
            int dy=hd && face ? 0 : y*h/1024;
            int width=hd ? (face ? h : h/2) : w/2,height=layer.Height*width/layer.Width;
            for(int py=0;py<height && dy+py<h;py++)for(int px=0;px<width && dx+px<w;px++)
            {
                int src=(py*layer.Height/height*layer.Width+px*layer.Width/width)*4;
                int dst=((dy+py)*w+dx+px)*4;int alpha=pixels[src+3];
                for(int c=0;c<3;c++)rgba[dst+c]=(byte)((pixels[src+c]*alpha+rgba[dst+c]*(255-alpha)+127)/255);
                rgba[dst+3]=255;
            }
        }
        return new BlpImage{Width=w,Height=h,Format=BlpImage.Kind.Rgba,Mips=new(){rgba}};
    }
}
