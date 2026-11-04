using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using BetterChat.Core;

namespace BetterChat.Chat;

/// <summary>Dessine les icônes du jeu à la taille du texte, depuis l'image où le jeu les range toutes.</summary>
internal sealed class GameIconAtlas
{
    private const string TablePath = "common/font/gfdata.gfd";
    private const string ImagePath = "common/font/fonticon_ps5.tex";

    // Les icônes commencent 170 pixels plus bas dans l'image, qui est deux fois plus grande que la table ne l'indique.
    private const int ImageTop = 170;
    private const float ImageScale = 2f;

    private GameIconTable? table;

    private GameIconTable Table
    {
        get
        {
            if (table != null)
                return table;

            try
            {
                table = GameIconTable.Parse(Plugin.DataManager.GetFile(TablePath)!.Data);
            }
            catch (Exception ex)
            {
                // Sans la table, les messages s'affichent simplement sans leurs icônes.
                Plugin.Log.Error(ex, "Lecture des icônes du jeu impossible");
                table = GameIconTable.Parse(ReadOnlySpan<byte>.Empty);
            }

            return table;
        }
    }

    /// <summary>Largeur de l'icône pour une hauteur de ligne donnée. Zéro si elle n'existe pas.</summary>
    public float WidthOf(uint iconId, float lineHeight) =>
        Table.TryGet(iconId, out var entry) ? MathF.Round(entry.Width * lineHeight / entry.Height) : 0f;

    public void Draw(ImDrawListPtr drawList, uint iconId, Vector2 pos, float lineHeight, float alpha)
    {
        if (!Table.TryGet(iconId, out var entry))
            return;

        var image = Plugin.TextureProvider.GetFromGame(ImagePath).GetWrapOrDefault();
        if (image == null)
            return;

        var imageSize = new Vector2(image.Width, image.Height);
        var uvMin = new Vector2(entry.Left, entry.Top + ImageTop) * ImageScale / imageSize;
        var uvMax = new Vector2(entry.Left + entry.Width, entry.Top + entry.Height + ImageTop) * ImageScale / imageSize;

        var min = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
        var size = new Vector2(WidthOf(iconId, lineHeight), MathF.Round(lineHeight));
        drawList.AddImage(image.Handle, min, min + size, uvMin, uvMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
    }
}
