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

    private static readonly Vector4 WindowBg = new(0.071f, 0.078f, 0.106f, 0.985f);
    // Légèrement transparents pour laisser deviner le fond animé.
    private static readonly Vector4 SidebarBg = new(0.055f, 0.060f, 0.082f, 0.62f);
    private static readonly Vector4 CardBg = new(0.106f, 0.114f, 0.153f, 0.84f);
    private static readonly Vector4 CardBgHover = new(0.135f, 0.145f, 0.190f, 1f);
    private static readonly Vector4 InputBg = new(0.140f, 0.150f, 0.200f, 1f);
    private static readonly Vector4 InputBgHover = new(0.170f, 0.180f, 0.240f, 1f);
    private static readonly Vector4 Line = new(1f, 1f, 1f, 0.075f);
    private static readonly Vector4 HoverOverlay = new(1f, 1f, 1f, 0.06f);
    private static readonly Vector4 TextMain = new(0.93f, 0.94f, 0.97f, 1f);
    private static readonly Vector4 TextDim = new(0.60f, 0.63f, 0.71f, 1f);
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Success = new(0.38f, 0.85f, 0.56f, 1f);
    private static readonly Vector4 Danger = new(1f, 0.47f, 0.47f, 1f);

    private Vector4 Accent => Cfg.AccentColor with { W = 1f };
    private Vector4 AccentSoft => Cfg.AccentColor with { W = 0.20f };
    private Vector4 AccentBorder => Cfg.AccentColor with { W = 0.60f };

    private static readonly Vector4 DarkInk = new(0.07f, 0.08f, 0.11f, 1f);
    private static readonly Vector4 SunColor = new(1f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 MoonColor = new(0.78f, 0.84f, 1f, 1f);

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

    /// <summary>
    /// Carte arrondie : le contenu est dessiné d'abord (calque 1), puis le fond derrière (calque 0)
    /// une fois la hauteur connue. Renvoie la position Y locale du bas de la carte.
    /// </summary>
    private float Card(Action content, float? width = null, bool spacingAfter = true)
    {
        var pad = 16f * S;
        var w = width ?? pageWidth;
        var drawList = ImGui.GetWindowDrawList();
        var startLocal = ImGui.GetCursorPos();
        var start = ImGui.GetCursorScreenPos();

        var previousInner = cardInner;
        cardInner = w - pad * 2;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorPos(startLocal + new Vector2(pad, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(startLocal.X + pad + cardInner);
        content();
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var bottom = ImGui.GetItemRectMax().Y + pad;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, new Vector2(start.X + w, bottom), Col(CardBg), 12f * S);
        drawList.AddRect(start, new Vector2(start.X + w, bottom), Col(Line), 12f * S);
        drawList.ChannelsMerge();

        cardInner = previousInner;
        var bottomLocal = startLocal.Y + (bottom - start.Y);
        ImGui.SetCursorPos(new Vector2(startLocal.X, bottomLocal));
        ImGui.Dummy(new Vector2(w, spacingAfter ? 6f * S : 0f));
        return bottomLocal;
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
        ImGui.Dummy(new Vector2(cardInner, 2f * S));
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

    /// <summary>Petit titre de section, avec un texte optionnel aligné à droite.</summary>
    private void SectionTitle(string title, string? right = null)
    {
        ImGui.Dummy(new Vector2(0, 4f * S));
        var start = ImGui.GetCursorPos();
        ImGui.TextUnformatted(title);
        if (right != null)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(start.X + pageWidth - TextWidth(right));
            ImGui.TextColored(TextDim, right);
        }
        ImGui.Dummy(new Vector2(0, 2f * S));
    }

    /// <summary>Libellé en petites capitales grisées, comme un en-tête de carte.</summary>
    private static void CardLabel(string text)
    {
        ImGui.TextColored(TextDim, text.ToUpperInvariant());
    }

    /// <summary>En-tête de page : salutation, grand titre, sous-titre et statistiques à droite.</summary>
    private void Hero(string title, string subtitle, string? statTop = null, string? statBottom = null)
    {
        var start = ImGui.GetCursorPos();

        using (heroFont.Push())
            ImGui.TextUnformatted(title);
        ImGui.TextColored(TextDim, subtitle);
        var end = ImGui.GetCursorPos();

        var lineY = start.Y + 2f * S;
        if (statTop != null)
        {
            ImGui.SetCursorPos(new Vector2(start.X + pageWidth - TextWidth(statTop), lineY));
            ImGui.TextUnformatted(statTop);
        }
        if (statBottom != null)
        {
            ImGui.SetCursorPos(new Vector2(start.X + pageWidth - TextWidth(statBottom), lineY + ImGui.GetTextLineHeightWithSpacing()));
            ImGui.TextColored(TextDim, statBottom);
        }

        ImGui.SetCursorPos(end);
        ImGui.Dummy(new Vector2(pageWidth, 10f * S));
    }

    /// <summary>Journée de 8h00 à 19h59 (heure du PC), soirée de 20h00 à 7h59.</summary>
    private static bool IsDaytime => DateTime.Now.Hour is >= 8 and < 20;

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
        var off = hovered ? new Vector4(0.30f, 0.32f, 0.40f, 1f) : new Vector4(0.24f, 0.26f, 0.33f, 1f);

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
            var bg = held ? Vector4.Lerp(Accent, OnAccent, 0.22f) : hovered ? Vector4.Lerp(Accent, OnAccent, 0.10f) : Accent;
            bg.W = 1f;
            drawList.AddRectFilled(min, max, Col(bg), 9f * S);
            DrawIconLabel(icon, label, min, max, OnAccent);
        }
        else
        {
            drawList.AddRectFilled(min, max, Col(held ? InputBgHover : hovered ? CardBgHover : InputBg), 9f * S);
            drawList.AddRect(min, max, Col(hovered && danger ? Danger with { W = 0.6f } : Line), 9f * S);
            DrawIconLabel(icon, label, min, max, hovered && danger ? Danger : TextMain);
        }

        return clicked;
    }

    /// <summary>Bouton icône sans fond (fond léger au survol).</summary>
    private bool IconButton(string id, FontAwesomeIcon icon, string? tooltip = null, bool disabled = false,
                            bool active = false, Vector4? hoverColor = null)
    {
        var size = new Vector2(FrameH, FrameH);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size) && !disabled;
        var hovered = ImGui.IsItemHovered() && !disabled;
        var drawList = ImGui.GetWindowDrawList();

        if (active)
            drawList.AddRectFilled(min, min + size, Col(AccentSoft), 7f * S);
        else if (hovered)
            drawList.AddRectFilled(min, min + size, Col(HoverOverlay), 7f * S);

        var color = disabled ? TextDim with { W = 0.30f }
            : active ? Accent
            : hovered ? hoverColor ?? TextMain
            : TextDim;
        DrawIconCentered(icon, min, min + size, color);

        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Sélecteur segmenté (onglets en forme de pilule).</summary>
    private int Segmented(string id, IReadOnlyList<(string Label, FontAwesomeIcon Icon)> items, int selected, float? width = null)
    {
        var w = width ?? pageWidth;
        var h = FrameH + 14f * S;
        var inset = 4f * S;
        var startLocal = ImGui.GetCursorPos();
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(start, start + new Vector2(w, h), Col(CardBg), 12f * S);
        drawList.AddRect(start, start + new Vector2(w, h), Col(Line), 12f * S);

        var segmentW = (w - inset * 2) / items.Count;

        // Pastille de sélection : elle glisse jusqu'à l'onglet choisi.
        var slide = Animate(ImGui.GetID(id), selected, 18f);
        var pillMin = start + new Vector2(inset + slide * segmentW, inset);
        var pillMax = pillMin + new Vector2(segmentW, h - inset * 2);
        drawList.AddRectFilled(pillMin, pillMax, Col(AccentSoft), 9f * S);
        drawList.AddRect(pillMin, pillMax, Col(AccentBorder), 9f * S);

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
                drawList.AddRectFilled(min, max, Col(HoverOverlay), 9f * S);

            DrawIconLabel(items[i].Icon, items[i].Label, min, max, active || hovered ? TextMain : TextDim);
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
