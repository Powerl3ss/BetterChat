using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using BetterChat.Chat;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>La barre d'onglets et la barre de recherche.</summary>
public sealed partial class ChatWindow
{
    private const float TabPadding = EdgeMargin;
    private const float SearchRevealSpeed = 14f;
    private const float BadgeGap = 6f;
    private const float BadgeDotSize = 6f;
    private const float BadgeDotGap = 3f;

    private readonly ShortcutKey searchShortcut = new(Dalamud.Game.ClientState.Keys.VirtualKey.F);
    private int menuTab = -1;
    private bool searchOpen;
    private bool focusSearch;
    private string searchText = string.Empty;

    // Ouverture de la barre de recherche : 0 = fermée, 1 = entièrement sortie.
    private float searchReveal;

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
        var pinX = settingsX - iconSize - iconGap;
        var searchX = pinX - iconSize - iconGap;
        var available = searchX - 4f * S;

        // Si les onglets ne tiennent pas, ils partagent la place et leur nom est coupé.
        var count = plugin.Feeds.Count;
        var widths = new float[count];
        var total = 0f;
        for (var i = 0; i < count; i++)
        {
            // Un onglet détaché a sa propre fenêtre : il n'a plus sa place dans la barre.
            widths[i] = plugin.Feeds[i].Settings.Detached ? 0f : TabWidth(plugin.Feeds[i]);
            total += widths[i];
        }

        var shrink = total > available ? available / total : 1f;
        var x = 0f; // chaque onglet a sa marge intérieure : le texte du premier s'aligne sur les messages
        var activeIndex = Math.Clamp(Cfg.ActiveTab, 0, count - 1);

