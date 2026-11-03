using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using BetterChat.Chat;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>
/// La liste des messages. Elle peut en contenir des milliers : chaque message est mis en page une fois,
/// et seuls ceux à l'écran sont dessinés.
/// </summary>
public sealed partial class ChatWindow
{
    private const float ScrollStickMargin = 4f;
    private const int ScrollSettleFrameCount = 3;
    private const float JumpRevealSpeed = 16f;

    // Air entre la liste et la barre d'onglets, puis la zone de saisie.
    private const float ListPaddingTop = 8f;
    private const float ListPaddingBottom = 6f;
    private const int WidthCacheLimit = 20000;

    /// <summary>Mise en page d'un message pour une largeur et une police données.</summary>
    private sealed class LineLayout
    {
        public int Key;
        public List<ChatSpan> Spans = new();
        public LaidOutText Text = LaidOutText.Empty;
    }

    // Largeur de chaque mot, gardée entre deux mises en page : un redimensionnement reste fluide.
    private readonly Dictionary<string, float> widths = new();
    private float widthsFontPx;

    private readonly LineStack stack = new();
    private readonly List<bool> headers = new();
    private ChatFeed? stackFeed;
    private int stackRebuild = -1;
    private int layoutKey;
    private float lineHeight;
    private float textWidth;
    private float textLeft;
    private float timeColumn;

    private bool stickToBottom = true;
    private int unseenBelow;
    private int lastCountForUnseen;
    private bool scrollToBottom;
    private float previousScrollMax;
    private float jumpReveal;
    private Vector2 jumpButtonMin;
    private Vector2 jumpButtonMax;
    private float previousListHeight;
    private int scrollSettleFrames;

    private ChatLink? hoveredLink;
    private ChatLink? hoveredLinkLastFrame;
    private ChatLine? contextLine;
    private ChatLink? contextLink;
    private ChatLink? pendingUrl;

    private void DrawMessages(Vector2 size)
    {
        using var child = ImRaii.Child("##messages", size, false, ImGuiWindowFlags.NoMove);
        if (!child.Success)
            return;

        var feed = plugin.ActiveFeed;
        lineHeight = ImGui.GetTextLineHeight();

        var scrollbar = ImGui.GetStyle().ScrollbarSize;
        textLeft = EdgeMargin * S;
        textWidth = MathF.Max(size.X - textLeft * 2f - scrollbar, 40f);

        PrepareStack(feed);

        if (stack.Count == 0)
        {
            DrawEmptyState(size, feed);
            return;
        }

        // Toute la hauteur est réservée : la barre de défilement est juste, même sans tout dessiner.
        ImGui.Dummy(new Vector2(size.X - scrollbar, ContentHeight));

        // Collé en bas, la position vient directement du calcul : ImGui applique le défilement et la nouvelle hauteur
        // de la liste avec une image de retard, ce qui faisait sautiller les messages quand elle change de taille.
        var paddingTop = ListPaddingTop * S;
        var scrollY = stickToBottom || scrollToBottom ? MathF.Max(ContentHeight - size.Y, 0f) : ImGui.GetScrollY();
        var windowPos = ImGui.GetWindowPos();
        var origin = new Vector2(windowPos.X, windowPos.Y - scrollY + paddingTop);

        // Le bouton « Revenir en bas » flotte au-dessus des messages : il reçoit seul les clics à cet endroit.
        var overJumpButton = !stickToBottom && jumpReveal > 0f && ImGui.IsMouseHoveringRect(jumpButtonMin, jumpButtonMax, false);
        var hoverable = ImGui.IsWindowHovered() && !overJumpButton;
        hoveredLinkLastFrame = hoveredLink;
        hoveredLink = null;

        var visibleTop = scrollY - paddingTop;
        var visibleBottom = visibleTop + size.Y;
        for (var i = stack.FirstVisible(visibleTop); i < stack.Count && stack.Top(i) < visibleBottom; i++)
            DrawLine(feed.Visible[i], i, origin, size.X, hoverable);

        // Le curseur main quand on survole un lien.
        if (hoveredLink != null)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        DrawLinePopups();
        UpdateScrolling(feed, size);
    }

