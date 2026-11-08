using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace BetterChat.Windows;

/// <summary>Composants visuels des réglages : cartes, interrupteurs, sélecteurs, boutons...</summary>
public sealed partial class ConfigWindow
{
    // ------------------------------------------------------------------ Palette

    // Des gris neutres à peine violets, des panneaux plus clairs que le fond, et la couleur d'accent pour ce qui compte.
    // Le fond de la fenêtre laisse deviner le jeu derrière ; les panneaux posés dessus restent pleins.
    private static readonly Vector4 WindowBg = new(0.063f, 0.059f, 0.075f, 0.80f);
    private static readonly Vector4 CardBg = new(0.110f, 0.106f, 0.129f, 1f);
    private static readonly Vector4 CardBgHover = new(0.200f, 0.196f, 0.227f, 1f);
    private static readonly Vector4 InputBg = new(0.157f, 0.153f, 0.180f, 1f);
    private static readonly Vector4 InputBgHover = new(0.200f, 0.196f, 0.227f, 1f);
    private static readonly Vector4 Line = new(1f, 1f, 1f, 0.055f);
    private static readonly Vector4 HoverOverlay = new(1f, 1f, 1f, 0.05f);
    private static readonly Vector4 TextMain = new(0.95f, 0.94f, 0.97f, 1f);
    private static readonly Vector4 TextDim = new(0.56f, 0.55f, 0.61f, 1f);
    private static readonly Vector4 TextFaint = new(0.42f, 0.41f, 0.47f, 1f);
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Success = new(0.38f, 0.85f, 0.56f, 1f);
    private static readonly Vector4 Danger = new(1f, 0.47f, 0.47f, 1f);

    private Vector4 Accent => Cfg.AccentColor with { W = 1f };
    private Vector4 AccentSoft => Cfg.AccentColor with { W = 0.20f };
    private Vector4 AccentBorder => Cfg.AccentColor with { W = 0.60f };

    /// <summary>Le fond clair des pastilles d'icône, dans le ton de l'accent.</summary>
    private Vector4 AccentPale => Vector4.Lerp(Accent, White, 0.72f) with { W = 1f };

    /// <summary>Une icône posée sur <see cref="AccentPale"/> : l'accent, assombri.</summary>
    private Vector4 AccentInk => Vector4.Lerp(Accent, DarkInk, 0.55f) with { W = 1f };

    private static readonly Vector4 DarkInk = new(0.07f, 0.06f, 0.10f, 1f);

    /// <summary>Couleur du texte posé sur la couleur d'accent : sombre si l'accent est clair (blanc...), blanc sinon.</summary>
    private Vector4 OnAccent
    {
        get
        {
            var a = Accent;
            var luminance = 0.2126f * a.X + 0.7152f * a.Y + 0.0722f * a.Z;
            return luminance > 0.6f ? DarkInk : White;
        }
    }

    // Arrondis : les panneaux sont très arrondis, les éléments à l'intérieur un peu moins.
    private static float PanelRounding => 18f * S;
    private static float PanelPadding => 18f * S;
    private static float PanelGap => 12f * S;

    private static float S => ImGuiHelpers.GlobalScale;
    private static uint Col(Vector4 color) => ImGui.GetColorU32(color);
    private static float FrameH => ImGui.GetFrameHeight();
    private static float ItemSpacing => ImGui.GetStyle().ItemSpacing.X;

    // Largeur utile de la page et de la carte en cours.
    private float pageWidth;
    private float cardInner;

    // ------------------------------------------------------------------ Mesures

    private static float TextWidth(string text) => ImGui.CalcTextSize(text).X;

    internal static Vector2 IconSize(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            return ImGui.CalcTextSize(icon.ToIconString());
    }

    // Interrupteur compact : piste de 60 % de la hauteur d'une ligne, 1,75 fois plus large que haute.
    private static float SwitchTrackH => FrameH * 0.60f;
    private static Vector2 SwitchSize => new(SwitchTrackH * 1.75f, FrameH);

    private static Vector2 ButtonSize(string label, FontAwesomeIcon? icon = null)
    {
        var width = TextWidth(label) + 28 * S;
        if (icon is { } i)
            width += IconSize(i).X + 8 * S;
        return new Vector2(width, FrameH + 4 * S);
    }

