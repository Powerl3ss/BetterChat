using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>La barre d'onglets et la barre de recherche.</summary>
public sealed partial class ChatWindow
{
    private const float TabPadding = EdgeMargin;
    private const float BadgePadding = 5f;
    private const float BadgeGap = 6f;

    private int menuTab = -1;
    private bool searchOpen;
    private bool focusSearch;
    private string searchText = string.Empty;

    // Soulignement de l'onglet actif : il glisse jusqu'au nouvel onglet.
    private float underlineX;
    private float underlineW;
    private bool underlineReady;

    private void DrawTabBar(Vector2 size, float height)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var iconSize = IconButtonSize;
        var iconY = MathF.Round((height - iconSize) / 2f);
        var iconGap = 2f * S;
        var settingsX = RightButtonX(size.X, FontAwesomeIcon.Cog, iconSize);
        var searchX = settingsX - iconSize - iconGap;
        var available = searchX - 4f * S;

        // Si les onglets ne tiennent pas, ils partagent la place et leur nom est coupé.
        var count = plugin.Feeds.Count;
        var widths = new float[count];
        var total = 0f;
        for (var i = 0; i < count; i++)
        {
            widths[i] = TabWidth(plugin.Feeds[i]);
            total += widths[i];
        }

        var shrink = total > available ? available / total : 1f;
        var x = 0f; // chaque onglet a sa marge intérieure : le texte du premier s'aligne sur les messages
        var activeIndex = Math.Clamp(Cfg.ActiveTab, 0, count - 1);

        for (var i = 0; i < count; i++)
        {
            var width = MathF.Floor(widths[i] * shrink);
            DrawTab(i, new Vector2(x, 0f), new Vector2(width, height), i == activeIndex);

            if (i == activeIndex)
                MoveUnderline(x, width);

            x += width;
        }

        // Soulignement de l'onglet actif et ligne sous la barre.
        drawList.AddLine(origin + new Vector2(0, height), origin + new Vector2(size.X, height), Col(Line));
        var lineTop = origin.Y + height - 2f * S;
        drawList.AddRectFilled(new Vector2(origin.X + underlineX + TabPadding * S, lineTop),
                               new Vector2(origin.X + underlineX + underlineW - TabPadding * S, origin.Y + height),
                               Col(Accent), 2f * S);

        DrawTabMenu();

        // Boutons à droite.
        ImGui.SetCursorPos(new Vector2(searchX, iconY));
        if (IconButton("##search", FontAwesomeIcon.Search, iconSize, "Rechercher dans cet onglet (Ctrl + F)", searchOpen))
            ToggleSearch();

