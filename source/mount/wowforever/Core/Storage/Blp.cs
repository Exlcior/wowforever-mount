namespace MiniBox.WowForever;

/// <summary>BLP2 textures (adapted from wow.export's BLP reader, MIT, Kruithne and Marlamin).
/// Block-compressed images keep their DXT mips; palette and BGRA images are expanded to RGBA.</summary>
public sealed class BlpImage
{
    public enum Kind { Dxt1, Dxt3, Dxt5, Rgba }
    public int Width, Height;
    public Kind Format;
    /// <summary>Mip levels, largest first, in the format above.</summary>
    public List<byte[]> Mips = new();
    public bool HasAlpha;

    public static BlpImage Read(byte[] data)
    {
        if (data.Length < 148 || BitConverter.ToUInt32(data, 0) != 0x32504C42) throw new InvalidDataException("Not a BLP2 texture");
        if (BitConverter.ToUInt32(data, 4) != 1) throw new InvalidDataException("Unsupported BLP type");
        byte encoding = data[8], alphaDepth = data[9], alphaEncoding = data[10];
        var image = new BlpImage { Width = (int)BitConverter.ToUInt32(data, 12), Height = (int)BitConverter.ToUInt32(data, 16), HasAlpha = alphaDepth > 0 };
        if (image.Width <= 0 || image.Height <= 0 || image.Width > 8192 || image.Height > 8192) throw new InvalidDataException("Invalid BLP size");
        var offsets = new uint[16]; var sizes = new uint[16];
        for (int i = 0; i < 16; i++) { offsets[i] = BitConverter.ToUInt32(data, 20 + i * 4); sizes[i] = BitConverter.ToUInt32(data, 84 + i * 4); }
        image.Format = encoding == 2 ? (alphaDepth > 1 ? (alphaEncoding == 7 ? Kind.Dxt5 : Kind.Dxt3) : Kind.Dxt1) : Kind.Rgba;
        var palette = encoding == 1 ? data.AsSpan(148, 1024).ToArray() : null;
        for (int level = 0; level < 16; level++)
        {
            if (offsets[level] == 0 || sizes[level] == 0) break;
            int w = Math.Max(1, image.Width >> level), h = Math.Max(1, image.Height >> level);
            if (offsets[level] + sizes[level] > data.Length) break;
            var raw = data.AsSpan((int)offsets[level], (int)sizes[level]);
            image.Mips.Add(encoding switch
            {
                1 => Palette(raw, palette, w, h, alphaDepth),
                2 => raw.ToArray(),
                3 => Bgra(raw, w, h),
                _ => throw new InvalidDataException("Unsupported BLP encoding " + encoding),
            });
            if (w == 1 && h == 1) break;
        }
        if (image.Mips.Count == 0) throw new InvalidDataException("BLP without image data");
        return image;
    }

    private static byte[] Palette(ReadOnlySpan<byte> raw, byte[] palette, int w, int h, int alphaDepth)
    {
        int n = w * h; var rgba = new byte[n * 4];
        for (int i = 0; i < n && i < raw.Length; i++)
        {
            int p = raw[i] * 4;
            rgba[i * 4] = palette[p + 2]; rgba[i * 4 + 1] = palette[p + 1]; rgba[i * 4 + 2] = palette[p]; // palette is BGRA
            rgba[i * 4 + 3] = alphaDepth switch
            {
                1 => (byte)((raw[n + i / 8] >> (i % 8) & 1) != 0 ? 255 : 0),
                4 => (byte)(i % 2 == 0 ? (raw[n + i / 2] & 15) << 4 : raw[n + i / 2] & 240),
                8 => raw[n + i],
                _ => 255,
            };
        }
        return rgba;
    }

    private static byte[] Bgra(ReadOnlySpan<byte> raw, int w, int h)
    {
        int n = Math.Min(w * h, raw.Length / 4); var rgba = new byte[w * h * 4];
        for (int i = 0; i < n; i++)
        {
            rgba[i * 4] = raw[i * 4 + 2]; rgba[i * 4 + 1] = raw[i * 4 + 1]; rgba[i * 4 + 2] = raw[i * 4]; rgba[i * 4 + 3] = raw[i * 4 + 3];
        }
        return rgba;
    }