    // ------------------------------------------------------------------ Dessin de base

    /// <summary>
    /// Centre le dessin réel de l'icône : la boîte d'un caractère de police n'est pas centrée sur son dessin,
    /// ce qui décalait les icônes de un ou deux pixels dans leur bouton.
    /// </summary>
    internal static void DrawIconCentered(FontAwesomeIcon icon, Vector2 min, Vector2 max, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var (position, _) = PlaceIcon(icon, min, max);
            ImGui.GetWindowDrawList().AddText(position, Col(color), icon.ToIconString());
        }
    }

    /// <summary>Le centre réel du dessin de l'icône une fois placée : c'est sur lui qu'un fond doit se centrer.</summary>
    internal static Vector2 IconVisualCenter(FontAwesomeIcon icon, Vector2 min, Vector2 max)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var (position, middle) = PlaceIcon(icon, min, max);
            return position + middle;
        }
    }

    /// <summary>Taille réelle du dessin de l'icône, sans les marges de sa police.</summary>
    internal static unsafe Vector2 IconGlyphSize(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var font = ImGui.GetFont();
            var glyph = font.FindGlyph((char)icon);
            if (glyph == null || font.FontSize <= 0f)
                return ImGui.CalcTextSize(icon.ToIconString());

            return new Vector2(glyph->X1 - glyph->X0, glyph->Y1 - glyph->Y0) * (ImGui.GetFontSize() / font.FontSize);
        }
    }

    /// <summary>
    /// Position d'écriture de l'icône (sur un pixel entier, pour rester nette) et milieu de son dessin par rapport à
    /// cette position. La police des icônes doit être active.
    /// </summary>
    private static unsafe (Vector2 Position, Vector2 Middle) PlaceIcon(FontAwesomeIcon icon, Vector2 min, Vector2 max)
    {
        var font = ImGui.GetFont();
        var glyph = font.FindGlyph((char)icon);

        Vector2 middle;
        if (glyph != null && font.FontSize > 0f)
        {
            var scale = ImGui.GetFontSize() / font.FontSize;
            middle = new Vector2(glyph->X0 + glyph->X1, glyph->Y0 + glyph->Y1) * scale / 2f;
        }
        else
        {
            middle = ImGui.CalcTextSize(icon.ToIconString()) / 2f;
        }

        var position = (min + max) / 2f - middle;
        return (new Vector2(MathF.Round(position.X), MathF.Round(position.Y)), middle);
    }

    /// <summary>Icône + texte centrés dans un rectangle.</summary>
    private static void DrawIconLabel(FontAwesomeIcon? icon, string label, Vector2 min, Vector2 max, Vector4 color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(label);
        var iconSize = icon is { } i ? IconSize(i) : Vector2.Zero;
        var gap = icon.HasValue && label.Length > 0 ? 8f * S : 0f;
        var total = iconSize.X + gap + textSize.X;
        var x = min.X + (max.X - min.X - total) / 2f;
        var centerY = (min.Y + max.Y) / 2f;

        if (icon is { } ic)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                drawList.AddText(new Vector2(x, centerY - iconSize.Y / 2f), Col(color), ic.ToIconString());
        }

        drawList.AddText(new Vector2(x + iconSize.X + gap, centerY - textSize.Y / 2f), Col(color), label);
    }

    private static void TextDimmed(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, TextDim))
            ImGui.TextWrapped(text);
    }

    private static void IconText(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextUnformatted(icon.ToIconString());
    }

    // ------------------------------------------------------------------ Mise en page

    // En-tête du prochain panneau, posé par SectionTitle : une pastille d'icône, un titre, et à droite une étiquette.
    private (string Title, FontAwesomeIcon Icon, string? Right)? pendingHeader;

    // Hauteur naturelle (sans hauteur minimale) du dernier panneau dessiné : sert à aligner les panneaux d'une rangée.
    private float lastCardNaturalHeight;

    /// <summary>
    /// Panneau arrondi : le contenu est dessiné d'abord (calque 1), puis le fond derrière (calque 0) une fois la
    /// hauteur connue. Avec <paramref name="glow"/>, un halo de la couleur d'accent éclaire son coin bas droit.
    /// Renvoie la position Y locale du bas du panneau.
    /// </summary>
    private float Card(Action content, float? width = null, bool spacingAfter = true, bool glow = false, float minHeight = 0f)
    {
        var pad = PanelPadding;
        var w = width ?? pageWidth;
        var drawList = ImGui.GetWindowDrawList();
        var startLocal = ImGui.GetCursorPos();
        var start = ImGui.GetCursorScreenPos();
        var header = pendingHeader;
        pendingHeader = null;

        var previousInner = cardInner;
        cardInner = w - pad * 2;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorPos(startLocal + new Vector2(pad, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(startLocal.X + pad + cardInner);
        if (header is { } h)
            PanelHeader(h.Title, h.Icon, h.Right);
        content();
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var natural = ImGui.GetItemRectMax().Y + pad;
        lastCardNaturalHeight = natural - start.Y;
        var bottom = MathF.Max(natural, start.Y + minHeight);
        var max = new Vector2(start.X + w, bottom);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, max, Col(CardBg), PanelRounding);
        if (glow)
        {
            var size = max - start;
            RoundedGlow(drawList, start, max, PanelRounding, max - size * 0.12f, MathF.Max(size.X, size.Y) * 0.85f, Accent, 0.20f);
        }

        drawList.AddRect(start, max, Col(Line), PanelRounding);
        drawList.ChannelsMerge();

        cardInner = previousInner;
        var bottomLocal = startLocal.Y + (bottom - start.Y);
        lastCardBottom = bottomLocal;
        EndAt(startLocal.X, bottomLocal, w, spacingAfter);
        return bottomLocal;
    }

    /// <summary>
    /// Halo lumineux en dégradé lisse, qui ne sort jamais de la forme arrondie : un maillage qui épouse le contour du
    /// rectangle arrondi, chaque sommet coloré selon sa distance au centre du halo. Le centre doit être dans le rectangle.
    /// </summary>
    private static void RoundedGlow(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding, Vector2 center,
                                    float radius, Vector4 color, float alpha)
    {
        const int pointsPerCorner = 10;
        const int rings = 12;

        // Le contour, dans le sens des aiguilles d'une montre : haut gauche, haut droit, bas droit, bas gauche.
        rounding = MathF.Min(rounding, MathF.Min(max.X - min.X, max.Y - min.Y) / 2f);
        var corners = new (Vector2 Center, float Start)[]
        {
            (new Vector2(min.X + rounding, min.Y + rounding), MathF.PI),
            (new Vector2(max.X - rounding, min.Y + rounding), MathF.PI * 1.5f),
            (new Vector2(max.X - rounding, max.Y - rounding), 0f),
            (new Vector2(min.X + rounding, max.Y - rounding), MathF.PI * 0.5f),
        };

        var outline = new List<Vector2>(corners.Length * pointsPerCorner);
        foreach (var (corner, startAngle) in corners)
        {
            for (var i = 0; i < pointsPerCorner; i++)
            {
                var angle = startAngle + MathF.PI / 2f * i / (pointsPerCorner - 1);
                outline.Add(corner + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * rounding);
            }
        }

        uint Shade(Vector2 point)
        {
            var x = Math.Clamp(1f - Vector2.Distance(point, center) / radius, 0f, 1f);
            return Col(color with { W = alpha * x * x * (3f - 2f * x) });
        }

        var count = outline.Count;
        var uv = ImGui.GetFontTexUvWhitePixel();
        drawList.PrimReserve(count * 3 + count * (rings - 1) * 6, 1 + count * rings);
        var first = drawList.VtxCurrentIdx;

        drawList.PrimWriteVtx(center, uv, Shade(center));
        for (var ring = 1; ring <= rings; ring++)
        {
            var t = ring / (float)rings;
            foreach (var point in outline)
            {
                var position = center + (point - center) * t;
                drawList.PrimWriteVtx(position, uv, Shade(position));
            }
        }

        // Un éventail autour du centre, puis des anneaux de quadrilatères jusqu'au contour.
        for (var i = 0; i < count; i++)
        {
            var next = (i + 1) % count;
            drawList.PrimWriteIdx((ushort)first);
            drawList.PrimWriteIdx((ushort)(first + 1 + i));
            drawList.PrimWriteIdx((ushort)(first + 1 + next));
        }

        for (var ring = 1; ring < rings; ring++)
        {
            var inner = first + 1 + (ring - 1) * count;
            var outer = inner + count;
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                drawList.PrimWriteIdx((ushort)(inner + i));
                drawList.PrimWriteIdx((ushort)(inner + next));
                drawList.PrimWriteIdx((ushort)(outer + next));
                drawList.PrimWriteIdx((ushort)(inner + i));
                drawList.PrimWriteIdx((ushort)(outer + next));
                drawList.PrimWriteIdx((ushort)(outer + i));
            }
        }
    }

    // Bas du dernier panneau dessiné, en position locale.
    private float lastCardBottom;

    /// <summary>
    /// Termine un bloc à ce bas exact : la page ne s'étend pas plus loin que lui (le dernier panneau tombe pile sur le
    /// bas de la fenêtre). L'écart avec le bloc suivant ne s'ajoute qu'en déplaçant le curseur.
    /// </summary>
    private static void EndAt(float x, float bottom, float width, bool spacingAfter)
    {
        ImGui.SetCursorPos(new Vector2(x, bottom));
        ImGui.Dummy(new Vector2(width, 0f));
        if (spacingAfter)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + GapAfterPanel);
    }

    /// <summary>L'espace sous un panneau : avec l'espacement d'ImGui, il égale l'écart entre deux panneaux côte à côte.</summary>
    private static float GapAfterPanel => MathF.Max(0f, PanelGap - ImGui.GetStyle().ItemSpacing.Y);

    // Hauteurs des rangées de panneaux, mesurées à l'image précédente.
    private readonly Dictionary<string, float> rowHeights = new();

    /// <summary>
    /// Plusieurs panneaux côte à côte, de même largeur et de même hauteur (celle du plus haut).
    /// <paramref name="draw"/> reçoit le numéro et la largeur du panneau, et doit dessiner un seul <see cref="Card"/>.
    /// </summary>
    private void CardRow(string id, int count, Action<int, float> draw)
    {
        var start = ImGui.GetCursorPos();
        var width = MathF.Floor((pageWidth - PanelGap * (count - 1)) / count);
        rowHeights.TryGetValue(id, out var height);
        var tallest = 0f;
        var bottom = start.Y;
        var previousPage = pageWidth;

        for (var i = 0; i < count; i++)
        {
            ImGui.SetCursorPos(new Vector2(start.X + i * (width + PanelGap), start.Y));
            rowMinHeight = height;
            draw(i, width);
            tallest = MathF.Max(tallest, lastCardNaturalHeight);
            bottom = MathF.Max(bottom, lastCardBottom);
        }

        rowMinHeight = 0f;
        pageWidth = previousPage;
        rowHeights[id] = tallest;
        EndAt(start.X, bottom, pageWidth, spacingAfter: true);
    }

    // Hauteur à donner aux panneaux de la rangée en cours (0 hors rangée).
    private float rowMinHeight;

    /// <summary>Un panneau dans une rangée : sa largeur, la hauteur commune, sans espace après.</summary>
    private void RowCard(float width, Action content, bool glow = false) =>
        Card(content, width, spacingAfter: false, glow: glow, minHeight: rowMinHeight);

    /// <summary>Pastille ronde avec une icône, dans le ton de l'accent.</summary>
    private void IconBadge(FontAwesomeIcon icon, float size)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size, size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled((min + max) / 2f, size / 2f, Col(AccentPale), 32);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            // L'icône à un peu moins de la moitié de la pastille, centrée sur son dessin.
            ImGui.SetWindowFontScale(size * 0.45f / MathF.Max(ImGui.GetFontSize(), 1f));
            var (position, _) = PlaceIcon(icon, min, max);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), position, Col(AccentInk), icon.ToIconString());
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.Dummy(new Vector2(size, size));
    }

    private static float BadgeSize => MathF.Round(30f * S);

    /// <summary>En-tête d'un panneau : pastille d'icône, titre, et une étiquette à droite.</summary>
    private void PanelHeader(string title, FontAwesomeIcon icon, string? right)
    {
        var start = ImGui.GetCursorPos();
        var badge = BadgeSize;
        IconBadge(icon, badge);

        ImGui.SetCursorPos(new Vector2(start.X + badge + 10f * S, start.Y + MathF.Round((badge - ImGui.GetTextLineHeight()) / 2f)));
        ImGui.TextUnformatted(title);

        if (right != null)
        {
            var chip = ChipSize(right);
            ImGui.SetCursorPos(new Vector2(start.X + cardInner - chip.X, start.Y + MathF.Round((badge - chip.Y) / 2f)));
            Chip(right);
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + badge));
        ImGui.Dummy(new Vector2(cardInner, 8f * S));
    }

    private static Vector2 ChipSize(string text) => new(TextWidth(text) + 24f * S, MathF.Round(ImGui.GetTextLineHeight() + 10f * S));

    /// <summary>Étiquette en forme de pilule, sur un fond légèrement plus clair que le panneau.</summary>
    private static void Chip(string text, Vector4? color = null)
    {
        var size = ChipSize(text);
        var min = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, Col(InputBg), size.Y / 2f);
        drawList.AddText(min + new Vector2(12f * S, MathF.Round((size.Y - ImGui.GetTextLineHeight()) / 2f)), Col(color ?? TextMain), text);
        ImGui.Dummy(size);
    }

    /// <summary>Petit bouton rond (flèche, coche...) en haut à droite d'un panneau.</summary>
    private bool CircleButton(string id, FontAwesomeIcon icon, float size)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size, size);
        var clicked = ImGui.InvisibleButton(id, max - min);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        ImGui.GetWindowDrawList().AddCircleFilled((min + max) / 2f, size / 2f, Col(held ? AccentSoft : hovered ? InputBgHover : InputBg), 32);
        DrawIconCentered(icon, min, max, hovered ? TextMain : TextDim);
        return clicked;
    }

    /// <summary>Ligne de réglage : texte à gauche, contrôle aligné à droite et centré verticalement.</summary>
    private void Row(Action left, float controlWidth, Action? control, float? controlHeight = null)
    {
        var start = ImGui.GetCursorPos();
        var gap = control != null ? 16f * S : 0f;
        var leftWidth = cardInner - controlWidth - gap;

        ImGui.PushTextWrapPos(start.X + leftWidth);
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        left();
        ImGui.EndGroup();
        ImGui.PopTextWrapPos();

        var height = controlHeight ?? FrameH;
        var rowHeight = Math.Max(ImGui.GetItemRectSize().Y, height);

        if (control != null)
        {
            ImGui.SetCursorPos(new Vector2(start.X + cardInner - controlWidth, start.Y + (rowHeight - height) / 2f));
            ImGui.BeginGroup();
            control();
            ImGui.EndGroup();
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowHeight));
        ImGui.Dummy(new Vector2(cardInner, 4f * S));
    }

    private void SettingRow(string title, string? description, float controlWidth, Action? control, float? controlHeight = null) =>
        Row(() =>
        {
            ImGui.TextUnformatted(title);
            if (!string.IsNullOrEmpty(description))
                TextDimmed(description);
        }, controlWidth, control, controlHeight);

    private bool ToggleRow(string title, string? description, bool value, Action<bool> setter)
    {
        var changed = false;
        SettingRow(title, description, SwitchSize.X, () =>
        {
            var v = value;
            if (Switch($"##sw_{title}", ref v))
            {
                setter(v);
                changed = true;
            }
        });
        return changed;
    }

    private bool SliderRow(string title, string? description, float value, float min, float max, string format, Action<float> setter)
    {
        var changed = false;
        var width = 230f * S;
        SettingRow(title, description, width, () =>
        {
            var v = value;
            ImGui.SetNextItemWidth(width);
            if (ImGui.SliderFloat($"##sl_{title}", ref v, min, max, format))
            {
                setter(v);
                changed = true;
            }
        });
        return changed;
    }

    private bool SliderIntRow(string title, string? description, int value, int min, int max, Action<int> setter)
    {
        var changed = false;
        var width = 230f * S;
        SettingRow(title, description, width, () =>
        {
            var v = value;
            ImGui.SetNextItemWidth(width);
            if (ImGui.SliderInt($"##sli_{title}", ref v, min, max))
            {
                setter(v);
                changed = true;
            }
        });
        return changed;
    }

    private bool ColorRow(string title, string? description, Vector4 value, Action<Vector4> setter)
    {
        var changed = false;
        SettingRow(title, description, FrameH, () =>
        {
            var v = value;
            if (ImGui.ColorEdit4($"##col_{title}", ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            {
                setter(v);
                changed = true;
            }
        });
        return changed;
    }

    /// <summary>
    /// Ligne repliable dans un panneau : un clic sur son titre l'ouvre ou la ferme. Le chevron tourne, le texte de
    /// droite (un compteur, par exemple) reste visible fermé. Un trait la sépare de la précédente. Renvoie le nouvel état.
    /// </summary>
    private bool FoldingRow(string id, string title, string? right, bool open, bool separator, Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        if (separator)
        {
            var p = ImGui.GetCursorScreenPos();
            drawList.AddLine(p, p + new Vector2(cardInner, 0f), Col(Line));
            ImGui.Dummy(new Vector2(cardInner, 4f * S));
        }

        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(cardInner, FrameH + 4f * S);
        if (ImGui.InvisibleButton(id, size))
            open = !open;

        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var textY = MathF.Round(min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2f);
        drawList.AddText(new Vector2(min.X, textY), Col(hovered ? White : TextMain), title);

        // Le chevron pointe vers la droite fermé, vers le bas ouvert.
        var turn = Animate(ImGui.GetID(id + "turn"), open ? 1f : 0f, 20f);
        var chevronMax = min + size;
        var chevronMin = new Vector2(chevronMax.X - size.Y, min.Y);
        DrawChevron(drawList, (chevronMin + chevronMax) / 2f, 4f * S, turn, hovered ? TextMain : TextDim);

        if (right != null)
        {
            var chip = ChipSize(right);
            ImGui.SetCursorScreenPos(new Vector2(chevronMin.X - 4f * S - chip.X, min.Y + (size.Y - chip.Y) / 2f));
            Chip(right, TextDim);
            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + size.Y));
        }

        if (open)
        {
            ImGui.Dummy(new Vector2(0, 2f * S));
            content();
            ImGui.Dummy(new Vector2(0, 4f * S));
        }

        return open;
    }

    /// <summary>Un chevron dessiné au trait, tourné de 0 (vers la droite) à 1 (vers le bas).</summary>
    private static void DrawChevron(ImDrawListPtr drawList, Vector2 center, float half, float turn, Vector4 color)
    {
        var angle = turn * MathF.PI / 2f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        Vector2 Turn(Vector2 p) => center + new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);

        var a = Turn(new Vector2(-half / 2f, -half));
        var b = Turn(new Vector2(half / 2f, 0f));
        var c = Turn(new Vector2(-half / 2f, half));
        var thickness = MathF.Max(1.5f * S, 1f);
        drawList.AddLine(a, b, Col(color), thickness);
        drawList.AddLine(b, c, Col(color), thickness);
    }

    /// <summary>
    /// Le titre du prochain panneau : il s'affiche dans son en-tête, avec une pastille d'icône et, à droite, une
    /// étiquette (un compteur, un état...).
    /// </summary>
    private void SectionTitle(string title, FontAwesomeIcon icon, string? right = null) => pendingHeader = (title, icon, right);

    /// <summary>
    /// En-tête de page, dans un panneau comme les autres : grande pastille avec l'icône de la page, grand titre,
    /// sous-titre, et quelques étiquettes à droite.
    /// </summary>
    private void Hero(string title, string subtitle, string? statTop = null, string? statBottom = null)
    {
        Card(() =>
        {
            var start = ImGui.GetCursorPos();
            var badge = MathF.Round(46f * S);
            var textX = start.X + badge + 14f * S;

            // Les étiquettes à droite ; le sous-titre passe à la ligne avant elles.
            var chips = new List<string>();
            if (statTop != null)
                chips.Add(statTop);
            if (statBottom != null)
                chips.Add(statBottom);
            var chipsW = 0f;
            foreach (var chip in chips)
                chipsW += ChipSize(chip).X + 8f * S;

            // Mesuré d'abord : le texte se centre sur la pastille quand il est moins haut qu'elle, et inversement.
            var wrapWidth = MathF.Max(40f * S, cardInner - (textX - start.X) - chipsW - 8f * S);
            float titleH;
            using (heroFont.Push())
                titleH = ImGui.GetTextLineHeight();
            var subtitleH = ImGui.CalcTextSize(subtitle, false, wrapWidth).Y;
            var textH = titleH + ImGui.GetStyle().ItemSpacing.Y + subtitleH;
            var height = MathF.Max(badge, textH);

            ImGui.SetCursorPos(new Vector2(textX, start.Y + MathF.Round((height - textH) / 2f)));
            ImGui.BeginGroup();
            using (heroFont.Push())
                ImGui.TextUnformatted(title);
            ImGui.PushTextWrapPos(textX + wrapWidth);
            ImGui.TextColored(TextDim, subtitle);
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();

            ImGui.SetCursorPos(new Vector2(start.X, start.Y + MathF.Round((height - badge) / 2f)));
            IconBadge(PageIcon(page), badge);

            var x = start.X + cardInner;
            for (var i = chips.Count - 1; i >= 0; i--)
            {
                var size = ChipSize(chips[i]);
                x -= size.X;
                ImGui.SetCursorPos(new Vector2(x, start.Y + MathF.Round((height - size.Y) / 2f)));
                Chip(chips[i]);
                x -= 8f * S;
            }

            ImGui.SetCursorPos(new Vector2(start.X, start.Y + height));
            ImGui.Dummy(new Vector2(cardInner, 0f));
        }, glow: true);
    }

    /// <summary>L'icône d'une page, celle de la barre latérale.</summary>
    private static FontAwesomeIcon PageIcon(Page target)
    {
        foreach (var (_, items) in Navigation)
        {
            foreach (var (itemPage, icon, _) in items)
            {
                if (itemPage == target)
                    return icon;
            }
        }

        return FontAwesomeIcon.Cog;
    }

    // ------------------------------------------------------------------ Contrôles

    /// <summary>Interrupteur arrondi (remplace la case à cocher).</summary>
    private bool Switch(string id, ref bool value)
    {
        var size = SwitchSize;
        var min = ImGui.GetCursorScreenPos();
        var key = ImGui.GetID(id);
        var clicked = ImGui.InvisibleButton(id, size);
        if (clicked)
            value = !value;

        var hovered = ImGui.IsItemHovered();
        var trackH = SwitchTrackH;
        var trackMin = new Vector2(min.X, min.Y + (size.Y - trackH) / 2f);
        var trackMax = trackMin + new Vector2(size.X, trackH);
        var off = hovered ? new Vector4(0.29f, 0.28f, 0.33f, 1f) : new Vector4(0.23f, 0.225f, 0.265f, 1f);

        // Le bouton rond glisse et la couleur se fond d'un état à l'autre.
        var t = Animate(key, value ? 1f : 0f, 20f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(trackMin, trackMax, Col(Vector4.Lerp(off, Accent, t)), trackH / 2f);
        var radius = trackH / 2f - 2.5f * S;
        var knobX = trackMin.X + trackH / 2f + (size.X - trackH) * t;
        drawList.AddCircleFilled(new Vector2(knobX, (trackMin.Y + trackMax.Y) / 2f), radius, Col(Vector4.Lerp(White, OnAccent, t)));
        return clicked;
    }

    /// <summary>Bouton plein (couleur d'accent) ou secondaire, avec icône.</summary>
    private bool FancyButton(string id, string label, FontAwesomeIcon? icon, Vector2 size, bool primary, bool danger = false)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();

        if (primary)
        {
            // Plein, de la couleur d'accent.
            var bg = held ? Vector4.Lerp(Accent, OnAccent, 0.22f) : hovered ? Vector4.Lerp(Accent, OnAccent, 0.10f) : Accent;
            bg.W = 1f;
            drawList.AddRectFilled(min, max, Col(bg), 12f * S);
            DrawIconLabel(icon, label, min, max, OnAccent);
        }
        else
        {
            // Pilule discrète, comme les étiquettes.
            var rounding = (max.Y - min.Y) / 2f;
            drawList.AddRectFilled(min, max, Col(held ? AccentSoft : hovered ? InputBgHover : InputBg), rounding);
            DrawIconLabel(icon, label, min, max, hovered && danger ? Danger : TextMain);
        }

        return clicked;
    }

    /// <summary>Bouton icône sans fond (fond léger au survol).</summary>
    private bool IconButton(string id, FontAwesomeIcon icon, bool disabled = false, bool active = false, Vector4? hoverColor = null)
    {
        var size = new Vector2(FrameH, FrameH);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size) && !disabled;
        var hovered = ImGui.IsItemHovered() && !disabled;
        var drawList = ImGui.GetWindowDrawList();

        if (active)
            drawList.AddCircleFilled(min + size / 2f, size.X / 2f, Col(AccentSoft), 32);
        else if (hovered)
            drawList.AddCircleFilled(min + size / 2f, size.X / 2f, Col(InputBg), 32);

        var color = disabled ? TextDim with { W = 0.30f }
            : active ? Accent
            : hovered ? hoverColor ?? TextMain
            : TextDim;
        DrawIconCentered(icon, min, min + size, color);
        return clicked;
    }

    /// <summary>Sélecteur segmenté (onglets en forme de pilule).</summary>
    private int Segmented(string id, IReadOnlyList<(string Label, FontAwesomeIcon Icon)> items, int selected, float? width = null)
    {
        var w = width ?? pageWidth;
        var h = FrameH + 12f * S;
        var inset = 4f * S;
        var startLocal = ImGui.GetCursorPos();
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Une pilule sombre ; le choix en cours est une pilule pleine de la couleur d'accent qui glisse jusqu'à lui.
        var outer = h / 2f;
        var inner = (h - inset * 2) / 2f;
        drawList.AddRectFilled(start, start + new Vector2(w, h), Col(CardBg), outer);
        drawList.AddRect(start, start + new Vector2(w, h), Col(Line), outer);

        var segmentW = (w - inset * 2) / items.Count;
        var slide = Animate(ImGui.GetID(id), selected, 18f);
        var pillMin = start + new Vector2(inset + slide * segmentW, inset);
        var pillMax = pillMin + new Vector2(segmentW, h - inset * 2);
        drawList.AddRectFilled(pillMin, pillMax, Col(Accent), inner);

        for (var i = 0; i < items.Count; i++)
        {
            var min = start + new Vector2(inset + i * segmentW, inset);
            var max = min + new Vector2(segmentW, h - inset * 2);
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"{id}_{i}", max - min))
                selected = i;

            var hovered = ImGui.IsItemHovered();
            var active = i == selected;
            if (!active && hovered)
                drawList.AddRectFilled(min, max, Col(HoverOverlay), inner);

            // Le texte du choix suit la pilule : clair dessus, gris à côté.
            var over = Math.Clamp(1f - MathF.Abs(slide - i), 0f, 1f);
            var color = Vector4.Lerp(hovered ? TextMain : TextDim, OnAccent, over);
            DrawIconLabel(items[i].Icon, items[i].Label, min, max, color);
        }

        ImGui.SetCursorPos(new Vector2(startLocal.X, startLocal.Y + h));
        ImGui.Dummy(new Vector2(w, 6f * S));
        return selected;
    }

    /// <summary>Pastille arrondie (statut, étiquette).</summary>
    private void Pill(string text, Vector4 color, bool dot = false, bool centerInFrame = false)
    {
        var p = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var padX = 8f * S;
        var h = textSize.Y + 5f * S;
        var dotW = dot ? 12f * S : 0f;
        var w = textSize.X + padX * 2 + dotW;
        var top = centerInFrame ? p.Y + (FrameH - h) / 2f : p.Y;
        var min = new Vector2(p.X, top);
        var max = min + new Vector2(w, h);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, Col(color with { W = 0.14f }), h / 2f);
        drawList.AddRect(min, max, Col(color with { W = 0.45f }), h / 2f);
        if (dot)
            drawList.AddCircleFilled(new Vector2(min.X + padX + 3f * S, min.Y + h / 2f), 3f * S, Col(color));
        drawList.AddText(new Vector2(min.X + padX + dotW, min.Y + 2.5f * S), Col(color), text);

        ImGui.Dummy(new Vector2(w, centerInFrame ? FrameH : h));
    }
}