    private float ContentHeight => stack.Total + (ListPaddingTop + ListPaddingBottom) * S;

    private void DrawEmptyState(Vector2 size, ChatFeed feed)
    {
        var text = feed.Search.Length > 0 ? "Aucun message trouvé" : "Aucun message pour l'instant";
        var textSize = ImGui.CalcTextSize(text);
        DrawOutlinedText(ImGui.GetWindowDrawList(), ImGui.GetWindowPos() + (size - textSize) / 2f, TextDim, text);
    }

    // ------------------------------------------------------------ Mise en page

    private void PrepareStack(ChatFeed feed)
    {
        // Présentation moderne : l'heure a sa colonne à gauche, et tous les textes commencent au même endroit.
        timeColumn = Cfg.Style == ChatStyle.Modern && Cfg.ShowTimestamps ? MathF.Ceiling(Measure("00:00") + 6f * S) : 0f;

        var key = HashCode.Combine(MathF.Round(textWidth), lineHeight, Cfg.Style, Cfg.ShowTimestamps, Cfg.ShowChannelTags, Cfg.SecondaryColor);
        var rebuild = !ReferenceEquals(stackFeed, feed) || stackRebuild != feed.RebuildCount || layoutKey != key;

        if (rebuild)
        {
            // Changer d'onglet ramène en bas. Une nouvelle mise en page garde le bas s'il y était.
            if (!ReferenceEquals(stackFeed, feed) || stickToBottom)
                scrollToBottom = true;

            stackFeed = feed;
            stackRebuild = feed.RebuildCount;
            layoutKey = key;
            stack.Clear();
            headers.Clear();
            lastCountForUnseen = feed.Visible.Count;
            unseenBelow = 0;
        }

        // Nouveaux messages depuis la dernière image : on ne met en page que ceux-là.
        var lines = feed.Visible;
        for (var i = stack.Count; i < lines.Count; i++)
        {
            var header = Cfg.Style == ChatStyle.Modern && ChatGrouping.StartsGroup(i > 0 ? lines[i - 1] : null, lines[i]);
            headers.Add(header);
            stack.Add(LineHeightOf(lines[i], i, header));
        }
    }

    private float LineHeightOf(ChatLine line, int index, bool header)
    {
        var layout = LayoutOf(line);
        var height = layout.Text.LineCount * lineHeight;
        var flows = index > 0 && !header && Cfg.Style == ChatStyle.Modern && ChatGrouping.HasAuthorHeader(line);

        if (header)
            height += lineHeight + 1f * S;

        // Plus d'air entre deux auteurs qu'entre deux messages du même auteur.
        height += header ? 10f * S : flows ? 2f * S : 3f * S;
        return height;
    }

    private LineLayout LayoutOf(ChatLine line)
    {
        if (line.LayoutCache is LineLayout cached && cached.Key == layoutKey)
            return cached;

        var layout = new LineLayout { Key = layoutKey, Spans = DisplaySpans(line) };
        layout.Text = TextLayout.Wrap(layout.Spans, textWidth - timeColumn, Measure, 0f, Cfg.Style == ChatStyle.Compact ? 12f * S : 0f);
        line.LayoutCache = layout;
        return layout;
    }

    /// <summary>En mode moderne, le texte seul (le nom est dans l'en-tête). En mode compact : heure, canal, nom, texte.</summary>
    private List<ChatSpan> DisplaySpans(ChatLine line)
    {
        var hasHeader = Cfg.Style == ChatStyle.Modern && ChatGrouping.HasAuthorHeader(line);
        if (hasHeader)
            return line.Body;

        var spans = new List<ChatSpan>(line.Body.Count + line.Sender.Count + 3);
        if (Cfg.ShowTimestamps && Cfg.Style == ChatStyle.Compact)
            spans.Add(new ChatSpan(line.TimeUtc.ToLocalTime().ToString("HH:mm") + " ", DimRgba));

        if (Cfg.ShowChannelTags && Cfg.Style == ChatStyle.Compact)
            spans.Add(new ChatSpan($"[{ChatKinds.Info(line.Kind).Tag}] "));

        if (line.Sender.Count > 0 && line.SenderText.Length > 0)
        {
            spans.AddRange(line.Sender);
            spans.Add(new ChatSpan(ChatGrouping.Separator(line.Kind)));
        }

        spans.AddRange(line.Body);
        return spans;
    }

