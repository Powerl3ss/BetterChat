using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>La barre d'onglets et la barre de recherche.</summary>
public sealed partial class ChatWindow
{
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
        var iconSize = height - 6f * S;
        var iconsW = (iconSize + 2f * S) * 3f + 6f * S;
        var pad = 8f * S;
        var available = size.X - iconsW - pad;

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
        var x = pad;
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
        drawList.AddRectFilled(new Vector2(origin.X + underlineX + 6f * S, lineTop),
                               new Vector2(origin.X + underlineX + underlineW - 6f * S, origin.Y + height),
                               Col(Accent), 2f * S);

        DrawTabMenu();

        // Boutons à droite.
        ImGui.SetCursorPos(new Vector2(size.X - (iconSize + 2f * S) * 3f - 4f * S, 3f * S));
        if (IconButton("##addTab", FontAwesomeIcon.Plus, iconSize, "Ajouter un onglet"))
            AddTab();

        ImGui.SameLine(0, 2f * S);
        if (IconButton("##search", FontAwesomeIcon.Search, iconSize, "Rechercher dans cet onglet (Ctrl + F)", searchOpen))
            ToggleSearch();

        ImGui.SameLine(0, 2f * S);
        if (IconButton("##settings", FontAwesomeIcon.Cog, iconSize, "Réglages\nClic droit sur un onglet : ses options"))
            plugin.ToggleConfigUi();

        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false))
            ToggleSearch();
    }

    private float TabWidth(ChatFeed feed)
    {
        var width = ImGui.CalcTextSize(feed.Settings.Name).X + 20f * S;
        var badge = BadgeText(feed);
        if (badge != null)
            width += ImGui.CalcTextSize(badge).X + 14f * S;
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
        var badgeW = badge != null ? ImGui.CalcTextSize(badge).X + 10f * S : 0f;
        var textY = min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2f;
        var textX = min.X + 10f * S;
        var textEnd = max.X - 4f * S - (badge != null ? badgeW + 4f * S : 0f);

        drawList.PushClipRect(new Vector2(textX, min.Y), new Vector2(textEnd, max.Y), true);
        DrawOutlinedText(drawList, new Vector2(textX, textY), active || hovered ? TextMain : TextDim, feed.Settings.Name);
        drawList.PopClipRect();

        if (badge != null)
        {
            var color = feed.UnreadMentions > 0 ? Danger : Accent;
            var badgeMin = new Vector2(textEnd + 4f * S, min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2f);
            var badgeMax = badgeMin + new Vector2(badgeW, ImGui.GetTextLineHeight());
            drawList.AddRectFilled(badgeMin, badgeMax, Col(color with { W = 0.85f }), ImGui.GetTextLineHeight() / 2f);
            drawList.AddText(badgeMin + new Vector2(5f * S, 0f), Col(new Vector4(0.05f, 0.06f, 0.09f, 1f)), badge);
        }

        if (hovered && size.X < ImGui.CalcTextSize(feed.Settings.Name).X + 20f * S)
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

    private void AddTab()
    {
        Cfg.Tabs.Add(new ChatTabSettings { Name = $"Onglet {Cfg.Tabs.Count + 1}", Kinds = ChatTabPresets.General().Kinds.ToList() });
        Cfg.ActiveTab = Cfg.Tabs.Count - 1;
        Cfg.Save();
        plugin.RebuildFeeds();
        plugin.OpenConfigAtTabs();
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
        var pad = 8f * S;
        var close = height - 8f * S;

        ImGui.SetCursorPos(new Vector2(pad, top + (height - ImGui.GetFrameHeight()) / 2f));
        ImGui.SetNextItemWidth(size.X - pad * 3f - close);
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

        ImGui.SameLine(0, pad);
        ImGui.SetCursorPosY(top + 4f * S);
        if (IconButton("##closeSearch", FontAwesomeIcon.Times, close, "Fermer la recherche") || escape)
            ToggleSearch();
    }
}