    /// <summary>RGBA8888 mip chain (box filter) as one buffer, smallest level first: what a native texture with mips expects.</summary>
    public byte[] RgbaChain(out int levels)
    {
        var level = ToRgba(); int w = Width, h = Height;
        var chain = new List<byte[]> { level };
        int wanted = 1 + (int)Math.Log2(Math.Max(1, Math.Min(Width, Height))); // what the engine counts for a full chain
        while ((w > 1 || h > 1) && chain.Count < wanted)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2); var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                        next[(y * nw + x) * 4 + c] = (byte)((level[(y0 * w + x0) * 4 + c] + level[(y0 * w + x1) * 4 + c] + level[(y1 * w + x0) * 4 + c] + level[(y1 * w + x1) * 4 + c] + 2) / 4);
                    }
            chain.Add(next); level = next; w = nw; h = nh;
        }
        levels = chain.Count;
        // Native textures take their mips smallest first (see the engine's DDS loader).
        var all = new byte[chain.Sum(c => c.Length)]; int o = 0;
        for (int i = chain.Count - 1; i >= 0; i--) { chain[i].CopyTo(all, o); o += chain[i].Length; }
        return all;
    }

    /// <summary>Alpha copied into RGB, with its own mip chain (opacity and height masks).</summary>
    public byte[] AlphaChain(out int levels)
    {
        var rgba = RgbaChain(out levels);
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = rgba[i + 1] = rgba[i + 2] = rgba[i + 3]; rgba[i + 3] = 255; }
        return rgba;
    }

    /// <summary>Expands mip 0 (any format) to RGBA8888.</summary>
    public byte[] ToRgba(int level = 0)
    {
        int w = Math.Max(1, Width >> level), h = Math.Max(1, Height >> level);
        if (Format == Kind.Rgba) return Mips[level];
        var src = Mips[level]; var dst = new byte[w * h * 4];
        int blockBytes = Format == Kind.Dxt1 ? 8 : 16, pos = 0;
        Span<byte> colours = stackalloc byte[16];
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4, pos += blockBytes)
            {
                if (pos + blockBytes > src.Length) return dst;
                int c = Format == Kind.Dxt1 ? pos : pos + 8;
                int a = src[c] | src[c + 1] << 8, b = src[c + 2] | src[c + 3] << 8;
                Unpack(a, colours, 0); Unpack(b, colours, 4);
                for (int k = 0; k < 3; k++)
                {
                    int ca = colours[k], cb = colours[4 + k];
                    if (Format != Kind.Dxt1 || a > b) { colours[8 + k] = (byte)((2 * ca + cb) / 3); colours[12 + k] = (byte)((ca + 2 * cb) / 3); }
                    else { colours[8 + k] = (byte)((ca + cb) / 2); colours[12 + k] = 0; }
                }
                colours[11] = 255; colours[15] = (byte)(Format == Kind.Dxt1 && a <= b ? 0 : 255);
                for (int py = 0; py < 4; py++)
                {
                    int bits = src[c + 4 + py];
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx + px, y = by + py; if (x >= w || y >= h) continue;
                        int idx = bits >> (px * 2) & 3, o = (y * w + x) * 4;
                        dst[o] = colours[idx * 4]; dst[o + 1] = colours[idx * 4 + 1]; dst[o + 2] = colours[idx * 4 + 2]; dst[o + 3] = colours[idx * 4 + 3];
                        int texel = py * 4 + px;
                        if (Format == Kind.Dxt3) { int nibble = src[pos + texel / 2] >> (texel % 2 * 4) & 15; dst[o + 3] = (byte)(nibble * 17); }
                        else if (Format == Kind.Dxt5) dst[o + 3] = Dxt5Alpha(src, pos, texel);
                    }
                }
            }
        return dst;
    }

    private static void Unpack(int value, Span<byte> colour, int o)
    {
        int r = value >> 11 & 31, g = value >> 5 & 63, b = value & 31;
        colour[o] = (byte)(r << 3 | r >> 2); colour[o + 1] = (byte)(g << 2 | g >> 4); colour[o + 2] = (byte)(b << 3 | b >> 2); colour[o + 3] = 255;
    }

    private static byte Dxt5Alpha(byte[] src, int pos, int texel)
    {
        int a0 = src[pos], a1 = src[pos + 1];
        ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)src[pos + 2 + i] << (8 * i);
        int code = (int)(bits >> (3 * texel) & 7);
        if (code == 0) return (byte)a0; if (code == 1) return (byte)a1;
        if (a0 > a1) return (byte)(((8 - code) * a0 + (code - 1) * a1) / 7);
        if (code == 6) return 0; if (code == 7) return 255;
        return (byte)(((6 - code) * a0 + (code - 1) * a1) / 5);
    }
}
