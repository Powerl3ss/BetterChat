using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BetterChat.Chat;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>La zone de saisie : canal, texte, historique, commandes proposées.</summary>
public sealed partial class ChatWindow
{
    private const int MaxSuggestions = 8;
    private const int InputBufferBytes = 2048;
    private const float ErrorSeconds = 4f;

    private readonly ImGui.ImGuiInputTextCallbackPtrDelegate inputCallback;
    private readonly InputHistory history = new();

    private string inputText = string.Empty;
    private bool inputActive;
    private bool focusRequested;
    private bool cursorToEnd;

    // Texte à placer dans la zone au prochain passage (historique, commande complétée, lien...).
    private string? pendingText;

    // Canaux parcourus avec Tab quand aucune commande n'est proposée.
    private bool cycleChannelRequested;

    private List<CommandSuggestion> suggestions = new();
    private int selectedSuggestion = -1;
    private string suggestionsFor = string.Empty;
    private List<ChannelChoice>? channelMenu;
    private Vector2 channelPillMin;
    private float channelPillHeight;

    private string? inputError;
    private double inputErrorUntil;

    // ------------------------------------------------------------ Depuis le jeu

    /// <summary>Le joueur a appuyé sur la touche qui ouvre le chat. Appelé depuis la boucle du jeu.</summary>
    public void OnGameActivated(string? text)
    {
        focusRequested = true;
        if (text != null && !inputText.Contains(text, StringComparison.Ordinal))
            AppendText(text);

        directChatPending = text == null && GameChat.DirectChatEnabled();
    }

    // Avec « discussion directe », la lettre qui ouvre le chat est encore dans la file d'ImGui à cette image.
    private bool directChatPending;

    private void FocusInput() => focusRequested = true;

    /// <summary>Ajoute un texte à la fin de la saisie et y donne le focus (lien d'objet...).</summary>
    private void AppendToInput(string text)
    {
        AppendText(text);
        focusRequested = true;
    }

    private void AppendText(string text)
    {
        var current = pendingText ?? inputText;
        var joined = current.Length == 0 || current.EndsWith(' ') || text == "/" ? current + text : current + " " + text;
        SetInput(joined);
    }

    private void SetInput(string text)
    {
        if (inputActive)
        {
            pendingText = text;
        }
        else
        {
            inputText = text;
            cursorToEnd = true;
        }
    }

    // ------------------------------------------------------------ Dessin

    private void DrawInputBar(Vector2 size, float height)
    {
        var pad = EdgeMargin * S;
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        drawList.AddLine(origin + new Vector2(0, size.Y - height), origin + new Vector2(size.X, size.Y - height), Col(Line));

        var typedChannel = ChatChannels.FromTypedPrefix(inputText);
        var channel = typedChannel ?? plugin.Game.CurrentChannel;
        var label = ChannelText(channel, typedChannel != null);

        var rowH = ImGui.GetFrameHeight();
        var rowY = MathF.Round(size.Y - height + (height - rowH) / 2f); // sur un pixel entier : pastille et saisie restent nettes

        ImGui.SetCursorPos(new Vector2(pad, rowY));
        var pillW = DrawChannelPill(channel, label, rowH);
        DrawChannelMenu();

        ImGui.SameLine(0, 6f * S);
        ImGui.SetCursorPosY(rowY);
        DrawTextInput(size.X - pad * 2f - pillW - 6f * S, channel);

        DrawInputFeedback();
        DrawSuggestions();
    }

    private string ChannelText(ChatKind channel, bool typed)
    {
        if (channel == ChatKind.TellOutgoing && !typed)
            return plugin.Game.TellTarget.Length > 0 ? $"MP {plugin.Game.TellTarget}" : "Message privé";

        return GameChat.ChannelLabel(channel);
    }

