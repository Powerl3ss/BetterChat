using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoFocusOnAppearing;

    // Palette commune avec les réglages.
    private static readonly Vector4 WindowBg = new(0.071f, 0.078f, 0.106f, 1f);
    private static readonly Vector4 InputBg = new(0.140f, 0.150f, 0.200f, 0.85f);
    private static readonly Vector4 HoverOverlay = new(1f, 1f, 1f, 0.05f);
    private static readonly Vector4 Line = new(1f, 1f, 1f, 0.075f);
    private static readonly Vector4 TextMain = new(0.93f, 0.94f, 0.97f, 1f);
    private static readonly Vector4 TextDim = new(0.60f, 0.63f, 0.71f, 1f);
    private static readonly Vector4 Danger = new(1f, 0.47f, 0.47f, 1f);
    private static readonly Vector4 PopupBg = new(0.09f, 0.10f, 0.13f, 0.98f);

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;
    private Vector4 Accent => Cfg.AccentColor with { W = 1f };

    private static float S => ImGuiHelpers.GlobalScale;

    // Police à sa vraie taille : agrandir le texte avec SetWindowFontScale le rend flou.
    private IFontHandle? chatFont;
    private float chatFontPx;

    private IDisposable? windowStyle;
    private IDisposable? windowColors;

    // Discrétion quand le chat ne sert pas : 0 = au repos, 1 = en cours d'utilisation.
    private float presence = 1f;
    private DateTime lastActivity = DateTime.UtcNow;
    private long lastSeenMessage;
    private Vector2 lastWindowMin;
    private Vector2 lastWindowMax;
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
        inputCallback = OnInputCallback;
    }

    public void Dispose() => chatFont?.Dispose();

    /// <summary>Vrai si la fenêtre a été dessinée à l'instant : sinon, seule la zone de saisie du jeu peut servir.</summary>
    public bool IsShownNow => IsOpen && (DateTime.UtcNow - lastDraw).TotalSeconds < 0.5;

    public override bool DrawConditions() => Plugin.CurrentCharacterId != 0;

    public override void PreDraw()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var defaultPos = viewport.Pos + new Vector2(24f * S, viewport.Size.Y - 340f * S);
        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);

        Flags = BaseFlags;
        if (Cfg.Locked)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        if (focusRequested)
            ImGui.SetNextWindowFocus();

        UpdatePresence();
        var frame = FrameAlpha;

        windowStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                            .Push(ImGuiStyleVar.WindowRounding, 12f * S)
                            .Push(ImGuiStyleVar.WindowBorderSize, 1f)
                            .Push(ImGuiStyleVar.ScrollbarSize, 6f * S)
                            .Push(ImGuiStyleVar.ScrollbarRounding, 6f * S)
                            .Push(ImGuiStyleVar.PopupRounding, 8f * S)
                            .Push(ImGuiStyleVar.FrameRounding, 8f * S);
        windowColors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg with { W = Math.Clamp(Cfg.WindowOpacity, 0f, 1f) * frame })
                             .Push(ImGuiCol.Border, Line with { W = Line.W * frame })
                             .Push(ImGuiCol.ChildBg, Vector4.Zero)
                             .Push(ImGuiCol.PopupBg, PopupBg)
                             .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                             .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.14f * frame))
                             .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.24f))
                             .Push(ImGuiCol.ScrollbarGrabActive, Accent with { W = 0.6f })
                             .Push(ImGuiCol.ResizeGrip, new Vector4(1f, 1f, 1f, 0.06f * frame))
                             .Push(ImGuiCol.ResizeGripHovered, Accent with { W = 0.5f })
                             .Push(ImGuiCol.ResizeGripActive, Accent with { W = 0.8f });
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
        lastWindowMin = ImGui.GetWindowPos();
        lastWindowMax = lastWindowMin + ImGui.GetWindowSize();

        // Les onglets peuvent avoir été recréés entre-temps (réglages) : la recherche suit l'onglet affiché.
        if (searchOpen)
            plugin.ActiveFeed.SetSearch(searchText);

        var font = ChatFont();
        using (font != null ? font.Push() : null)
        {
            var size = ImGui.GetWindowSize();
            var tabH = MathF.Round(ImGui.GetTextLineHeight() + 14f * S);
            var inputH = MathF.Round(ImGui.GetTextLineHeight() + 20f * S);
            var searchH = searchOpen ? inputH : 0f;

            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, MathF.Max(FrameAlpha, 0.001f) * ImGui.GetStyle().Alpha))
            {
                DrawTabBar(size, tabH);
                if (searchOpen)
                    DrawSearchBar(size, tabH, searchH);
            }

            ImGui.SetCursorPos(new Vector2(0, tabH + searchH));
            DrawMessages(new Vector2(size.X, MathF.Max(size.Y - tabH - searchH - inputH, 10f)));

            ImGui.SetCursorPos(new Vector2(0, size.Y - inputH));
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, MathF.Max(FrameAlpha, 0.001f) * ImGui.GetStyle().Alpha))
                DrawInputBar(size, inputH);
        }
    }

    private IFontHandle? ChatFont()
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

    // ------------------------------------------------------------ Discrétion

    private float FrameAlpha => Cfg.IdleFade ? Lerp(Math.Clamp(Cfg.IdleFrameOpacity, 0f, 1f), 1f, presence) : 1f;
    private float TextAlpha => Cfg.IdleFade ? Lerp(Math.Clamp(Cfg.IdleTextOpacity, 0f, 1f), 1f, presence) : 1f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Le cadre s'efface quand personne ne se sert du chat, et revient au survol ou à l'arrivée d'un message.</summary>
    private void UpdatePresence()
    {
        var now = DateTime.UtcNow;
        var mouse = ImGui.GetMousePos();
        var hovered = mouse.X >= lastWindowMin.X && mouse.X < lastWindowMax.X && mouse.Y >= lastWindowMin.Y && mouse.Y < lastWindowMax.Y;

        var newest = plugin.Messages.LastId;
        if (newest != lastSeenMessage)
        {
            lastSeenMessage = newest;
            var lines = plugin.Messages.Lines;
            if (lines.Count > 0 && !lines[^1].FromHistory && !ChatKinds.IsBattle(lines[^1].Kind))
                lastActivity = now;
        }

        if (hovered || inputActive || searchOpen || focusRequested || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup))
            lastActivity = now;

        var awake = (now - lastActivity).TotalSeconds < Math.Max(1, Cfg.IdleSeconds);
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        var speed = awake ? 12f : 2.5f; // revient vite, s'efface doucement
        presence += ((awake ? 1f : 0f) - presence) * (1f - MathF.Exp(-speed * dt));
    }

    // ------------------------------------------------------------ Petits dessins

    private static uint Col(Vector4 color) => ImGui.GetColorU32(color);

    private bool IconButton(string id, FontAwesomeIcon icon, float size, string? tooltip, bool active = false)
    {
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        if (active)
            drawList.AddRectFilled(min, min + new Vector2(size, size), Col(Accent with { W = 0.20f }), 7f * S);
        else if (hovered)
            drawList.AddRectFilled(min, min + new Vector2(size, size), Col(HoverOverlay), 7f * S);

        ConfigWindow.DrawIconCentered(icon, min, min + new Vector2(size, size), active ? Accent : hovered ? TextMain : TextDim);

        if (tooltip != null && hovered)
            ImGui.SetTooltip(tooltip);
        return clicked;
    }
}
