using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using BetterChat.Chat;
using BetterChat.Core;
using static BetterChat.Windows.ChatWindow;

namespace BetterChat.Windows;

/// <summary>
/// La liste des messages. Elle peut en contenir des milliers : chaque message est mis en page une fois,
/// et seuls ceux à l'écran sont dessinés. Le chat en a une, et chaque onglet détaché a la sienne.
/// </summary>
public sealed class MessageList
{
    private const float ScrollStickMargin = 4f;
    private const int ScrollSettleFrameCount = 3;
    private const float JumpRevealSpeed = 16f;

    // Air entre la liste et la barre d'onglets, puis la zone de saisie.
    private const float ListPaddingTop = 8f;
    private const float ListPaddingBottom = 6f;
    private const int WidthCacheLimit = 20000;
    private const float SelectionScrollSpeed = 320f;

    // Les icônes de job du jeu se suivent à partir de ce numéro, dans l'ordre de la feuille ClassJob.
    private const uint JobIconBase = 62000;
    private static readonly Dictionary<uint, IconInk> JobIconInks = new();

    /// <summary>Mise en page d'un message pour une largeur et une police données.</summary>
    private sealed class LineLayout
    {
        public int Key;
        public List<ChatSpan> Spans = new();
        public LaidOutText Text = LaidOutText.Empty;
        public int IconSpan = -1;
    }

    // Largeur de chaque mot, gardée entre deux mises en page : un redimensionnement reste fluide.
    private readonly Dictionary<string, float> widths = new();
    private float widthsFontPx;

    // Chaque vue garde sa mise en page, car les fenêtres n'ont pas la même largeur. Elle s'en va avec le message.
    private readonly ConditionalWeakTable<ChatLine, LineLayout> layouts = new();

    private readonly LineStack stack = new();
    private readonly List<bool> headers = new();
    private readonly List<LineSeparators> separators = new();
    private readonly List<float> topGaps = new();
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
    private string colorPlayer = string.Empty;

    // Sélection à la souris : du point où le bouton a été enfoncé jusqu'au curseur.
    private readonly record struct TextPos(long LineId, int Offset);
    private TextPos selectionAnchor;
    private TextPos selectionEnd;
    private bool selecting;
    private bool openPlayerColor;

    private readonly Plugin plugin;

    public MessageList(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>La saisie de la même fenêtre : un lien ou un message privé s'y prépare.</summary>
    public ChatInputBar? Input { get; set; }

    private Configuration Cfg => plugin.Configuration;
    private Vector4 Accent => Cfg.AccentColor with { W = 1f };

    public void Draw(ChatFeed feed, Vector2 size)
    {
        using var child = ImRaii.Child("##messages", size, false, ImGuiWindowFlags.NoMove);
        if (!child.Success)
            return;

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
            DrawLine(feed, feed.Visible[i], i, origin, size.X, hoverable);

        UpdateSelection(feed, origin, size, hoverable);

        // Le curseur main quand on survole un lien, le curseur de texte sur ce qui se sélectionne.
        if (hoveredLink != null)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        else if (selecting || (hoverable && IsOverText(feed, origin, ImGui.GetMousePos())))
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);

        DrawLinePopups(feed);
        UpdateScrolling(feed, size);
        ScrollWhileSelecting(size);
    }

    private float ContentHeight => stack.Total + (ListPaddingTop + ListPaddingBottom) * S;

    private void DrawEmptyState(Vector2 size, ChatFeed feed)
    {
        var text = feed.Search.Length > 0 ? "Aucun message trouvé" : "Aucun message pour l'instant";
        var textSize = ImGui.CalcTextSize(text);
        DrawOutlinedText(ImGui.GetWindowDrawList(), ImGui.GetWindowPos() + (size - textSize) / 2f, TextDim, text);
    }

    // ------------------------------------------------------------ Mise en page

    private float SeparatorHeight => MathF.Round(lineHeight + 8f * S);