    /// <summary>La pastille du canal actif. Un clic ouvre la liste, et sa couleur rappelle où partira le message.</summary>
    private float DrawChannelPill(ChatKind channel, string label, float height)
    {
        var color = plugin.Colors.Of(channel);
        var maxLabel = 170f * S;
        var labelW = MathF.Min(Measure(label), maxLabel);
        var size = new Vector2(labelW + 28f * S, height); // 8 de chaque côté, 12 entre le point et le texte

        var min = ImGui.GetCursorScreenPos();
        channelPillMin = min;
        channelPillHeight = height;
        if (ImGui.InvisibleButton("##channel", size))
        {
            channelMenu = GameChat.AvailableChannels();
            ImGui.OpenPopup("##channelMenu");
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, Col(color with { W = hovered ? 0.26f : 0.16f }), 8f * S);
        drawList.AddCircleFilled(min + new Vector2(11.5f * S, height / 2f), 3.5f * S, Col(color));

        var textPos = min + new Vector2(20f * S, (height - ImGui.GetTextLineHeight()) / 2f);
        drawList.PushClipRect(min, min + new Vector2(size.X - 4f * S, height), true);
        DrawOutlinedText(drawList, textPos, color, label);
        drawList.PopClipRect();

        if (hovered)
            DrawChannelTooltip(min, label, color);

        return size.X;
    }

    /// <summary>Infobulle posée au-dessus de la pastille, sans la cacher : elle ne suit pas la souris.</summary>
    private static void DrawChannelTooltip(Vector2 pillMin, string label, Vector4 color)
    {
        ImGui.SetNextWindowPos(pillMin - new Vector2(0f, 6f * S), ImGuiCond.Always, new Vector2(0f, 1f));
        using var tooltip = Tip.Begin();

        var start = ImGui.GetCursorScreenPos();
        var dotRadius = 3.5f * S;
        ImGui.GetWindowDrawList().AddCircleFilled(start + new Vector2(dotRadius, ImGui.GetTextLineHeight() / 2f), dotRadius, Col(color));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + dotRadius * 2f + 6f * S);
        ImGui.TextColored(color, label);

