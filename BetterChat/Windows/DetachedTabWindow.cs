using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using BetterChat.Core;
using static BetterChat.Windows.ChatWindow;

namespace BetterChat.Windows;

/// <summary>Un onglet sorti du chat, à placer où l'on veut : le combat ou la compagnie libre dans un coin de l'écran.</summary>
public sealed class DetachedTabWindow : Window
{
    private readonly Plugin plugin;
    private readonly ChatWindow chat;
    private readonly MessageList messages;
    private readonly ChatInputBar input;
    private readonly SearchBar search;

    private IDisposable? windowStyle;
    private IDisposable? windowColors;

    public DetachedTabWindow(Plugin plugin, ChatWindow chat, ChatTabSettings tab)
        : base($"Onglet de BetterChat###BetterChatTab{tab.Id}", BaseFlags)
    {
        this.plugin = plugin;
        this.chat = chat;
        Tab = tab;
        messages = new MessageList(plugin);
        input = new ChatInputBar(plugin, messages, chat) { ClickedIn = OnClickedIn };
        messages.Input = input;
        search = new SearchBar(chat, text => plugin.FeedFor(Tab)?.SetSearch(text));

        RespectCloseHotkey = false;
        Size = new Vector2(360, 220);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240, 140),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public ChatTabSettings Tab { get; }

    private Configuration Cfg => plugin.Configuration;

    public override bool DrawConditions()
    {
        if (Cfg.ChatVisible && Plugin.CurrentCharacterId != 0 && !HiddenByGame)
            return true;

        input.ReleaseFocus();
        return false;
    }

    /// <summary>Écrire ici passe sur le canal choisi pour cet onglet, s'il y en a un : la fenêtre de la CL écrit à la CL.</summary>
    private void OnClickedIn()
    {
        if (Tab.InputChannel != ChatKind.Other)
            plugin.Game.SwitchChannel(Tab.InputChannel);
    }

    public override void PreDraw()
    {
        // Au premier affichage, vers la droite de l'écran : elle ne tombe pas sur le chat.
        var viewport = ImGuiHelpers.MainViewport;
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X - 400f * S, viewport.Size.Y * 0.35f), ImGuiCond.FirstUseEver);

        // Chaque fenêtre détachée a son propre verrou, indépendant de celui du chat.
        Flags = BaseFlags;
        if (Tab.Locked)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        if (input.FocusRequested)
            ImGui.SetNextWindowFocus();

        PushWindowStyle(Cfg, out windowStyle, out windowColors);
    }

    public override void PostDraw()
    {
        windowColors?.Dispose();
        windowColors = null;
        windowStyle?.Dispose();
        windowStyle = null;
    }

    public override void Draw()
    {
        var feed = plugin.FeedFor(Tab);
        if (feed == null)
            return;

        using var popupPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f) * S);

        var font = chat.ChatFont();
        using (font != null ? font.Push() : null)
        {
            var size = ImGui.GetWindowSize();
            var headerH = MathF.Round(ImGui.GetTextLineHeight() + 12f * S);
            var inputH = MathF.Round(ImGui.GetTextLineHeight() + 20f * S);
            var searchH = search.Update(inputH);

            // La recherche a pu être effacée en sortant l'onglet du chat : elle suit ce que montre la barre.
            if (search.IsOpen)
                feed.SetSearch(search.Text);

            DrawHeader(feed, size, headerH);
            search.Draw(size, headerH, searchH, inputH, FontAwesomeIcon.SignInAlt);

            ImGui.SetCursorPos(new Vector2(0f, headerH + searchH));
            var translateH = input.AutoTranslateHeight(inputH);
            messages.Draw(feed, new Vector2(size.X, MathF.Max(size.Y - headerH - searchH - translateH - inputH, 10f)));
            input.DrawAutoTranslateBar(size, size.Y - inputH, translateH, inputH);

            ImGui.SetCursorPos(new Vector2(0f, size.Y - inputH));
            input.Draw(size, inputH);
        }
    }

    private void DrawHeader(ChatFeed feed, Vector2 size, float height)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var iconSize = IconButtonSize;
        var reattachX = RightButtonX(size.X, FontAwesomeIcon.SignInAlt, iconSize);
        var pinX = reattachX - iconSize - 2f * S;
        var buttonX = pinX - iconSize - 2f * S; // la loupe, le premier des trois boutons

        var textPos = origin + new Vector2(EdgeMargin * S, MathF.Floor((height - ImGui.GetTextLineHeight()) / 2f));
        drawList.PushClipRect(origin, origin + new Vector2(buttonX - 4f * S, height), true);
        messages.DrawOutlinedText(drawList, textPos, TextMain, Tab.Name);
        drawList.PopClipRect();

        drawList.AddLine(origin + new Vector2(0f, height), origin + new Vector2(size.X, height), Col(Line));

        // Le clic droit sur l'en-tête ouvre les options. L'en-tête reste libre pour déplacer la fenêtre.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + new Vector2(buttonX, height), false) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##detachedMenu");

        ImGui.SetCursorPos(new Vector2(buttonX, MathF.Round((height - iconSize) / 2f)));
        if (chat.IconButton("##search", FontAwesomeIcon.Search, iconSize, search.IsOpen))
            search.Toggle();

        ImGui.SetCursorPos(new Vector2(pinX, MathF.Round((height - iconSize) / 2f)));
        if (chat.IconButton("##pin", FontAwesomeIcon.Thumbtack, iconSize, Tab.Locked))
        {
            Tab.Locked = !Tab.Locked;
            Cfg.Save();
        }

        ImGui.SetCursorPos(new Vector2(reattachX, MathF.Round((height - iconSize) / 2f)));
        if (chat.IconButton("##reattach", FontAwesomeIcon.SignInAlt, iconSize))
            plugin.SetDetached(Tab, false);

        if (Tip.BeginMenu("##detachedMenu"))
        {
            Tip.MenuTitle(Tab.Name);
            Tip.MenuSeparator();

            if (Tip.MenuItem("Remettre dans le chat", FontAwesomeIcon.SignInAlt))
                plugin.SetDetached(Tab, false);

            if (Tip.MenuItem("Exporter dans un fichier...", FontAwesomeIcon.FileExport))
                plugin.ExportFeed(feed);

            if (Tip.MenuItem("Options de l'onglet", FontAwesomeIcon.SlidersH))
                plugin.OpenConfigAtTabs();

            Tip.EndMenu();
        }
    }
}
