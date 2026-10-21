using Sandbox;
using Sandbox.Mounting;

namespace MiniBox.WowForever;

/// <summary>The client's interface atlases (UiTextureAtlas → UiTextureAtlasMember): each named piece (race and class
/// icons, rings, bronze frames, buttons of the character creation screen) cut out of its atlas texture.</summary>
internal static class UiAtlases
{
    public const uint AtlasFile = 897470, MemberFile = 897532, ElementFile = 1989276;
    /// <summary>Families of pieces the character creation screen draws.</summary>
    private static readonly string[] Prefixes =
    {
        "charactercreate", "character-create", "heavybronze", "raceicon", "classicon", "common-icon", "glue", "128-redbutton",
        "128-brownbutton", "customizationtab", "charcustomize", "common-dropdown", "bag-arrow", "uitools-icon",
    };

    public sealed record Piece(uint File, int Left, int Top, int Width, int Height);

    public static Dictionary<string, Piece> Read(WowForeverMount host)
    {
        var atlases = host.Table(AtlasFile); var members = host.Table(MemberFile);
        Db2 elements = null;
        try { elements = host.Table(ElementFile); } catch (Exception) { }
        var pieces = new Dictionary<string, Piece>();
        foreach (var id in members.Ids)
        {
            string name = members.String(id, 0);
            if (string.IsNullOrEmpty(name) && elements is not null) name = elements.String(members.Relation(id), 0);
            if (string.IsNullOrEmpty(name)) continue;
            name = name.ToLowerInvariant();
            if (!Prefixes.Any(name.StartsWith)) continue;
            uint atlas = members.UInt(id, 2), file = atlases.UInt(atlas, 1);
            int left = members.Int(id, 5), right = members.Int(id, 6), top = members.Int(id, 7), bottom = members.Int(id, 8);
            if (file == 0 || right <= left || bottom <= top || !host.Storage.Exists(file)) continue;
            pieces.TryAdd(name, new Piece(file, left, top, right - left, bottom - top));
        }
        return pieces;
    }
}

/// <summary>One atlas piece, cropped from the client's atlas texture.</summary>
internal sealed class AtlasPieceLoader(UiAtlases.Piece piece) : ResourceLoader<WowForeverMount>
{
    protected override object Load()
    {
        var image = Host.Image(piece.File);
        if (image is null) return Texture.White;
        var rgba = image.ToRgba();
        int w = Math.Min(piece.Width, image.Width - piece.Left), h = Math.Min(piece.Height, image.Height - piece.Top);
        if (w <= 0 || h <= 0) return Texture.White;
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Array.Copy(rgba, ((piece.Top + y) * image.Width + piece.Left) * 4, data, y * w * 4, w * 4);
        return Texture.Create(w, h).WithData(data).WithMips().Finish();
    }
}
