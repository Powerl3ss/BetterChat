using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    private bool stickToBottom = true;
    private int unseenBelow;
    private int lastCountForUnseen;
    private bool scrollToBottom;
    private float previousScrollMax;

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
        textLeft = 10f * S;
        textWidth = MathF.Max(size.X - textLeft * 2f - scrollbar, 40f);

        PrepareStack(feed);

        if (stack.Count == 0)
        {
            DrawEmptyState(size, feed);
            return;
        }

        // Toute la hauteur est réservée : la barre de défilement est juste, même sans tout dessiner.
        var paddingTop = ListPaddingTop * S;
        var origin = ImGui.GetCursorScreenPos() + new Vector2(0f, paddingTop);
        ImGui.Dummy(new Vector2(size.X - scrollbar, ContentHeight));

        var scrollY = ImGui.GetScrollY();
        var hoverable = ImGui.IsWindowHovered();
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
        ImGui.SetCursorPos((size - textSize) / 2f);
        ImGui.TextColored(TextDim with { W = TextAlpha }, text);
    }

    // ------------------------------------------------------------ Mise en page

    private void PrepareStack(ChatFeed feed)
    {
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
            height += lineHeight + 2f * S;

        // Plus d'air entre deux auteurs qu'entre deux messages du même auteur.
        height += header ? 8f * S : flows ? 1f * S : 3f * S;
        return height;
    }

    private LineLayout LayoutOf(ChatLine line)
    {
        if (line.LayoutCache is LineLayout cached && cached.Key == layoutKey)
            return cached;

        var layout = new LineLayout { Key = layoutKey, Spans = DisplaySpans(line) };
        layout.Text = TextLayout.Wrap(layout.Spans, textWidth, Measure, 0f, Cfg.Style == ChatStyle.Compact ? 12f * S : 0f);
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
        if (Cfg.ShowTimestamps)
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
            drawList.AddRectFilled(rowMin, rowMax, Col(mark with { W = 0.10f * TextAlpha }));
            drawList.AddRectFilled(rowMin, new Vector2(rowMin.X + 3f * S, rowMax.Y), Col(mark with { W = 0.85f * TextAlpha }));
        }

        var header = headers[index];
        var channel = plugin.Colors.Of(line.Kind);
        var y = top;
        var left = origin.X + textLeft;

        if (header)
        {
            y += 6f * S;
            DrawHeader(line, new Vector2(left, y), channel, hoverable);
            y += lineHeight + 2f * S;
        }

        var layout = LayoutOf(line);
        DrawRuns(layout, new Vector2(left, y), channel, hoverable);

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
            DrawTextShadowed(drawList, new Vector2(x, pos.Y), channel with { W = TextAlpha }, "À ");
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

        // Le canal et l'heure, plus discrets.
        var dim = Cfg.SecondaryColor with { W = Cfg.SecondaryColor.W * TextAlpha };
        x += 8f * S;
        if (Cfg.ShowChannelTags)
        {
            var tag = ChatKinds.Info(line.Kind).Tag;
            var tagW = Measure(tag);
            var pillMin = new Vector2(x, pos.Y + 1f * S);
            var pillMax = new Vector2(x + tagW + 10f * S, pos.Y + lineHeight - 1f * S);
            drawList.AddRectFilled(pillMin, pillMax, Col(channel with { W = 0.14f * TextAlpha }), lineHeight / 2f);
            drawList.AddText(new Vector2(x + 5f * S, pos.Y), Col(channel with { W = TextAlpha }), tag);
            x = pillMax.X + 6f * S;
        }

        if (Cfg.ShowTimestamps)
            drawList.AddText(new Vector2(x, pos.Y), Col(dim), line.TimeUtc.ToLocalTime().ToString("HH:mm"));
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
        color = color with { W = color.W * TextAlpha };
        DrawTextShadowed(drawList, pos, color, text);

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

    private void DrawTextShadowed(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text)
    {
        if (Cfg.TextShadow)
            drawList.AddText(pos + new Vector2(1f, 1f), Col(new Vector4(0f, 0f, 0f, 0.65f * color.W)), text);
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
                ImGui.SetTooltip($"{(link.Text.Length > 0 ? link.Text + "\n" : string.Empty)}Clic : ouvrir la carte");
                break;
            case LinkKind.Url:
                ImGui.SetTooltip($"{link.Text}\nClic : ouvrir le lien (une confirmation est demandée)");
                break;
            case LinkKind.Player:
                ImGui.SetTooltip($"{link.Text}{(link.World.Length > 0 ? " (" + link.World + ")" : string.Empty)}\nClic : message privé, copier, masquer");
                break;
        }
    }

    private static void DrawItemTooltip(uint itemId)
    {
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return;

        using var tooltip = ImRaii.Tooltip();

        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Value.Icon));
        if (icon.TryGetWrap(out var wrap, out _) && wrap != null)
        {
            ImGui.Image(wrap.Handle, new Vector2(40f, 40f) * S);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted(item.Value.Name.ExtractText());
        if (item.Value.LevelItem.RowId > 1)
            ImGui.TextColored(TextDim, $"Niveau d'objet {item.Value.LevelItem.RowId}");
        ImGui.TextColored(TextDim, "Clic : copier, lier dans le chat");
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
        if (ImGui.BeginPopup("##lineMenu"))
        {
            if (contextLine is { } line)
            {
                if (line.Author != null && line.Author.Text != plugin.LocalName)
                    DrawPlayerActions(line.Author);

                if (ImGui.MenuItem("Copier le message"))
                    ImGui.SetClipboardText(line.CopyText);
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("##linkMenu"))
        {
            switch (contextLink?.Kind)
            {
                case LinkKind.Player:
                    ImGui.TextDisabled(contextLink.TellTarget);
                    ImGui.Separator();
                    DrawPlayerActions(contextLink);
                    break;

                case LinkKind.Item:
                    var name = ItemName(contextLink.Id);
                    ImGui.TextDisabled(name);
                    ImGui.Separator();
                    if (ImGui.MenuItem("Lier dans le chat"))
                    {
                        GameChat.LinkItem(contextLink.Id);
                        AppendToInput("<item>");
                    }
                    if (ImGui.MenuItem("Copier le nom"))
                        ImGui.SetClipboardText(name);
                    break;

                case LinkKind.Map:
                    if (ImGui.MenuItem("Ouvrir la carte"))
                        OpenMap(contextLink);
                    if (ImGui.MenuItem("Copier le lieu"))
                        ImGui.SetClipboardText(contextLink.Text);
                    break;

                case LinkKind.Url:
                    if (ImGui.MenuItem("Copier le lien"))
                        ImGui.SetClipboardText(contextLink.Text);
                    break;
            }

            ImGui.EndPopup();
        }

        DrawUrlConfirm();
    }

    private void DrawPlayerActions(ChatLink player)
    {
        if (player.Text != plugin.LocalName && ImGui.MenuItem("Envoyer un message privé"))
        {
            plugin.Game.SwitchToTell(player);
            FocusInput();
        }

        if (ImGui.MenuItem("Copier le nom"))
            ImGui.SetClipboardText(player.TellTarget);

        if (player.Text != plugin.LocalName && ImGui.MenuItem("Masquer ses messages"))
            plugin.MutePlayer(player.Text);

        ImGui.Separator();
    }

    /// <summary>Un lien dans le chat peut mener n'importe où : l'adresse s'affiche en entier avant de l'ouvrir.</summary>
    private void DrawUrlConfirm()
    {
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
        if (!scrollToBottom)
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

        if (!stickToBottom)
            DrawJumpToBottom(size, scrollY);
    }

    /// <summary>Pastille flottante : revenir au bas de la liste, avec le nombre de messages arrivés entre-temps.</summary>
    private void DrawJumpToBottom(Vector2 size, float scrollY)
    {
        var label = unseenBelow > 0 ? $"{unseenBelow} nouveau{(unseenBelow > 1 ? "x" : string.Empty)} message{(unseenBelow > 1 ? "s" : string.Empty)}" : "Revenir en bas";
        var textSize = ImGui.CalcTextSize(label);
        var pillSize = textSize + new Vector2(20f * S, 8f * S);
        var pos = new Vector2((size.X - pillSize.X) / 2f, scrollY + size.Y - pillSize.Y - 8f * S);

        ImGui.SetCursorPos(pos);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##jumpBottom", pillSize);
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var ink = new Vector4(0.05f, 0.06f, 0.09f, 1f);
        drawList.AddRectFilled(min, min + pillSize, Col(unseenBelow > 0 ? Accent : hovered ? TextMain : TextDim), pillSize.Y / 2f);
        drawList.AddText(min + new Vector2(10f * S, 4f * S), Col(ink), label);

        if (clicked)
            scrollToBottom = true;
    }
}
