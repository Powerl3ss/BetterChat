using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using BetterChat.Chat;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>La fenêtre de chat : onglets en haut, messages au milieu, zone de saisie en bas.</summary>
public sealed partial class ChatWindow : Window, IDisposable
{
    internal const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoFocusOnAppearing;

    // Palette commune avec les réglages.
    internal static readonly Vector4 WindowBg = new(0.071f, 0.078f, 0.106f, 1f);
    internal static readonly Vector4 InputBg = new(0.140f, 0.150f, 0.200f, 0.85f);
    internal static readonly Vector4 HoverOverlay = new(1f, 1f, 1f, 0.05f);
    internal static readonly Vector4 Line = new(1f, 1f, 1f, 0.075f);
    internal static readonly Vector4 TextMain = new(0.93f, 0.94f, 0.97f, 1f);
    internal static readonly Vector4 TextDim = new(0.60f, 0.63f, 0.71f, 1f);
    internal static readonly Vector4 Danger = new(1f, 0.47f, 0.47f, 1f);
    internal static readonly Vector4 PopupBg = new(0.09f, 0.10f, 0.13f, 0.98f);

    // Marge commune à gauche et à droite : onglets, messages, saisie et icônes s'alignent dessus.
    internal const float EdgeMargin = 10f;

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;
    private Vector4 Accent => Cfg.AccentColor with { W = 1f };

    internal static float S => ImGuiHelpers.GlobalScale;

    private readonly MessageList messages;
    private readonly ChatInputBar input;

    // Police à sa vraie taille : agrandir le texte avec SetWindowFontScale le rend flou.
    private IFontHandle? chatFont;
    private float chatFontPx;

    private IDisposable? windowStyle;
    private IDisposable? windowColors;

    private DateTime lastDraw = DateTime.MinValue;