    private void PrepareStack(ChatFeed feed)
    {
        // Présentation moderne : l'heure a sa colonne à gauche, et tous les textes commencent au même endroit.
        timeColumn = Cfg.Style == ChatStyle.Modern && Cfg.ShowTimestamps ? MathF.Ceiling(Measure("00:00") + 6f * S) : 0f;

        var key = HashCode.Combine(MathF.Round(textWidth), lineHeight, Cfg.Style, Cfg.ShowTimestamps, Cfg.ShowChannelTags, Cfg.SecondaryColor,
                                   HashCode.Combine(Cfg.ShowJobIcons, Cfg.JobIconHeight, Cfg.MessageSpacing), Cfg.ShowSeparators);
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
            separators.Clear();
            topGaps.Clear();
            ClearSelection();
            lastCountForUnseen = feed.Visible.Count;
            unseenBelow = 0;
        }

        // Nouveaux messages depuis la dernière image : on ne met en page que ceux-là.
        var lines = feed.Visible;
        var today = DateTime.Today;
        for (var i = stack.Count; i < lines.Count; i++)
        {
            var previous = i > 0 ? lines[i - 1] : null;
            var marks = Cfg.ShowSeparators ? LineSeparators.Between(previous, lines[i], today) : default;

            // Après un séparateur, le nom de l'auteur est toujours rappelé.
            var header = Cfg.Style == ChatStyle.Modern && ChatGrouping.HasAuthorHeader(lines[i]) && (marks.Any || ChatGrouping.StartsGroup(previous, lines[i]));
            // L'écart choisi dans les réglages, le même entre tous les messages. Une barre masquée n'en ajoute pas.
            var hiddenBar = lines[i].Kind == ChatKind.SessionStart && !Cfg.ShowSeparators;
            var gap = i > 0 && !hiddenBar ? MathF.Round(Cfg.MessageSpacing * S) : 0f;

            headers.Add(header);
            separators.Add(marks);
            topGaps.Add(gap);
            stack.Add(LineHeightOf(lines[i], header) + gap + SeparatorsHeight(marks));
        }
    }

    private float SeparatorsHeight(LineSeparators marks) => marks.Day != null ? SeparatorHeight : 0f;

    private float LineHeightOf(ChatLine line, bool header)
    {
        if (line.Kind == ChatKind.SessionStart)
            return Cfg.ShowSeparators ? SeparatorHeight : 0f;

        var height = LayoutOf(line).Text.LineCount * lineHeight;
        return header ? height + lineHeight : height;
    }

    private LineLayout LayoutOf(ChatLine line)
    {
        if (layouts.TryGetValue(line, out var cached) && cached.Key == layoutKey)
            return cached;

        var layout = new LineLayout { Key = layoutKey };
        layout.Spans = DisplaySpans(line, layout);
        layout.Text = TextLayout.Wrap(layout.Spans, textWidth - timeColumn, Measure, 0f, Cfg.Style == ChatStyle.Compact ? 12f * S : 0f);
        layouts.AddOrUpdate(line, layout);
        return layout;
    }

    /// <summary>En mode moderne, le texte seul (le nom est dans l'en-tête). En mode compact : heure, canal, nom, texte.</summary>
    private List<ChatSpan> DisplaySpans(ChatLine line, LineLayout layout)
    {
        var hasHeader = Cfg.Style == ChatStyle.Modern && ChatGrouping.HasAuthorHeader(line);
        if (hasHeader)
            return line.Body;

        var spans = new List<ChatSpan>(line.Body.Count + line.Sender.Count + 3);
        if (Cfg.ShowTimestamps && Cfg.Style == ChatStyle.Compact)
            spans.Add(new ChatSpan(line.TimeUtc.ToLocalTime().ToString("HH:mm") + " ", DimRgba));

        if (Cfg.ShowChannelTags && Cfg.Style == ChatStyle.Compact)
            spans.Add(new ChatSpan($"[{ChatKinds.Info(line.Kind).Tag}] "));

        if (line.ShowsSender)
        {
            // L'icône de job n'est pas du texte : des espaces lui réservent sa place juste avant le nom.
            if (HasJobIcon(line))
            {
                layout.IconSpan = spans.Count;
                var spaces = (int)MathF.Ceiling(JobIconAdvance / MathF.Max(Measure(" "), 1f));
                spans.Add(new ChatSpan(new string(' ', spaces)));
            }

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

    internal float Measure(string text)
    {
        if (text.Length == 0)
            return 0f;

        var fontPx = ImGui.GetTextLineHeight();
        if (MathF.Abs(widthsFontPx - fontPx) > 0.01f || widths.Count > WidthCacheLimit)
        {
            widths.Clear();
            widthsFontPx = fontPx;
        }

        if (widths.TryGetValue(text, out var width))
            return width;

        width = GameIcons.Contains(text) ? MeasureWithIcons(text, fontPx) : ImGui.CalcTextSize(text).X;
        widths[text] = width;
        return width;
    }

    private float MeasureWithIcons(string text, float fontPx)
    {
        var width = 0f;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!GameIcons.IsIcon(text[i]))
                continue;

            if (i > start)
                width += ImGui.CalcTextSize(text[start..i]).X;
            width += plugin.Icons.WidthOf(GameIcons.IdOf(text[i]), fontPx);
            start = i + 1;
        }

        if (start < text.Length)
            width += ImGui.CalcTextSize(text[start..]).X;
        return width;
    }

    // ------------------------------------------------------------ Dessin d'un message

    private void DrawLine(ChatFeed feed, ChatLine line, int index, Vector2 origin, float width, bool hoverable)
    {
        var top = origin.Y + stack.Top(index);
        var bottom = origin.Y + stack.Bottom(index);
        var drawList = ImGui.GetWindowDrawList();

        var marks = separators[index];
        if (marks.Day is { } day)
        {
            DrawSeparator(drawList, origin.X, top, ChatDates.Label(day, DateTime.Today), Cfg.SecondaryColor);
            top += SeparatorHeight;
        }

        if (line.Kind == ChatKind.SessionStart)
        {
            if (Cfg.ShowSeparators)
                DrawSeparator(drawList, origin.X, top, line.BodyText, Cfg.SecondaryColor);
            return;
        }

        // Le fond du survol et des mentions serre le texte : il n'englobe pas l'écart entre les messages.
        top += topGaps[index];
        var rowMin = new Vector2(origin.X, top);
        var rowMax = new Vector2(origin.X + width, bottom);

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
            DrawOutlinedText(drawList, new Vector2(left, y), Cfg.SecondaryColor, line.TimeUtc.ToLocalTime().ToString("HH:mm"));
        }

        if (header)
        {
            DrawHeader(line, new Vector2(textX, y), channel, hoverable);
            y += lineHeight;
        }

        var layout = LayoutOf(line);
        DrawRuns(line, layout, new Vector2(textX, y), channel, hoverable);
        DrawRepeatCount(drawList, feed.RepeatCount(line), layout, new Vector2(textX, y));

        if (hovered && hoveredLink == null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            contextLine = line;
            contextLink = null;
            ImGui.OpenPopup("##lineMenu");
        }
    }

    /// <summary>Une ligne horizontale coupée par un texte centré : changement de date, ou reconnexion.</summary>
    private void DrawSeparator(ImDrawListPtr drawList, float originX, float top, string text, Vector4 color)
    {
        var left = originX + textLeft;
        var right = left + textWidth;
        var width = Measure(text);
        var textX = MathF.Round(left + (textWidth - width) / 2f);
        var textY = MathF.Round(top + (SeparatorHeight - lineHeight) / 2f);
        var lineY = MathF.Round(top + SeparatorHeight / 2f);
        var gap = 8f * S;
        var thickness = MathF.Max(1f, MathF.Round(S));
        var lineColor = Col(color with { W = 0.45f });

        // Dans une fenêtre très étroite, le texte seul prend toute la place.
        if (textX - gap > left)
        {
            drawList.AddRectFilled(new Vector2(left, lineY), new Vector2(textX - gap, lineY + thickness), lineColor);
            drawList.AddRectFilled(new Vector2(textX + width + gap, lineY), new Vector2(right, lineY + thickness), lineColor);
        }

        DrawOutlinedText(drawList, new Vector2(textX, textY), color, text);
    }

    /// <summary>« x3 » juste après le texte d'un message répété.</summary>
    private void DrawRepeatCount(ImDrawListPtr drawList, int count, LineLayout layout, Vector2 pos)
    {
        if (count <= 1 || layout.Text.Runs.Count == 0)
            return;

        var last = layout.Text.Runs[^1];
        var text = $"x{count}";
        var x = MathF.Min(last.X + last.Width + 6f * S, textWidth - timeColumn - Measure(text));
        DrawOutlinedText(drawList, pos + new Vector2(x, last.Line * lineHeight), Cfg.SecondaryColor, text);
    }

    private bool HasJobIcon(ChatLine line) => Cfg.ShowJobIcons && line.AuthorJob != 0;

    // La case réservée à l'icône : les noms commencent au même endroit, quelle que soit la forme du dessin.
    private float JobIconSize => MathF.Max(MathF.Round(lineHeight * Cfg.JobIconHeight / 100f), 1f);

    private float JobIconAdvance => JobIconSize + MathF.Round(4f * S);

    /// <summary>
    /// L'icône de job, à la hauteur du texte écrit en <paramref name="textPos"/>. C'est son dessin qui est placé, sans
    /// les marges de son image : il se centre sur le texte et remplit sa case quel que soit le job.
    /// </summary>
    private void DrawJobIcon(ImDrawListPtr drawList, uint job, Vector2 textPos)
    {
        var lookup = new GameIconLookup(JobIconBase + job);
        var icon = Plugin.TextureProvider.GetFromGameIcon(lookup);
        if (!icon.TryGetWrap(out var wrap, out _) || wrap == null)
            return;

        var ink = JobIconInk(job, lookup);
        var inkW = ink.Right - ink.Left;
        var inkH = ink.Bottom - ink.Top;
        var slot = JobIconSize;

        // Le dessin le plus long tient dans la case, l'autre côté suit pour ne pas le déformer.
        var ratio = inkW / MathF.Max(inkH, 0.001f) * wrap.Width / MathF.Max(wrap.Height, 1f);
        var size = ratio >= 1f
            ? new Vector2(slot, MathF.Max(MathF.Round(slot / ratio), 1f))
            : new Vector2(MathF.Max(MathF.Round(slot * ratio), 1f), slot);

        // Même centre que les chiffres et les lettres : voir la flèche de « Revenir en bas ».
        var (cellTop, cellBottom) = DigitInk();
        var min = new Vector2(
            MathF.Round(textPos.X + (slot - size.X) / 2f),
            MathF.Ceiling(textPos.Y + (cellTop + cellBottom) / 2f - size.Y / 2f));

        drawList.AddImage(wrap.Handle, min, min + size, new Vector2(ink.Left, ink.Top), new Vector2(ink.Right, ink.Bottom));
    }

    private static IconInk JobIconInk(uint job, GameIconLookup lookup)
    {
        if (JobIconInks.TryGetValue(job, out var known))
            return known;

        var ink = IconInk.Full;
        try
        {
            var file = Plugin.DataManager.GetFile<TexFile>(Plugin.TextureProvider.GetIconPath(lookup));
            if (file != null)
                ink = IconInk.Find(file.ImageData, file.Header.Width, file.Header.Height);
        }
        catch (Exception ex)
        {
            // Sans cette mesure, l'icône s'affiche simplement avec ses marges.
            Plugin.Log.Debug(ex, "Lecture de l'icône du job {Job} impossible", job);
        }

        JobIconInks[job] = ink;
        return ink;
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

        if (HasJobIcon(line))
        {
            DrawJobIcon(drawList, line.AuthorJob, new Vector2(x, pos.Y));
            x += JobIconAdvance;
        }

        foreach (var span in line.Sender)
        {
            if (span.Text.Length == 0)
                continue;

            var link = span.Link ?? line.Author;
            var color = plugin.PlayerColor(link) ?? (span.Color != 0 ? ChannelColors.FromRgba(span.Color) : channel);
            var w = Measure(span.Text);
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

    private void DrawRuns(ChatLine line, LineLayout layout, Vector2 pos, Vector4 channel, bool hoverable)
    {
        var drawList = ImGui.GetWindowDrawList();
        var (selectionStart, selectionStop) = SelectionIn(line);
        var offset = 0;
        foreach (var run in layout.Text.Runs)
        {
            // Le fond de la sélection passe sous le texte.
            if (selectionStart < selectionStop &&
                TextSelection.Highlight(run, offset, selectionStart, selectionStop, Measure) is { } highlight)
            {
                var rowTop = pos.Y + run.Line * lineHeight;
                drawList.AddRectFilled(new Vector2(pos.X + highlight.Left, rowTop), new Vector2(pos.X + highlight.Right, rowTop + lineHeight), Col(Accent with { W = 0.38f }));
            }

            offset += run.Text.Length;

            if (run.SpanIndex == layout.IconSpan)
            {
                DrawJobIcon(drawList, line.AuthorJob, pos + new Vector2(run.X, run.Line * lineHeight));
                continue;
            }

            var span = layout.Spans[run.SpanIndex];
            var color = plugin.PlayerColor(span.Link) ?? (span.Color != 0 ? ChannelColors.FromRgba(span.Color) : channel);
            if (span.Italic)
                color = color with { W = color.W * 0.85f };

            DrawRun(drawList, pos + new Vector2(run.X, run.Line * lineHeight), run.Text, run.Width, color, span.Link, hoverable);
        }
    }

    private void DrawRun(ImDrawListPtr drawList, Vector2 pos, string text, float width, Vector4 color, ChatLink? link, bool hoverable)
    {
        DrawTextWithIcons(drawList, pos, color, text);

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

    /// <summary>Le texte coupé autour de ses icônes, chacune dessinée à sa place.</summary>
    private void DrawTextWithIcons(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text)
    {
        if (!GameIcons.Contains(text))
        {
            DrawOutlinedText(drawList, pos, color, text);
            return;
        }

        var x = pos.X;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!GameIcons.IsIcon(text[i]))
                continue;

            if (i > start)
            {
                var part = text[start..i];
                DrawOutlinedText(drawList, new Vector2(x, pos.Y), color, part);
                x += Measure(part);
            }

            var icon = GameIcons.IdOf(text[i]);
            plugin.Icons.Draw(drawList, icon, new Vector2(x, pos.Y), lineHeight, color.W);
            x += plugin.Icons.WidthOf(icon, lineHeight);
            start = i + 1;
        }

        if (start < text.Length)
            DrawOutlinedText(drawList, new Vector2(x, pos.Y), color, text[start..]);
    }

    /// <summary>Texte avec un contour : 8 copies sombres décalées, dessinées dessous. Il reste lisible sans fond.</summary>
    internal void DrawOutlinedText(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text)
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

    // ------------------------------------------------------------ Sélection à la souris

    private bool HasSelection => selectionAnchor != selectionEnd;

    private void ClearSelection()
    {
        selecting = false;
        selectionAnchor = default;
        selectionEnd = default;
    }

    private (TextPos First, TextPos Last) OrderedSelection()
    {
        var anchorFirst = selectionAnchor.LineId < selectionEnd.LineId ||
                          (selectionAnchor.LineId == selectionEnd.LineId && selectionAnchor.Offset <= selectionEnd.Offset);
        return anchorFirst ? (selectionAnchor, selectionEnd) : (selectionEnd, selectionAnchor);
    }

    /// <summary>La partie sélectionnée d'un message, en caractères. Vide (0, 0) s'il n'en a pas.</summary>
    private (int Start, int End) SelectionIn(ChatLine line)
    {
        if (!HasSelection)
            return (0, 0);

        var (first, last) = OrderedSelection();
        if (line.Id < first.LineId || line.Id > last.LineId)
            return (0, 0);

        return (line.Id == first.LineId ? first.Offset : 0, line.Id == last.LineId ? last.Offset : int.MaxValue);
    }

    /// <summary>Haut du texte d'un message dans la liste, sous ses séparateurs et son en-tête.</summary>
    private float BodyTop(int index) =>
        stack.Top(index) + SeparatorsHeight(separators[index]) + topGaps[index] + (headers[index] ? lineHeight : 0f);

    private TextPos PositionAt(ChatFeed feed, Vector2 origin, Vector2 mouse)
    {
        var index = Math.Clamp(stack.FirstVisible(mouse.Y - origin.Y), 0, stack.Count - 1);
        var line = feed.Visible[index];
        var textTop = origin.Y + BodyTop(index);
        if (line.Kind == ChatKind.SessionStart || mouse.Y < textTop)
            return new TextPos(line.Id, 0);

        var row = (int)MathF.Floor((mouse.Y - textTop) / lineHeight);
        return new TextPos(line.Id, TextSelection.OffsetAt(LayoutOf(line).Text, row, mouse.X - origin.X - textLeft - timeColumn, Measure));
    }

    private bool IsOverText(ChatFeed feed, Vector2 origin, Vector2 mouse)
    {
        var index = Math.Clamp(stack.FirstVisible(mouse.Y - origin.Y), 0, stack.Count - 1);
        var line = feed.Visible[index];
        var textTop = origin.Y + BodyTop(index);
        if (line.Kind == ChatKind.SessionStart || mouse.Y < textTop)
            return false;

        var row = (int)MathF.Floor((mouse.Y - textTop) / lineHeight);
        var x = mouse.X - origin.X - textLeft - timeColumn;
        foreach (var run in LayoutOf(line).Text.Runs)
        {
            if (run.Line == row && x >= run.X && x <= run.X + run.Width)
                return true;
        }

        return false;
    }

    private void UpdateSelection(ChatFeed feed, Vector2 origin, Vector2 size, bool hoverable)
    {
        var mouse = ImGui.GetMousePos();
        var listTop = ImGui.GetWindowPos().Y;
        var overList = hoverable && hoveredLink == null && mouse.X < ImGui.GetWindowPos().X + size.X - ImGui.GetStyle().ScrollbarSize;

        if (overList && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selectionAnchor = PositionAt(feed, origin, mouse);
            selectionEnd = selectionAnchor;
            selecting = true;
        }
        else if (selecting)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                selectionEnd = PositionAt(feed, origin, mouse with { Y = Math.Clamp(mouse.Y, listTop, listTop + size.Y) });
            else
                selecting = false;
        }

    }

    /// <summary>Le curseur dépasse de la liste pendant la sélection : elle défile pour aller plus loin.</summary>
    private void ScrollWhileSelecting(Vector2 size)
    {
        if (!selecting)
            return;

        var mouseY = ImGui.GetMousePos().Y;
        var listTop = ImGui.GetWindowPos().Y;
        var direction = mouseY < listTop ? -1f : mouseY > listTop + size.Y ? 1f : 0f;
        if (direction != 0f)
            ImGui.SetScrollY(ImGui.GetScrollY() + direction * SelectionScrollSpeed * S * Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f));
    }

    /// <summary>Le texte sélectionné, avec un retour à la ligne entre deux messages.</summary>
    private string SelectedText(ChatFeed feed)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in feed.Visible)
        {
            var (start, end) = SelectionIn(line);
            if (start >= end || line.Kind == ChatKind.SessionStart)
                continue;

            var layout = LayoutOf(line);
            var piece = TextSelection.Extract(layout.Text, start, end, layout.IconSpan);
            if (piece.Length == 0)
                continue;

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(piece);
        }

        return builder.ToString();
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

        if (left && link.Kind == LinkKind.PartyFinder)
        {
            GameChat.OpenPartyFinder(link.Id);
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
                Tip.Show($"{link.Text}{(link.World.Length > 0 ? " (" + link.World + ")" : string.Empty)}\nClic : message privé, copier, couleur, masquer");
                break;
            case LinkKind.Quest:
                DrawQuestTooltip(link.Id);
                break;
            case LinkKind.Status:
                DrawStatusTooltip(link.Id);
                break;
            case LinkKind.PartyFinder:
                Tip.Show("Annonce de recherche de groupe\nClic : ouvrir l'annonce");
                break;
        }
    }

    private static void DrawItemTooltip(uint itemId)
    {
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return;

        using var tooltip = Tip.Begin();

        var hasLevel = item.Value.LevelItem.RowId > 1;
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Value.Icon));
        if (icon.TryGetWrap(out var wrap, out _) && wrap != null)
        {
            var iconSize = 40f * S;
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
            ImGui.SameLine();

            // Le texte se centre sur la hauteur de l'image.
            var lines = hasLevel ? 2 : 1;
            var textH = lines * ImGui.GetTextLineHeight() + (lines - 1) * Tip.Spacing.Y;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Round((iconSize - textH) / 2f));
        }

        ImGui.BeginGroup();
        Tip.Title(item.Value.Name.ExtractText());
        if (hasLevel)
            Tip.Note($"Niveau d'objet {item.Value.LevelItem.RowId}");
        ImGui.EndGroup();
    }

    private static void DrawQuestTooltip(uint questId)
    {
        var quest = Plugin.DataManager.GetExcelSheet<Quest>().GetRowOrDefault(questId);
        if (quest == null)
            return;

        using var tooltip = Tip.Begin();
        Tip.Title(quest.Value.Name.ExtractText());

        var genre = quest.Value.JournalGenre.ValueNullable?.Name.ExtractText();
        if (!string.IsNullOrEmpty(genre))
            Tip.Note(genre);

        var level = quest.Value.ClassJobLevel[0];
        if (level > 0)
            Tip.Note($"Niveau {level}");

        Tip.Hint("Clic : copier le nom");
    }

    private static void DrawStatusTooltip(uint statusId)
    {
        var status = Plugin.DataManager.GetExcelSheet<Status>().GetRowOrDefault(statusId);
        if (status == null)
            return;

        using var tooltip = Tip.Begin();

        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(status.Value.Icon));
        if (icon.TryGetWrap(out var wrap, out _) && wrap != null)
        {
            // Les icônes d'effet sont plus hautes que larges.
            ImGui.Image(wrap.Handle, new Vector2(24f, 32f) * S);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        Tip.Title(status.Value.Name.ExtractText());
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 280f * S);
        ImGui.TextUnformatted(status.Value.Description.ExtractText());
        ImGui.PopTextWrapPos();
        Tip.Hint("Clic : copier le nom");
        ImGui.EndGroup();
    }

    private static string QuestName(uint questId) =>
        Plugin.DataManager.GetExcelSheet<Quest>().GetRowOrDefault(questId)?.Name.ExtractText() ?? string.Empty;

    private static string StatusName(uint statusId) =>
        Plugin.DataManager.GetExcelSheet<Status>().GetRowOrDefault(statusId)?.Name.ExtractText() ?? string.Empty;

    private static string ItemName(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;

    private static void OpenMap(ChatLink link)
    {
        var payload = new Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload(link.Id, link.MapId, link.RawX, link.RawY);
        Plugin.GameGui.OpenMapWithMapLink(payload);
    }

    private void DrawLinePopups(ChatFeed feed)
    {
        if (Tip.BeginMenu("##lineMenu"))
        {
            if (contextLine is { } line)
            {
                var fromOther = line.Author != null && line.Author.Text != plugin.LocalName;
                if (fromOther)
                {
                    Tip.MenuTitle(line.Author!.TellTarget);
                    Tip.MenuSeparator();
                }

                if (HasSelection && Tip.MenuItem("Copier la sélection", FontAwesomeIcon.Clipboard))
                    ImGui.SetClipboardText(SelectedText(feed));

                if (fromOther)
                    DrawPlayerActions(line.Author!);

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
                    DrawPlayerActions(contextLink);
                    break;

                case LinkKind.Item:
                    var name = ItemName(contextLink.Id);
                    if (GameChat.HasRecipe(contextLink.Id) && Tip.MenuItem("Voir le craft", FontAwesomeIcon.Hammer))
                        GameChat.OpenRecipe(contextLink.Id);
                    if (Tip.MenuItem("Lier dans le chat", FontAwesomeIcon.Link))
                    {
                        GameChat.LinkItem(contextLink.Id);
                        Input?.AppendToInput("<item>");
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

                case LinkKind.Quest:
                    DrawCopyNameMenu(QuestName(contextLink.Id));
                    break;

                case LinkKind.Status:
                    DrawCopyNameMenu(StatusName(contextLink.Id));
                    break;

                case LinkKind.PartyFinder:
                    if (Tip.MenuItem("Ouvrir l'annonce", FontAwesomeIcon.Users))
                        GameChat.OpenPartyFinder(contextLink.Id);
                    break;
            }

            Tip.EndMenu();
        }

        // Le choix de la couleur s'ouvre une fois le menu refermé.
        if (openPlayerColor)
        {
            openPlayerColor = false;
            ImGui.OpenPopup("##playerColor");
        }

        DrawPlayerColorPopup();
        DrawUrlConfirm();
    }

    private static void DrawCopyNameMenu(string name)
    {
        Tip.MenuTitle(name);
        Tip.MenuSeparator();
        if (Tip.MenuItem("Copier le nom", FontAwesomeIcon.Copy))
            ImGui.SetClipboardText(name);
    }

    private void DrawPlayerColorPopup()
    {
        using var popupStyle = Tip.PushPopupStyle();
        if (!ImGui.BeginPopup("##playerColor"))
            return;

        Tip.Title(colorPlayer);
        Tip.MenuSeparator();

        var current = Cfg.PlayerColors.TryGetValue(colorPlayer, out var chosen) ? chosen : Accent;
        var color = new Vector3(current.X, current.Y, current.Z);
        const ImGuiColorEditFlags Flags = ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview | ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel;
        ImGui.SetNextItemWidth(180f * S);
        if (ImGui.ColorPicker3("##pick", ref color, Flags))
            plugin.SetPlayerColor(colorPlayer, new Vector4(color, 1f), save: false);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Cfg.Save();

        if (Cfg.PlayerColors.ContainsKey(colorPlayer) && ImGui.Button("Retirer la couleur"))
        {
            plugin.RemovePlayerColor(colorPlayer);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawPlayerActions(ChatLink player)
    {
        if (player.Text != plugin.LocalName && Tip.MenuItem("Envoyer un message privé", FontAwesomeIcon.Envelope))
        {
            plugin.Game.SwitchToTell(player);
            Input?.FocusInput();
        }

        if (Tip.MenuItem("Copier le nom", FontAwesomeIcon.Copy))
            ImGui.SetClipboardText(player.TellTarget);

        if (Tip.MenuItem("Choisir sa couleur", FontAwesomeIcon.Palette))
        {
            colorPlayer = player.Text;
            openPlayerColor = true;
        }

        if (player.Text != plugin.LocalName && Tip.MenuItem("Masquer ses messages", FontAwesomeIcon.CommentSlash, danger: true))
            plugin.MutePlayer(player.Text);
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
        ImGui.Dummy(new Vector2(0f, 2f * S));

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