        ImGui.Dummy(new Vector2(0f, 2f * S));
        Tip.Hint("Clic : changer de canal");
        Tip.Hint("Tab dans la saisie : canal suivant");
    }

    // Menu des canaux : une ligne par canal (point et nom de sa couleur, commande à droite),
    // le canal actif surligné et coché, puis les derniers messages privés.
    private float ChannelRowHeight => MathF.Round(ImGui.GetTextLineHeight() + 10f * S);

    /// <summary>Le menu s'ouvre au-dessus de la pastille. S'il n'a pas la place (chat en haut de l'écran), il passe dessous.</summary>
    private void PlaceChannelMenu()
    {
        var gap = 6f * S;
        var channels = channelMenu?.Count ?? 0;
        var tells = plugin.RecentTells.People.Count;
        var rowStep = ChannelRowHeight + 2f * S;
        var headers = (tells > 0 ? 2 : 1) * (ImGui.GetTextLineHeight() + 10f * S);
        var estimatedHeight = (channels + tells) * rowStep + headers + 16f * S;
        var fitsAbove = channelPillMin.Y - gap - estimatedHeight >= ImGuiHelpers.MainViewport.Pos.Y;

        if (fitsAbove)
            ImGui.SetNextWindowPos(channelPillMin - new Vector2(0f, gap), ImGuiCond.Always, new Vector2(0f, 1f));
        else
            ImGui.SetNextWindowPos(channelPillMin + new Vector2(0f, channelPillHeight + gap), ImGuiCond.Always);
    }

    private void DrawChannelMenu()
    {
        PlaceChannelMenu();
        using var popupStyle = Tip.PushPopupStyle();
        if (!ImGui.BeginPopup("##channelMenu"))
            return;

        var channels = channelMenu ?? new List<ChannelChoice>();
        var tells = plugin.RecentTells.People;
        var current = plugin.Game.CurrentChannel;

        // Largeur commune à toutes les lignes : nom le plus long + commande la plus longue.
        var nameW = 0f;
        var detailW = 0f;
        foreach (var choice in channels)
        {
            nameW = MathF.Max(nameW, ImGui.CalcTextSize(choice.Label).X);
            detailW = MathF.Max(detailW, ImGui.CalcTextSize(ChatChannels.Command(choice.Kind)).X);
        }
        foreach (var person in tells)
        {
            nameW = MathF.Max(nameW, ImGui.CalcTextSize(person.Text).X);
            detailW = MathF.Max(detailW, ImGui.CalcTextSize(person.World).X);
        }
        var rowW = MathF.Max(200f * S, 24f * S + nameW + 24f * S + detailW + 30f * S);

        MenuHeader(FontAwesomeIcon.CommentDots, "Canal de discussion");
        for (var i = 0; i < channels.Count; i++)
        {
            var choice = channels[i];
            if (ChannelRow($"##ch{i}", rowW, plugin.Colors.Of(choice.Kind), null, choice.Label,
                           ChatChannels.Command(choice.Kind), current == choice.Kind))
            {
                plugin.Game.SwitchChannel(choice.Kind);
                FocusInput();
                ImGui.CloseCurrentPopup();
            }
        }

        if (tells.Count > 0)
        {
            ImGui.Dummy(new Vector2(0f, 2f * S));
            ImGui.Separator();
            MenuHeader(FontAwesomeIcon.Envelope, "Messages privés récents");
            var tellColor = plugin.Colors.Of(ChatKind.TellOutgoing);
            for (var i = 0; i < tells.Count; i++)
            {
                var person = tells[i];
                var active = current == ChatKind.TellOutgoing && plugin.Game.TellTarget == person.TellTarget;
                if (ChannelRow($"##tell{i}", rowW, tellColor, FontAwesomeIcon.User, person.Text, person.World, active))
                {
                    plugin.Game.SwitchToTell(person);
                    FocusInput();
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.Dummy(new Vector2(0f, 1f * S));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f * S);
        Tip.Hint("Tab dans la saisie : canal suivant");

        ImGui.EndPopup();
    }

    /// <summary>Petit titre de section du menu : icône et texte discrets.</summary>
    private static void MenuHeader(FontAwesomeIcon icon, string text)
    {
        ImGui.Dummy(new Vector2(0f, 2f * S));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f * S);
        Tip.Line(icon, Tip.Dim, text, color: Tip.Dim);
        ImGui.Dummy(new Vector2(0f, 1f * S));
    }

    /// <summary>
    /// Une ligne du menu : point (ou icône) et nom dans la couleur du canal, détail discret à droite,
    /// fond teinté et coche pour le canal actif. Renvoie vrai quand on clique dessus.
    /// </summary>
    private bool ChannelRow(string id, float width, Vector4 color, FontAwesomeIcon? icon, string name, string detail, bool active)
    {
        var rowH = ChannelRowHeight;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, rowH);
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, rowH));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var radius = 6f * S;

        // Fond : teinté de la couleur du canal s'il est actif, léger au survol.
        if (active)
        {
            dl.AddRectFilled(min, max, Col(color with { W = hovered ? 0.26f : 0.18f }), radius);
            dl.AddRect(min, max, Col(color with { W = 0.40f }), radius, ImDrawFlags.None, 1f);
        }
        else if (hovered)
        {
            dl.AddRectFilled(min, max, Col(new Vector4(1f, 1f, 1f, 0.07f)), radius);
        }

        var lineH = ImGui.GetTextLineHeight();
        var textY = min.Y + (rowH - lineH) / 2f;
        var centerY = min.Y + rowH / 2f;

        // Point de couleur, ou icône (messages privés).
        if (icon is { } fa)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = fa.ToIconString();
                var scale = 0.75f;
                var size = ImGui.CalcTextSize(glyph) * scale;
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
                           new Vector2(min.X + 12f * S - size.X / 2f, centerY - size.Y / 2f), Col(color with { W = 1f }), glyph);
            }
        }
        else
        {
            dl.AddCircleFilled(new Vector2(min.X + 12f * S, centerY), (active ? 4f : 3.5f) * S, Col(color with { W = 1f }));
        }

        // Nom du canal, dans sa couleur.
        dl.AddText(new Vector2(min.X + 24f * S, textY), Col(color with { W = 1f }), name);

        // Coche du canal actif, tout à droite ; la commande juste avant.
        var right = max.X - 10f * S;
        if (active)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Check.ToIconString();
                var scale = 0.8f;
                var size = ImGui.CalcTextSize(glyph) * scale;
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
                           new Vector2(right - size.X, centerY - size.Y / 2f), Col(color with { W = 1f }), glyph);
            }
        }
        right -= 20f * S;

        if (detail.Length > 0)
        {
            var detailSize = ImGui.CalcTextSize(detail);
            dl.AddText(new Vector2(right - detailSize.X, textY), Col(hovered || active ? Tip.Text with { W = 0.75f } : Tip.Dim), detail);
        }

        return clicked;
    }

    private void DrawTextInput(float width, ChatKind channel)
    {
        // Le contour prend la couleur du canal pendant la saisie : on voit où part le message.
        using var border = ImRaii.PushColor(ImGuiCol.Border, plugin.Colors.Of(channel) with { W = inputActive ? 0.6f : 0f });
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, inputActive ? 1f : 0f);
        using var background = ImRaii.PushColor(ImGuiCol.FrameBg, InputBg);

        if (focusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            focusRequested = false;
            cursorToEnd = true;
            plugin.RefreshCommandsIfNeeded();
            AbsorbDirectChatLetter();
        }

        ImGui.SetNextItemWidth(width);
        const ImGuiInputTextFlags Flags =
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways |
            ImGuiInputTextFlags.CallbackHistory | ImGuiInputTextFlags.CallbackCompletion;

        var submitted = ImGui.InputTextWithHint("##chatInput", "Écris ton message...", ref inputText, InputBufferBytes, Flags, inputCallback);
        inputActive = ImGui.IsItemActive();

        if (inputActive && inputText.Length > 0 && inputText[0] == '/')
            plugin.RefreshCommandsIfNeeded();

        if (cycleChannelRequested)
        {
            cycleChannelRequested = false;
            CycleChannel();
        }

        UpdateSuggestions();

        if (submitted)
            Submit();
    }

    /// <summary>Récupère la lettre qui vient d'ouvrir le chat, encore en file d'attente à cette image.</summary>
    private void AbsorbDirectChatLetter()
    {
        if (!directChatPending)
            return;

        directChatPending = false;
        var queue = ImGui.GetIO().InputQueueCharacters;
        if (queue.Size == 0)
            return;

        var c = (char)queue[0];
        if (!char.IsControl(c))
            SetInput(inputText + c);
    }

    private void Submit()
    {
        // Une commande choisie à la flèche : Entrée la complète au lieu d'envoyer ce qui est tapé.
        if (selectedSuggestion >= 0 && selectedSuggestion < suggestions.Count)
        {
            inputText = suggestions[selectedSuggestion].Command + " ";
            selectedSuggestion = -1;
            cursorToEnd = true;
            focusRequested = true;
            return;
        }

        var text = inputText;
        switch (plugin.Game.Send(text))
        {
            case SendResult.Sent:
                history.Add(ChatText.Normalize(text));
                inputText = string.Empty;
                inputError = null;
                if (Cfg.KeepFocusAfterSend)
                    focusRequested = true;
                break;

            case SendResult.Empty:
                inputText = string.Empty;
                break;

            case SendResult.TooLong:
                ShowInputError($"Message trop long : {ChatText.ByteLength(text)} / {ChatText.MaxBytes} octets. Il n'a pas été envoyé.");
                focusRequested = true;
                break;

            case SendResult.InvalidCharacters:
                ShowInputError("Ce message contient des caractères refusés par le jeu. Il n'a pas été envoyé.");
                focusRequested = true;
                break;
        }
    }

    private void ShowInputError(string message)
    {
        inputError = message;
        inputErrorUntil = ImGui.GetTime() + ErrorSeconds;
    }

    /// <summary>Compteur à l'approche de la limite, ou message d'erreur, juste au-dessus de la zone de saisie.</summary>
    private void DrawInputFeedback()
    {
        string? text = null;
        var color = TextDim;

        if (inputError != null && ImGui.GetTime() < inputErrorUntil)
        {
            text = inputError;
            color = Danger;
        }
        else
        {
            inputError = null;
            var bytes = ChatText.ByteLength(inputText);
            if (bytes >= ChatText.MaxBytes - 100)
            {
                text = $"{bytes} / {ChatText.MaxBytes}";
                color = bytes > ChatText.MaxBytes ? Danger : TextDim;
            }
        }

        if (text == null)
            return;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var size = ImGui.CalcTextSize(text);
        var pos = new Vector2(max.X - size.X - 4f * S, min.Y - size.Y - 6f * S); // le fond s'aligne sur le bord de la saisie
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos - new Vector2(4f, 2f) * S, pos + size + new Vector2(4f, 2f) * S, Col(PopupBg), 5f * S);
        drawList.AddText(pos, Col(color), text);
    }

    // ------------------------------------------------------------ Canal suivant

    private void CycleChannel()
    {
        var channels = GameChat.AvailableChannels();
        var index = channels.FindIndex(c => c.Kind == plugin.Game.CurrentChannel);
        plugin.Game.SwitchChannel(channels[(index + 1) % channels.Count].Kind);
    }

    // ------------------------------------------------------------ Commandes proposées

    private void UpdateSuggestions()
    {
        if (!inputActive || !Cfg.CommandSuggestions)
        {
            suggestions.Clear();
            selectedSuggestion = -1;
            suggestionsFor = string.Empty;
            return;
        }

        if (inputText == suggestionsFor)
            return;

        suggestionsFor = inputText;
        selectedSuggestion = -1;
        suggestions = plugin.Commands.Suggest(inputText, MaxSuggestions);
    }

    private void DrawSuggestions()
    {
        if (suggestions.Count == 0)
            return;

        var min = ImGui.GetItemRectMin();
        var lineH = ImGui.GetTextLineHeight() + Tip.Spacing.Y; // espacement de l'infobulle stylée
        var height = lineH * (suggestions.Count + 1) + Tip.Padding.Y * 2f;
        ImGui.SetNextWindowPos(new Vector2(min.X, min.Y - height - 6f * S));

        using var tooltip = Tip.Begin();

        for (var i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            ImGui.TextColored(i == selectedSuggestion ? Accent : TextMain, suggestion.Command);
            if (suggestion.Description.Length > 0)
            {
                ImGui.SameLine(0, 12f * S);
                ImGui.TextColored(TextDim, Shorten(suggestion.Description, 60));
            }
        }

        Tip.Hint("Tab : compléter  |  Flèches : choisir");
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    // ------------------------------------------------------------ Rappel de la zone de texte

    private unsafe int OnInputCallback(ImGuiInputTextCallbackDataPtr data)
    {
        if (data.EventFlag.HasFlag(ImGuiInputTextFlags.CallbackAlways))
        {
            if (pendingText != null)
            {
                Replace(data, pendingText);
                pendingText = null;
                cursorToEnd = false;
            }
            else if (cursorToEnd)
            {
                data.ClearSelection();
                data.CursorPos = data.BufTextLen;
                cursorToEnd = false;
            }

            return 0;
        }

        if (data.EventFlag.HasFlag(ImGuiInputTextFlags.CallbackHistory))
        {
            var up = data.EventKey == ImGuiKey.UpArrow;

            // Les flèches parcourent d'abord les commandes proposées, sinon les messages déjà envoyés.
            if (suggestions.Count > 0)
            {
                var step = up ? -1 : 1;
                selectedSuggestion = selectedSuggestion < 0
                    ? (up ? suggestions.Count - 1 : 0)
                    : (selectedSuggestion + step + suggestions.Count) % suggestions.Count;
                return 0;
            }

            var current = System.Text.Encoding.UTF8.GetString(data.BufTextSpan);
            var text = up ? history.Older(current) : history.Newer();
            if (text != null)
                Replace(data, text);

            return 0;
        }

        if (data.EventFlag.HasFlag(ImGuiInputTextFlags.CallbackCompletion))
        {
            if (suggestions.Count > 0)
            {
                var choice = suggestions[Math.Max(selectedSuggestion, 0)];
                Replace(data, choice.Command + " ");
                selectedSuggestion = -1;
            }
            else
            {
                cycleChannelRequested = true;
            }
        }

        return 0;
    }

    private static void Replace(ImGuiInputTextCallbackDataPtr data, string text)
    {
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, text);
        data.ClearSelection();
        data.CursorPos = data.BufTextLen;
    }
}