    private uint DimRgba
    {
        get
        {
            var c = Cfg.SecondaryColor;
            return ((uint)(c.X * 255f) << 24) | ((uint)(c.Y * 255f) << 16) | ((uint)(c.Z * 255f) << 8) | 0xFFu;
        }
    }

    private float Measure(string text)
    {
        if (text.Length == 0)
            return 0f;

        if (MathF.Abs(widthsFontPx - lineHeight) > 0.01f || widths.Count > WidthCacheLimit)
        {
            widths.Clear();
            widthsFontPx = lineHeight;
        }

        if (widths.TryGetValue(text, out var width))
            return width;

        width = ImGui.CalcTextSize(text).X;
        widths[text] = width;
        return width;
    }

    // ------------------------------------------------------------ Dessin d'un message

    private void DrawLine(ChatLine line, int index, Vector2 origin, float width, bool hoverable)
    {
        var top = origin.Y + stack.Top(index);
        var bottom = origin.Y + stack.Bottom(index);
        var rowMin = new Vector2(origin.X, top);
        var rowMax = new Vector2(origin.X + width, bottom);
        var drawList = ImGui.GetWindowDrawList();

        var hovered = hoverable && ImGui.IsMouseHoveringRect(rowMin, rowMax, false);
        if (hovered)
            drawList.AddRectFilled(rowMin, rowMax, Col(HoverOverlay));

        // Message qui te cite ou message privé reçu : fond teinté et trait à gauche.
        if (line.Mentioned || line.Kind == ChatKind.TellIncoming)
        {
            var mark = line.Mentioned ? Accent : plugin.Colors.Of(line.Kind);
            drawList.AddRectFilled(rowMin, rowMax, Col(mark with { W = 0.10f }));
            drawList.AddRectFilled(rowMin, new Vector2(rowMin.X + 3f * S, rowMax.Y), Col(mark with { W = 0.85f }));
        }

        var header = headers[index];
        var channel = plugin.Colors.Of(line.Kind);
        var y = top;
        var left = origin.X + textLeft;
        var textX = left + timeColumn;

        // L'heure en face de la première ligne. Un message qui suit le précédent du même auteur n'en répète pas.
        if (timeColumn > 0f && (header || !ChatGrouping.HasAuthorHeader(line)))
        {
            var timeY = header ? y + 8f * S : y;
            DrawOutlinedText(drawList, new Vector2(left, timeY), Cfg.SecondaryColor, line.TimeUtc.ToLocalTime().ToString("HH:mm"));
        }

        if (header)
        {
            y += 8f * S;
            DrawHeader(line, new Vector2(textX, y), channel, hoverable);
            y += lineHeight + 1f * S;
        }

        var layout = LayoutOf(line);
        DrawRuns(layout, new Vector2(textX, y), channel, hoverable);

        if (hovered && hoveredLink == null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            contextLine = line;
            contextLink = null;
            ImGui.OpenPopup("##lineMenu");
        }
    }