    public ChatWindow(Plugin plugin) : base("BetterChat###BetterChatWindow", BaseFlags)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false; // Échap sert à quitter la saisie, pas à fermer le chat
        Size = new Vector2(560, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 140),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        messages = new MessageList(plugin);
        input = new ChatInputBar(plugin, messages, this);
        messages.Input = input;
    }

    public void Dispose() => chatFont?.Dispose();

    /// <summary>Le joueur a appuyé sur la touche qui ouvre le chat : la saisie de la fenêtre principale la reçoit.</summary>
    public void OnGameActivated(string? text) => input.OnGameActivated(text);

    /// <summary>Vrai si la fenêtre a été dessinée à l'instant : sinon, seule la zone de saisie du jeu peut servir.</summary>
    public bool IsShownNow => IsOpen && (DateTime.UtcNow - lastDraw).TotalSeconds < 0.5;

    /// <summary>Écran de chargement ou cinématique : le chat s'efface, comme le reste de l'interface du jeu.</summary>
    public static bool HiddenByGame =>
        Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
        Plugin.Condition[ConditionFlag.BetweenAreas] ||
        Plugin.Condition[ConditionFlag.BetweenAreas51];

    public override bool DrawConditions()
    {
        if (Plugin.CurrentCharacterId != 0 && !HiddenByGame)
            return true;

        input.ReleaseFocus();
        return false;
    }

    public override void PreDraw()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var defaultPos = viewport.Pos + new Vector2(24f * S, viewport.Size.Y - 340f * S);
        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);

        Flags = BaseFlags;
        if (Cfg.Locked)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        if (input.FocusRequested)
            ImGui.SetNextWindowFocus();

        PushWindowStyle(Cfg, out windowStyle, out windowColors);
    }

    /// <summary>L'apparence commune à la fenêtre de chat et aux onglets détachés.</summary>
    internal static void PushWindowStyle(Configuration cfg, out IDisposable style, out IDisposable colors)
    {
        var accent = cfg.AccentColor;
        style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                      .Push(ImGuiStyleVar.WindowRounding, 12f * S)
                      .Push(ImGuiStyleVar.WindowBorderSize, 1f)
                      .Push(ImGuiStyleVar.ScrollbarSize, 6f * S)
                      .Push(ImGuiStyleVar.ScrollbarRounding, 6f * S)
                      .Push(ImGuiStyleVar.PopupRounding, 8f * S)
                      .Push(ImGuiStyleVar.FrameRounding, 8f * S);
        colors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg with { W = Math.Clamp(cfg.WindowOpacity, 0f, 1f) })
                       .Push(ImGuiCol.Border, Line)
                       .Push(ImGuiCol.ChildBg, Vector4.Zero)
                       .Push(ImGuiCol.PopupBg, PopupBg)
                       .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                       .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.14f))
                       .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.24f))
                       .Push(ImGuiCol.ScrollbarGrabActive, accent with { W = 0.6f })
                       .Push(ImGuiCol.ResizeGrip, new Vector4(1f, 1f, 1f, 0.06f))
                       .Push(ImGuiCol.ResizeGripHovered, accent with { W = 0.5f })
                       .Push(ImGuiCol.ResizeGripActive, accent with { W = 0.8f });
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
        lastDraw = DateTime.UtcNow;

        // Les onglets peuvent avoir été recréés entre-temps (réglages) : la recherche suit l'onglet affiché.
        if (searchOpen)
            plugin.ActiveFeed.SetSearch(searchText);

        // La fenêtre n'a pas de marges, mais ses infobulles et ses menus en ont besoin.
        using var popupPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f) * S);

        var font = ChatFont();
        using (font != null ? font.Push() : null)
        {
            var size = ImGui.GetWindowSize();
            var tabH = MathF.Round(ImGui.GetTextLineHeight() + 14f * S);
            var inputH = MathF.Round(ImGui.GetTextLineHeight() + 20f * S);
            var searchH = UpdateSearchReveal(inputH);

            var tellsH = ConversationBarHeight();

            DrawTabBar(size, tabH);
            if (searchH > 0f)
                DrawRevealedSearchBar(size, tabH, searchH, inputH);
            if (tellsH > 0f)
                DrawConversationBar(size, tabH + searchH, tellsH);

            var top = tabH + searchH + tellsH;
            var translateH = input.AutoTranslateHeight(inputH);
            ImGui.SetCursorPos(new Vector2(0, top));
            messages.Draw(plugin.ActiveFeed, new Vector2(size.X, MathF.Max(size.Y - top - translateH - inputH, 10f)));
            input.DrawAutoTranslateBar(size, size.Y - inputH, translateH, inputH);

            ImGui.SetCursorPos(new Vector2(0, size.Y - inputH));
            input.Draw(size, inputH);
        }
    }

    internal IFontHandle? ChatFont()
    {
        var px = MathF.Round(Math.Clamp(Cfg.FontSize, 10f, 36f));
        if (chatFont == null || Math.Abs(px - chatFontPx) > 0.1f)
        {
            chatFont?.Dispose();
            chatFontPx = px;
            chatFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
                e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(px)));
        }

        return chatFont.Available ? chatFont : null;
    }

    // ------------------------------------------------------------ Petits dessins

    internal static uint Col(Vector4 color) => ImGui.GetColorU32(color);

    private float Measure(string text) => messages.Measure(text);

    internal void DrawOutlinedText(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text) =>
        messages.DrawOutlinedText(drawList, pos, color, text);

    /// <summary>
    /// Position du bouton le plus à droite : c'est le dessin de l'icône qui s'aligne sur la marge,
    /// le bouton autour la dépasse légèrement (il n'est visible qu'au survol).
    /// </summary>
    internal static float RightButtonX(float windowWidth, FontAwesomeIcon icon, float buttonSize)
    {
        var inset = (buttonSize - ConfigWindow.IconSize(icon).X) / 2f;
        return MathF.Round(windowWidth - EdgeMargin * S + inset - buttonSize); // sur un pixel entier, comme la croix et le fond
    }

    /// <summary>
    /// Fond arrondi et discret derrière une icône, à la place d'un contour : sur un dessin aussi petit, un contour
    /// bave sur les côtés. Il s'estompe à mesure que le fond de la fenêtre devient opaque, où il ne sert plus à rien.
    /// </summary>
    private void DrawIconBackdrop(Vector2 min, Vector2 max)
    {
        if (!Cfg.TextOutline || Cfg.OutlineStrength <= 0f)
            return;

        var transparency = 1f - Math.Clamp(Cfg.WindowOpacity, 0f, 1f);
        var alpha = 0.55f * Math.Clamp(Cfg.OutlineStrength, 0f, 1f) * transparency;
        ImGui.GetWindowDrawList().AddRectFilled(min, max, Col(new Vector4(0f, 0f, 0f, alpha)), 7f * S);
    }

    /// <summary>La même taille pour tous les boutons à icône du chat, sur un nombre entier de pixels.</summary>
    internal static float IconButtonSize => MathF.Round(ImGui.GetTextLineHeight() + 8f * S);

    /// <summary>
    /// La croix de la police est nettement plus petite que les autres icônes : elle est tracée à la main,
    /// à une taille accordée à la leur et exactement au centre.
    /// </summary>
    private static void DrawCloseCross(ImDrawListPtr drawList, Vector2 center, Vector4 color)
    {
        var reach = ConfigWindow.IconGlyphSize(FontAwesomeIcon.Cog).Y / 2f * 0.72f;
        var thickness = MathF.Max(1.5f, 2f * S);

        // Tracé direct : AddLine décale tout d'un demi-pixel en bas à droite, ce qui décentrait la croix.
        drawList.PathLineTo(center - new Vector2(reach, reach));
        drawList.PathLineTo(center + new Vector2(reach, reach));
        drawList.PathStroke(Col(color), ImDrawFlags.None, thickness);
        drawList.PathLineTo(center + new Vector2(-reach, reach));
        drawList.PathLineTo(center + new Vector2(reach, -reach));
        drawList.PathStroke(Col(color), ImDrawFlags.None, thickness);
    }

    internal bool IconButton(string id, FontAwesomeIcon icon, float size, string? tooltip, bool active = false)
    {
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        // Le dessin de l'icône est calé sur un pixel entier : les fonds se centrent sur lui, pas sur le bouton.
        var isCross = icon == FontAwesomeIcon.Times;
        var center = isCross
            ? new Vector2(MathF.Round(min.X + size / 2f), MathF.Round(min.Y + size / 2f))
            : ConfigWindow.IconVisualCenter(icon, min, min + new Vector2(size, size));
        var half = new Vector2(size / 2f, size / 2f);

        DrawIconBackdrop(center - half, center + half);

        if (active)
            drawList.AddRectFilled(center - half, center + half, Col(Accent with { W = 0.20f }), 7f * S);
        else if (hovered)
            drawList.AddRectFilled(center - half, center + half, Col(HoverOverlay), 7f * S);

        // Hors survol, un gris plus clair quand le contraste est activé : il se détache mieux du décor.
        var idle = Cfg.TextOutline ? Vector4.Lerp(TextDim, TextMain, 0.6f) : TextDim;
        var color = active ? Accent : hovered ? TextMain : idle;
        if (isCross)
            DrawCloseCross(drawList, center, color);
        else
            ConfigWindow.DrawIconCentered(icon, min, min + new Vector2(size, size), color);

        if (tooltip != null && hovered)
            Tip.Show(tooltip);
        return clicked;
    }
}