        for (var i = 0; i < count; i++)
        {
            if (plugin.Feeds[i].Settings.Detached)
                continue;

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

        ImGui.SetCursorPos(new Vector2(pinX, iconY));
        if (IconButton("##pin", FontAwesomeIcon.Thumbtack, iconSize, Cfg.Locked ? "Position verrouillée\nClic : déverrouiller" : "Verrouiller la position", Cfg.Locked))
        {
            Cfg.Locked = !Cfg.Locked;
            Cfg.Save();
        }

        ImGui.SetCursorPos(new Vector2(settingsX, iconY));
        if (IconButton("##settings", FontAwesomeIcon.Cog, iconSize, "Réglages\nClic droit sur un onglet : ses options"))
            plugin.ToggleConfigUi();

        // Hors d'une zone de texte, Dalamud ne transmet pas la touche à ImGui : voir ShortcutKey. Pendant la saisie,
        // c'est l'inverse : le jeu ne la voit plus, et seul ImGui la reçoit.
        var searchPressed = searchShortcut.Pressed();
        if (ImGui.GetIO().WantTextInput)
            searchPressed = ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false);
        var overChat = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows);
        if (searchPressed && overChat)
            ToggleSearch();
    }

    private float TabWidth(ChatFeed feed)
    {
        var width = ImGui.CalcTextSize(feed.Settings.Name).X + TabPadding * 2f * S;
        var badge = BadgeText(feed);
        if (badge != null)
            width += BadgeWidth(badge) + BadgeGap * S;

        return width;
    }

    /// <summary>Le nombre de messages non lus. Une mention ou un message privé passe devant : il s'affiche en rouge.</summary>
    private static string? BadgeText(ChatFeed feed)
    {
        var count = feed.UnreadMentions > 0 ? feed.UnreadMentions : feed.Unread;
        return count switch
        {
            0 => null,
            > 9 => "9+",
            _ => count.ToString(),
        };
    }

    /// <summary>Mesure et place les chiffres avec la police active : voir <see cref="BadgeLayout"/>.</summary>
    private static unsafe BadgeLayout MeasureBadge(string text)
    {
        var font = ImGui.GetFont();
        var scale = font.FontSize > 0f ? ImGui.GetFontSize() / font.FontSize : 1f;

        GlyphBox Box(char c)
        {
            var glyph = font.FindGlyph(c);
            return glyph != null ? new GlyphBox(glyph->X0 * scale, glyph->X1 * scale) : new GlyphBox(0f, ImGui.CalcTextSize(c.ToString()).X);
        }

        var cell = 0f;
        foreach (var digit in "0123456789")
        {
            var box = Box(digit);
            cell = MathF.Max(cell, box.Right - box.Left);
        }

        var boxes = new GlyphBox[text.Length];
        for (var i = 0; i < text.Length; i++)
            boxes[i] = Box(text[i]);

        return BadgeLayout.Compute(boxes, MathF.Ceiling(cell), MathF.Round(1f * S), 0f, 0f);
    }

    /// <summary>Haut et bas réels des chiffres, depuis la position d'écriture. La hauteur de la pastille en découle.</summary>
    internal static unsafe (float Top, float Bottom) DigitInk()
    {
        var font = ImGui.GetFont();
        var glyph = font.FindGlyph('0');
        if (glyph == null || font.FontSize <= 0f)
            return (0f, ImGui.GetTextLineHeight());

        var scale = ImGui.GetFontSize() / font.FontSize;
        return (glyph->Y0 * scale, glyph->Y1 * scale);
    }

    private static float BadgeWidth(string text) => (BadgeDotSize + BadgeDotGap) * S + MeasureBadge(text).Width;

    /// <summary>
    /// Un petit point suivi du nombre, sans fond : discret, et il se lit sur n'importe quel décor.
    /// Bleu d'ordinaire, rouge quand on te parle directement.
    /// </summary>
    /// <param name="textY">Position d'écriture du nom de l'onglet : les chiffres partagent sa ligne de base.</param>
    private void DrawBadge(string text, float left, float textY, bool urgent)
    {
        var drawList = ImGui.GetWindowDrawList();
        var layout = MeasureBadge(text);
        var (top, bottom) = DigitInk();

        var dotColor = urgent ? new Vector4(0.91f, 0.29f, 0.29f, 1f) : new Vector4(0.30f, 0.65f, 1f, 1f);
        var digitColor = urgent ? new Vector4(1f, 0.47f, 0.47f, 1f) : new Vector4(0.55f, 0.76f, 1f, 1f);

        // Mêmes position et police que le nom de l'onglet : les chiffres sont sur sa ligne, au pixel près.
        // Le point se centre sur le dessin réel des chiffres.
        var inkLeft = left + (BadgeDotSize + BadgeDotGap) * S;
        var originX = MathF.Round(inkLeft + layout.Width / 2f - layout.InkMiddle);
        var originY = MathF.Floor(textY);

        var radius = BadgeDotSize / 2f * S;
        var dotCenter = new Vector2(left + radius, MathF.Round(originY + (top + bottom) / 2f));
        if (Cfg.TextOutline && Cfg.OutlineStrength > 0f)
            drawList.AddCircleFilled(dotCenter, radius + MathF.Max(1f, S), Col(new Vector4(0f, 0f, 0f, 0.6f * Math.Clamp(Cfg.OutlineStrength, 0f, 1f))));
        drawList.AddCircleFilled(dotCenter, radius, Col(dotColor));

        for (var i = 0; i < text.Length; i++)
            DrawOutlinedText(drawList, new Vector2(originX + layout.PenX[i], originY), digitColor, text[i].ToString());
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
        var badgeW = badge != null ? BadgeWidth(badge) : 0f;
        var textY = MathF.Floor(min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2f);
        var textX = min.X + TabPadding * S;
        var textEnd = max.X - TabPadding * S - (badge != null ? badgeW + BadgeGap * S : 0f);

        // Un peu plus large que le texte, pour ne pas couper son contour.
        var outlineRoom = 2f * S;
        drawList.PushClipRect(new Vector2(textX - outlineRoom, min.Y), new Vector2(textEnd + outlineRoom, max.Y), true);
        DrawOutlinedText(drawList, new Vector2(textX, textY), active || hovered ? TextMain : TextDim, feed.Settings.Name);
        drawList.PopClipRect();

        if (badge != null)
            DrawBadge(badge, textEnd + BadgeGap * S, textY, feed.UnreadMentions > 0);

        if (hovered && size.X < ImGui.CalcTextSize(feed.Settings.Name).X + TabPadding * 2f * S)
            Tip.Show(feed.Settings.Name);
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
        if (!Tip.BeginMenu("##tabMenu"))
            return;

        if (menuTab >= 0 && menuTab < plugin.Feeds.Count)
        {
            var feed = plugin.Feeds[menuTab];
            Tip.MenuTitle(feed.Settings.Name);
            Tip.MenuSeparator();

            if (Tip.MenuItem("Marquer comme lu", FontAwesomeIcon.CheckDouble))
                feed.MarkRead();

            if (plugin.CanDetachTab && Tip.MenuItem("Détacher dans une fenêtre", FontAwesomeIcon.ExternalLinkAlt))
                plugin.SetDetached(feed.Settings, true);

            if (Tip.MenuItem("Exporter dans un fichier...", FontAwesomeIcon.FileExport))
                plugin.ExportFeed(feed);

            if (Tip.MenuItem("Options de l'onglet", FontAwesomeIcon.SlidersH))
                plugin.OpenConfigAtTabs();
        }

        Tip.EndMenu();
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
            feed.SetSearch(feed.Settings.Detached ? string.Empty : text);
    }

    /// <summary>Fait avancer l'animation et renvoie la hauteur occupée en ce moment, sur un nombre entier de pixels.</summary>
    private float UpdateSearchReveal(float fullHeight)
    {
        var target = searchOpen ? 1f : 0f;
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        searchReveal += (target - searchReveal) * (1f - MathF.Exp(-SearchRevealSpeed * dt));
        if (MathF.Abs(target - searchReveal) < 0.005f)
            searchReveal = target;

        return MathF.Round(fullHeight * searchReveal);
    }

    /// <summary>La barre glisse depuis le dessous des onglets en apparaissant, et les messages descendent pour lui faire place.</summary>
    private void DrawRevealedSearchBar(Vector2 size, float tabHeight, float shown, float fullHeight)
    {
        var origin = ImGui.GetWindowPos();
        ImGui.PushClipRect(origin + new Vector2(0f, tabHeight), origin + new Vector2(size.X, tabHeight + shown), true);
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, MathF.Max(searchReveal, 0.001f) * ImGui.GetStyle().Alpha))
            DrawSearchBar(size, tabHeight + shown - fullHeight, fullHeight);
        ImGui.PopClipRect();
    }

    private static void DrawSearchHelp()
    {
        using var tooltip = Tip.Begin();
        Tip.Title("Recherche avancée");
        Tip.Note("Des mots : dans le nom et dans le texte");
        Tip.Note("de:Jean : par joueur");
        Tip.Note("canal:CL : par canal (groupe, mp, crier...)");
        Tip.Note("date:hier ou date:03/11 : par jour");
        Tip.Hint("Des guillemets gardent une expression entière");
    }

    private void DrawSearchBar(Vector2 size, float top, float height)
    {
        var margin = EdgeMargin * S;
        var gap = 6f * S;
        var close = IconButtonSize;
        var closeX = RightButtonX(size.X, FontAwesomeIcon.Cog, close); // dans la même colonne que l'engrenage

        ImGui.SetCursorPos(new Vector2(margin, MathF.Round(top + (height - ImGui.GetFrameHeight()) / 2f)));
        ImGui.SetNextItemWidth(closeX - gap - margin);
        // Attend que la barre soit assez sortie : une zone encore hors du cadre ne peut pas prendre le focus.
        if (focusSearch && searchReveal >= 0.5f)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        var text = searchText;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, InputBg);
        if (ImGui.InputTextWithHint("##searchInput", "Rechercher... (de:Jean canal:CL date:hier)", ref text, 128))
            ApplySearch(text);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered() && !ImGui.IsItemActive())
            DrawSearchHelp();

        // Échap quitte d'abord la zone de texte : c'est à ce moment-là qu'on ferme la recherche.
        var escape = ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Escape, false);

        ImGui.SetCursorPos(new Vector2(closeX, MathF.Round(top + (height - close) / 2f)));
        if (IconButton("##closeSearch", FontAwesomeIcon.Times, close, "Fermer la recherche") || escape)
            ToggleSearch();
    }
}