    private void DrawHeader(ChatLine line, Vector2 pos, Vector4 channel, bool hoverable)
    {
        var drawList = ImGui.GetWindowDrawList();

        // Le nom, cliquable. Pour un message privé envoyé, c'est le destinataire : une flèche l'indique.
        var x = pos.X;
        if (line.Kind == ChatKind.TellOutgoing)
        {
            DrawOutlinedText(drawList, new Vector2(x, pos.Y), channel, "À ");
            x += Measure("À ");
        }

        foreach (var span in line.Sender)
        {
            if (span.Text.Length == 0)
                continue;

            var color = span.Color != 0 ? ChannelColors.FromRgba(span.Color) : channel;
            var w = Measure(span.Text);
            var link = span.Link ?? line.Author;
            DrawRun(drawList, new Vector2(x, pos.Y), span.Text, w, color, link, hoverable);
            x += w;
        }

        // Le canal, plus discret.
        x += 8f * S;
        if (Cfg.ShowChannelTags)
        {
            var tag = ChatKinds.Info(line.Kind).Tag;
            var tagW = Measure(tag);
            var pillMin = new Vector2(x, pos.Y + 1f * S);
            var pillMax = new Vector2(x + tagW + 10f * S, pos.Y + lineHeight - 1f * S);
            drawList.AddRectFilled(pillMin, pillMax, Col(channel with { W = 0.14f }), lineHeight / 2f);
            DrawOutlinedText(drawList, new Vector2(x + 5f * S, pos.Y), channel, tag);
        }
    }

    private void DrawRuns(LineLayout layout, Vector2 pos, Vector4 channel, bool hoverable)
    {
        var drawList = ImGui.GetWindowDrawList();
        foreach (var run in layout.Text.Runs)
        {
            var span = layout.Spans[run.SpanIndex];
            var color = span.Color != 0 ? ChannelColors.FromRgba(span.Color) : channel;
            if (span.Italic)
                color = color with { W = color.W * 0.85f };

            DrawRun(drawList, pos + new Vector2(run.X, run.Line * lineHeight), run.Text, run.Width, color, span.Link, hoverable);
        }
    }

    private void DrawRun(ImDrawListPtr drawList, Vector2 pos, string text, float width, Vector4 color, ChatLink? link, bool hoverable)
    {
        DrawOutlinedText(drawList, pos, color, text);

        if (link == null)
            return;

        var max = pos + new Vector2(width, lineHeight);
        if (hoverable && ImGui.IsMouseHoveringRect(pos, max, false))
        {
            hoveredLink = link;
            HandleLinkInteraction(link);
        }

        // Les liens web sont toujours soulignés. Les autres, seulement au survol (sur tous leurs morceaux).
        if (link.Kind == LinkKind.Url || ReferenceEquals(link, hoveredLinkLastFrame) || ReferenceEquals(link, hoveredLink))
            drawList.AddLine(new Vector2(pos.X, max.Y - 1f), new Vector2(max.X, max.Y - 1f), Col(color));
    }