        ImGui.SetCursorPos(new Vector2(settingsX, iconY));
        if (IconButton("##settings", FontAwesomeIcon.Cog, iconSize, "Réglages\nClic droit sur un onglet : ses options"))
            plugin.ToggleConfigUi();

        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false))
            ToggleSearch();
    }

    private float TabWidth(ChatFeed feed)
    {
        var width = ImGui.CalcTextSize(feed.Settings.Name).X + TabPadding * 2f * S;
        var badge = BadgeText(feed);
        if (badge != null)
            width += ImGui.CalcTextSize(badge).X + (BadgePadding * 2f + BadgeGap) * S;
        return width;
    }

    /// <summary>Le nombre de messages non lus. Une mention ou un message privé compte double : il s'affiche en rouge.</summary>
    private static string? BadgeText(ChatFeed feed)
    {
        var count = feed.UnreadMentions > 0 ? feed.UnreadMentions : feed.Unread;
        return count switch
        {
            0 => null,
            > 99 => "99+",
            _ => count.ToString(),
        };
    }

    private void DrawTab(int index, Vector2 pos, Vector2 size, bool active)
    {
        var feed = plugin.Feeds[index];
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorPos(pos);
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        if (ImGui.InvisibleButton($"##tab{index}", size))
            plugin.SelectTab(index);

        var hovered = ImGui.IsItemHovered();
        if (hovered && !active)
            drawList.AddRectFilled(min + new Vector2(2f * S, 4f * S), max - new Vector2(2f * S, 4f * S), Col(HoverOverlay), 7f * S);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            menuTab = index;
            ImGui.OpenPopup("##tabMenu");
        }

        var badge = BadgeText(feed);
        var badgeW = badge != null ? ImGui.CalcTextSize(badge).X + BadgePadding * 2f * S : 0f;
        var textY = min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2f;
        var textX = min.X + TabPadding * S;
        var textEnd = max.X - TabPadding * S - (badge != null ? badgeW + BadgeGap * S : 0f);

        // Un peu plus large que le texte, pour ne pas couper son contour.
        var outlineRoom = 2f * S;
        drawList.PushClipRect(new Vector2(textX - outlineRoom, min.Y), new Vector2(textEnd + outlineRoom, max.Y), true);
        DrawOutlinedText(drawList, new Vector2(textX, textY), active || hovered ? TextMain : TextDim, feed.Settings.Name);
        drawList.PopClipRect();

        if (badge != null)
        {
            var color = feed.UnreadMentions > 0 ? Danger : Accent;
            var badgeMin = new Vector2(textEnd + BadgeGap * S, textY);
            var badgeMax = badgeMin + new Vector2(badgeW, ImGui.GetTextLineHeight());
            drawList.AddRectFilled(badgeMin, badgeMax, Col(color with { W = 0.85f }), ImGui.GetTextLineHeight() / 2f);
            drawList.AddText(badgeMin + new Vector2(BadgePadding * S, 0f), Col(new Vector4(0.05f, 0.06f, 0.09f, 1f)), badge);
        }

        if (hovered && size.X < ImGui.CalcTextSize(feed.Settings.Name).X + TabPadding * 2f * S)
            ImGui.SetTooltip(feed.Settings.Name);
    }

    private void MoveUnderline(float x, float width)
    {
        if (!underlineReady)
        {
            underlineX = x;
            underlineW = width;
            underlineReady = true;
            return;
        }

        var t = 1f - MathF.Exp(-18f * Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f));
        underlineX += (x - underlineX) * t;
        underlineW += (width - underlineW) * t;
    }

    private void DrawTabMenu()
    {
        if (!ImGui.BeginPopup("##tabMenu"))
            return;

        if (menuTab >= 0 && menuTab < plugin.Feeds.Count)
        {
            var feed = plugin.Feeds[menuTab];
            ImGui.TextDisabled(feed.Settings.Name);
            ImGui.Separator();

            if (ImGui.MenuItem("Marquer comme lu"))
                feed.MarkRead();

            if (ImGui.MenuItem("Options de l'onglet"))
                plugin.OpenConfigAtTabs();

            ImGui.Separator();
            if (ImGui.MenuItem(Cfg.Locked ? "Déverrouiller la fenêtre" : "Verrouiller la fenêtre"))
            {
                Cfg.Locked = !Cfg.Locked;
                Cfg.Save();
            }
        }

        ImGui.EndPopup();
    }

    // ------------------------------------------------------------ Recherche

    private void ToggleSearch()
    {
        searchOpen = !searchOpen;
        focusSearch = searchOpen;
        if (!searchOpen)
            ApplySearch(string.Empty);
    }

    private void ApplySearch(string text)
    {
        searchText = text;
        foreach (var feed in plugin.Feeds)
            feed.SetSearch(text);
    }

    private void DrawSearchBar(Vector2 size, float top, float height)
    {
        var margin = EdgeMargin * S;
        var gap = 6f * S;
        var close = IconButtonSize;
        var closeX = RightButtonX(size.X, FontAwesomeIcon.Cog, close); // dans la même colonne que l'engrenage

        ImGui.SetCursorPos(new Vector2(margin, MathF.Round(top + (height - ImGui.GetFrameHeight()) / 2f)));
        ImGui.SetNextItemWidth(closeX - gap - margin);
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        var text = searchText;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, InputBg);
        if (ImGui.InputTextWithHint("##searchInput", "Rechercher un mot ou un joueur...", ref text, 128))
            ApplySearch(text);
        ImGui.PopStyleColor();

        // Échap quitte d'abord la zone de texte : c'est à ce moment-là qu'on ferme la recherche.
        var escape = ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Escape, false);

        ImGui.SetCursorPos(new Vector2(closeX, MathF.Round(top + (height - close) / 2f)));
        if (IconButton("##closeSearch", FontAwesomeIcon.Times, close, "Fermer la recherche") || escape)
            ToggleSearch();
    }
}