    /// <summary>Texte avec un contour : 8 copies sombres décalées, dessinées dessous. Il reste lisible sans fond.</summary>
    private void DrawOutlinedText(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text)
    {
        if (Cfg.TextOutline && Cfg.OutlineStrength > 0f)
        {
            var shadow = Col(new Vector4(0f, 0f, 0f, Math.Clamp(Cfg.OutlineStrength, 0f, 1f) * color.W));
            var t = MathF.Max(1f, S);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx != 0 || dy != 0)
                        drawList.AddText(pos + new Vector2(dx * t, dy * t), shadow, text);
                }
            }
        }

        drawList.AddText(pos, Col(color), text);
    }

    // ------------------------------------------------------------ Liens

    private void HandleLinkInteraction(ChatLink link)
    {
        DrawLinkTooltip(link);

        var left = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var right = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        if (!left && !right)
            return;

        contextLink = link;
        contextLine = null;

        // Un clic sur une carte l'ouvre tout de suite. Le reste propose ses actions.
        if (left && link.Kind == LinkKind.Map)
        {
            OpenMap(link);
            return;
        }

        if (left && link.Kind == LinkKind.Url)
        {
            pendingUrl = link;
            ImGui.OpenPopup("##urlConfirm");
            return;
        }

        ImGui.OpenPopup("##linkMenu");
    }

    private void DrawLinkTooltip(ChatLink link)
    {
        switch (link.Kind)
        {
            case LinkKind.Item:
                DrawItemTooltip(link.Id);
                break;
            case LinkKind.Map:
                Tip.Show($"{(link.Text.Length > 0 ? link.Text + "\n" : string.Empty)}Clic : ouvrir la carte");
                break;
            case LinkKind.Url:
                Tip.Show($"{link.Text}\nClic : ouvrir le lien (une confirmation est demandée)");
                break;
            case LinkKind.Player:
                Tip.Show($"{link.Text}{(link.World.Length > 0 ? " (" + link.World + ")" : string.Empty)}\nClic : message privé, copier, masquer");
                break;
        }
    }

    private static void DrawItemTooltip(uint itemId)
    {
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return;

        using var tooltip = Tip.Begin();

        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Value.Icon));
        if (icon.TryGetWrap(out var wrap, out _) && wrap != null)
        {
            ImGui.Image(wrap.Handle, new Vector2(40f, 40f) * S);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        Tip.Title(item.Value.Name.ExtractText());
        if (item.Value.LevelItem.RowId > 1)
            Tip.Note($"Niveau d'objet {item.Value.LevelItem.RowId}");
        Tip.Hint("Clic : copier, lier dans le chat");
        ImGui.EndGroup();
    }

    private static string ItemName(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;

    private static void OpenMap(ChatLink link)
    {
        var payload = new Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload(link.Id, link.MapId, link.RawX, link.RawY);
        Plugin.GameGui.OpenMapWithMapLink(payload);
    }

    private void DrawLinePopups()
    {
        if (Tip.BeginMenu("##lineMenu"))
        {
            if (contextLine is { } line)
            {
                if (line.Author != null && line.Author.Text != plugin.LocalName)
                {
                    Tip.MenuTitle(line.Author.TellTarget);
                    Tip.MenuSeparator();
                    DrawPlayerActions(line.Author);
                }

                if (Tip.MenuItem("Copier le message", FontAwesomeIcon.Copy))
                    ImGui.SetClipboardText(line.CopyText);
            }

            Tip.EndMenu();
        }

        if (Tip.BeginMenu("##linkMenu"))
        {
            switch (contextLink?.Kind)
            {
                case LinkKind.Player:
                    Tip.MenuTitle(contextLink.TellTarget);
                    Tip.MenuSeparator();
                    DrawPlayerActions(contextLink, separatorAfter: false);
                    break;

                case LinkKind.Item:
                    var name = ItemName(contextLink.Id);
                    Tip.MenuTitle(name);
                    Tip.MenuSeparator();
                    if (Tip.MenuItem("Lier dans le chat", FontAwesomeIcon.Link))
                    {
                        GameChat.LinkItem(contextLink.Id);
                        AppendToInput("<item>");
                    }
                    if (Tip.MenuItem("Copier le nom", FontAwesomeIcon.Copy))
                        ImGui.SetClipboardText(name);
                    break;

                case LinkKind.Map:
                    Tip.MenuTitle(contextLink.Text);
                    Tip.MenuSeparator();
                    if (Tip.MenuItem("Ouvrir la carte", FontAwesomeIcon.MapMarkedAlt))
                        OpenMap(contextLink);
                    if (Tip.MenuItem("Copier le lieu", FontAwesomeIcon.Copy))
                        ImGui.SetClipboardText(contextLink.Text);
                    break;

                case LinkKind.Url:
                    if (Tip.MenuItem("Copier le lien", FontAwesomeIcon.Copy))
                        ImGui.SetClipboardText(contextLink.Text);
                    break;
            }

            Tip.EndMenu();
        }

        DrawUrlConfirm();
    }

    private void DrawPlayerActions(ChatLink player, bool separatorAfter = true)
    {
        if (player.Text != plugin.LocalName && Tip.MenuItem("Envoyer un message privé", FontAwesomeIcon.Envelope))
        {
            plugin.Game.SwitchToTell(player);
            FocusInput();
        }

        if (Tip.MenuItem("Copier le nom", FontAwesomeIcon.Copy))
            ImGui.SetClipboardText(player.TellTarget);

        if (player.Text != plugin.LocalName && Tip.MenuItem("Masquer ses messages", FontAwesomeIcon.CommentSlash, danger: true))
            plugin.MutePlayer(player.Text);

        if (separatorAfter)
            Tip.MenuSeparator();
    }

    /// <summary>Un lien dans le chat peut mener n'importe où : l'adresse s'affiche en entier avant de l'ouvrir.</summary>
    private void DrawUrlConfirm()
    {
        using var popupStyle = Tip.PushPopupStyle();
        if (!ImGui.BeginPopup("##urlConfirm"))
            return;

        var url = pendingUrl?.Text ?? string.Empty;
        ImGui.TextUnformatted("Ouvrir ce lien dans ton navigateur ?");
        ImGui.TextColored(TextDim, url);
        ImGui.Separator();

        if (ImGui.Button("Ouvrir"))
        {
            Util.OpenLink(url);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copier"))
        {
            ImGui.SetClipboardText(url);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Annuler"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    // ------------------------------------------------------------ Défilement

    private void UpdateScrolling(ChatFeed feed, Vector2 size)
    {
        var scrollY = ImGui.GetScrollY();
        var scrollMax = MathF.Max(ContentHeight - size.Y, 0f);

        // Comparé au bas de l'image précédente : un nouveau message allonge la liste sans décoller du bas,
        // alors que la molette ou la barre de défilement, elles, font remonter.
        //
        // Pendant que la liste change de hauteur (barre de recherche, fenêtre redimensionnée), le défilement réel est en
        // retard sur le calcul : ce n'est pas l'utilisateur qui a quitté le bas, on garde donc l'état précédent.
        if (MathF.Abs(size.Y - previousListHeight) > 0.5f)
            scrollSettleFrames = ScrollSettleFrameCount;
        previousListHeight = size.Y;

        if (scrollSettleFrames > 0)
            scrollSettleFrames--;
        else if (!scrollToBottom)
            stickToBottom = scrollY >= previousScrollMax - ScrollStickMargin * S;
        previousScrollMax = scrollMax;

        var count = feed.Visible.Count;
        if (count > lastCountForUnseen)
        {
            if (!stickToBottom)
                unseenBelow += count - lastCountForUnseen;
        }
        lastCountForUnseen = count;

        if (stickToBottom)
            unseenBelow = 0;

        if (scrollToBottom || stickToBottom)
        {
            ImGui.SetScrollY(scrollMax);
            scrollToBottom = false;
            stickToBottom = true;
        }

        if (stickToBottom)
            jumpReveal = 0f;
        else
            DrawJumpToBottom(size);
    }

    /// <summary>Bords réels du dessin d'un texte par rapport à sa position d'écriture, sans les vides aux extrémités.</summary>
    private static unsafe (float Left, float Right) TextInkBounds(string text)
    {
        var width = ImGui.CalcTextSize(text).X;
        var font = ImGui.GetFont();
        if (text.Length == 0 || font.FontSize <= 0f)
            return (0f, width);

        var scale = ImGui.GetFontSize() / font.FontSize;
        var first = font.FindGlyph(text[0]);
        var last = font.FindGlyph(text[^1]);
        var left = first != null ? first->X0 * scale : 0f;
        var rightGap = last != null ? (last->AdvanceX - last->X1) * scale : 0f;
        return (left, width - rightGap);
    }

    /// <summary>Flèche tracée à la main, à la hauteur des chiffres du texte à côté d'elle.</summary>
    private static void DrawDownArrow(ImDrawListPtr drawList, float left, float top, float width, float height, Vector4 color)
    {
        var thickness = MathF.Max(2f, 2f * S);
        var centerX = left + width / 2f;

        // La pointe s'arrête un pixel plus haut : l'épaisseur du trait la prolonge jusqu'au bas de la flèche.
        var tip = top + height - 1f;
        var headDrop = width / 2f;

        drawList.PathLineTo(new Vector2(centerX, top));
        drawList.PathLineTo(new Vector2(centerX, tip));
        drawList.PathStroke(Col(color), ImDrawFlags.None, thickness);

        drawList.PathLineTo(new Vector2(left, tip - headDrop));
        drawList.PathLineTo(new Vector2(centerX, tip));
        drawList.PathLineTo(new Vector2(left + width, tip - headDrop));
        drawList.PathStroke(Col(color), ImDrawFlags.None, thickness);
    }

    /// <summary>
    /// Bouton flottant pour revenir au bas de la liste, avec le nombre de messages arrivés entre-temps.
    /// Il remonte en apparaissant, et passe au bleu quand il y a du nouveau, comme les non-lus des onglets.
    /// </summary>
    private void DrawJumpToBottom(Vector2 size)
    {
        var hasNews = unseenBelow > 0;
        var count = unseenBelow > 99 ? "99+" : unseenBelow.ToString();
        var label = hasNews
            ? $"{count} nouveau{(unseenBelow > 1 ? "x" : string.Empty)} message{(unseenBelow > 1 ? "s" : string.Empty)}"
            : "Revenir en bas";

        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        jumpReveal += (1f - jumpReveal) * (1f - MathF.Exp(-JumpRevealSpeed * dt));

        // La police du jeu range ses lettres dans une case plus haute qu'elles, centrée sur leur dessin : la pilule
        // se calcule autour de cette case, avec des marges entières, pour que le texte tombe au milieu.
        var (cellTop, cellBottom) = DigitInk();
        var cellHeight = cellBottom - cellTop;
        var (textInkLeft, textInkRight) = TextInkBounds(label);

        var paddingX = MathF.Round(12f * S);
        var paddingY = MathF.Round(6f * S);
        var gap = MathF.Round(6f * S);

        // La hauteur des chiffres dans cette police, mesurée en jeu : 9 pixels pour 16. Largeur paire : la tige, sur
        // deux pixels d'épaisseur, tombe alors pile sur la grille et reste nette.
        var arrowHeight = MathF.Round(ImGui.GetFontSize() * 0.56f);
        var arrowWidth = MathF.Round(arrowHeight * 0.45f) * 2f;

        var pillSize = new Vector2(
            paddingX * 2f + arrowWidth + gap + (textInkRight - textInkLeft),
            MathF.Round(cellHeight) + paddingY * 2f);

        var windowPos = ImGui.GetWindowPos();
        var rest = windowPos + new Vector2((size.X - pillSize.X) / 2f, size.Y - pillSize.Y - 10f * S);
        var min = new Vector2(MathF.Round(rest.X), MathF.Round(rest.Y + (1f - jumpReveal) * 8f * S));
        var max = min + pillSize;

        jumpButtonMin = min;
        jumpButtonMax = max;

        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton("##jumpBottom", pillSize);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var news = new Vector4(0.55f, 0.76f, 1f, 1f);
        var rounding = pillSize.Y / 2f;
        var drawList = ImGui.GetWindowDrawList();

        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, MathF.Max(jumpReveal, 0.001f) * ImGui.GetStyle().Alpha))
        {
            var fill = held ? new Vector4(0.20f, 0.22f, 0.29f, 0.98f)
                : hovered ? new Vector4(0.16f, 0.18f, 0.24f, 0.98f)
                : new Vector4(0.10f, 0.11f, 0.15f, 0.96f);
            drawList.AddRectFilled(min, max, Col(fill), rounding);
            drawList.AddRect(min, max, Col(hasNews ? news with { W = 0.55f } : new Vector4(1f, 1f, 1f, hovered ? 0.22f : 0.12f)), rounding);

            var arrowLeft = min.X + paddingX;
            var textPos = new Vector2(arrowLeft + arrowWidth + gap - textInkLeft, MathF.Round(min.Y + paddingY - cellTop));

            // Même centre que les chiffres. Arrondi vers le bas de l'écran : leur dessin tombe un demi-pixel sous le
            // milieu de la case, c'est ce qui fait coïncider exactement le haut et le bas de la flèche avec eux.
            var cellCenter = textPos.Y + (cellTop + cellBottom) / 2f;
            var arrowTop = MathF.Ceiling(cellCenter - arrowHeight / 2f);
            DrawDownArrow(drawList, arrowLeft, arrowTop, arrowWidth, arrowHeight, hasNews ? news : hovered ? TextMain : TextDim);

            drawList.AddText(textPos, Col(hasNews || hovered ? TextMain : new Vector4(0.82f, 0.84f, 0.89f, 1f)), label);
        }

        if (clicked)
            scrollToBottom = true;
    }
}
