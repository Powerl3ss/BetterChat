using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using BetterChat.Chat;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>Fenêtre de réglages : en-tête, barre de navigation à gauche, pages, statut en bas.</summary>
public sealed partial class ConfigWindow : Window, IDisposable
{
    private enum Page { Home, Tabs, Display, Alerts, About }

    private static readonly (Page Page, FontAwesomeIcon Icon, string Label)[] Pages =
    {
        (Page.Home, FontAwesomeIcon.Home, "Accueil"),
        (Page.Tabs, FontAwesomeIcon.Columns, "Onglets"),
        (Page.Display, FontAwesomeIcon.Palette, "Affichage"),
        (Page.Alerts, FontAwesomeIcon.Bell, "Messages et alertes"),
        (Page.About, FontAwesomeIcon.InfoCircle, "À propos"),
    };

    private const string RepoUrl = "https://github.com/Powerl3ss/BetterChat";

    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Configuration;

    private Page page = Page.Home;
    private int displaySection;
    private int editedTab;
    private string newWord = string.Empty;
    private string newSpamWord = string.Empty;
    private string newColorPlayer = string.Empty;

    // Styles de fenêtre poussés avant Begin (PreDraw) et retirés après End (PostDraw).
    private IDisposable? windowStyle;
    private IDisposable? windowColors;

    private Task<IDalamudTextureWrap?>? logoTask;

    public ConfigWindow(Plugin plugin)
        : base("BetterChat | Réglages###BetterChatSettings",
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(880, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedConstraints;

        // Vraies polices de grande taille (au lieu d'agrandir le texte, ce qui le rend flou).
        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        // La police par défaut est dessinée à des tailles fixes : toute autre taille est étirée et devient floue.
        // 14 et 18 points (soit 18,67 et 24 pixels) sont des tailles exactes.
        titleFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(14f * 4f / 3f)));
        heroFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(24f)));
    }

    private readonly IFontHandle titleFont;
    private readonly IFontHandle heroFont;

    // Tailles en pixels "non mis à l'échelle" (Dalamud applique l'échelle de l'interface).
    private const float HeaderHeight = 44f;
    private static readonly WindowSizeConstraints ExpandedConstraints = new()
    {
        MinimumSize = new Vector2(780, 560),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    private static readonly WindowSizeConstraints CollapsedConstraints = new()
    {
        MinimumSize = new Vector2(780, HeaderHeight),
        MaximumSize = new Vector2(float.MaxValue, HeaderHeight),
    };
    private static readonly WindowSizeConstraints FoldingConstraints = new()
    {
        MinimumSize = new Vector2(780, HeaderHeight),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    // Fenêtre repliée : seul l'en-tête reste visible.
    private bool collapsed;
    private float expandedHeight;
    private Vector2 lastSize;

    private void ToggleWindowCollapsed()
    {
        if (folding)
            return;

        if (!collapsed)
        {
            expandedHeight = lastSize.Y;
            collapsed = true;
            StartFold(lastSize.Y, HeaderHeight * S);
        }
        else
        {
            collapsed = false;
            StartFold(lastSize.Y, Math.Max(expandedHeight, 560f * S));
        }
    }

    // --- Enregistrement différé des réglages
    private bool savePending;

    /// <summary>Demande l'enregistrement des réglages : il est fait dès que plus rien n'est en cours de modification.</summary>
    private void SaveSoon() => savePending = true;

    private void FlushSave()
    {
        if (!savePending)
            return;
        savePending = false;
        Cfg.Save();
    }

    public override void OnClose() => FlushSave();

    public void Dispose()
    {
        FlushSave();
        titleFont.Dispose();
        heroFont.Dispose();
        if (logoTask is { IsCompletedSuccessfully: true })
            logoTask.Result?.Dispose();
    }

    /// <summary>Ouvre directement la page des onglets, sur l'onglet affiché.</summary>
    public void ShowTabsPage()
    {
        page = Page.Tabs;
        editedTab = Cfg.ActiveTab;
    }

    // =================================================================== Cadre

    public override void PreDraw()
    {
        windowStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                            .Push(ImGuiStyleVar.WindowRounding, 14f * S)
                            .Push(ImGuiStyleVar.WindowBorderSize, 1f);
        windowColors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg)
                             .Push(ImGuiCol.Border, Line);

        // Repli / dépli animé de la fenêtre.
        ApplyFoldAnimation();
    }

    public override void PostDraw()
    {
        // Enregistré une fois le geste fini (curseur relâché, couleur choisie, texte validé),
        // et non à chaque image pendant qu'on fait glisser un curseur.
        if (savePending && !ImGui.IsAnyItemActive())
            FlushSave();

        windowColors?.Dispose();
        windowColors = null;
        windowStyle?.Dispose();
        windowStyle = null;
    }

    public override void Draw()
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f * S)
                                .Push(ImGuiStyleVar.GrabRounding, 7f * S)
                                .Push(ImGuiStyleVar.PopupRounding, 8f * S)
                                .Push(ImGuiStyleVar.ScrollbarRounding, 8f * S)
                                .Push(ImGuiStyleVar.ScrollbarSize, 10f * S)
                                .Push(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f) * S)
                                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 8f) * S)
                                .Push(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * S);
        using var colors = ImRaii.PushColor(ImGuiCol.Text, TextMain)
                                 .Push(ImGuiCol.TextDisabled, TextDim)
                                 .Push(ImGuiCol.ChildBg, Vector4.Zero)
                                 .Push(ImGuiCol.FrameBg, InputBg)
                                 .Push(ImGuiCol.FrameBgHovered, InputBgHover)
                                 .Push(ImGuiCol.FrameBgActive, InputBgHover)
                                 .Push(ImGuiCol.Button, InputBg)
                                 .Push(ImGuiCol.ButtonHovered, InputBgHover)
                                 .Push(ImGuiCol.ButtonActive, AccentSoft)
                                 .Push(ImGuiCol.Header, AccentSoft)
                                 .Push(ImGuiCol.HeaderHovered, AccentSoft)
                                 .Push(ImGuiCol.HeaderActive, AccentBorder)
                                 .Push(ImGuiCol.CheckMark, Accent)
                                 .Push(ImGuiCol.SliderGrab, Accent)
                                 .Push(ImGuiCol.SliderGrabActive, Vector4.Lerp(Accent, White, 0.2f))
                                 .Push(ImGuiCol.PopupBg, new Vector4(0.09f, 0.10f, 0.13f, 0.98f))
                                 .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                                 .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f))
                                 .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.20f))
                                 .Push(ImGuiCol.ScrollbarGrabActive, AccentBorder)
                                 .Push(ImGuiCol.Separator, Line);

        var size = ImGui.GetWindowSize();
        lastSize = size;
        var origin = ImGui.GetWindowPos();
        var headerH = HeaderHeight * S;
        var footerH = 44f * S;
        var sidebarW = 64f * S;
        var drawList = ImGui.GetWindowDrawList();

        DrawAnimatedBackground(origin, size);

        // Repliée, ou pas encore assez haute pendant l'animation : seulement l'en-tête.
        if (collapsed || size.Y < headerH + footerH + 80f * S)
        {
            DrawTitleBar(size, headerH);
            return;
        }

        drawList.AddRectFilled(origin + new Vector2(0, headerH), origin + new Vector2(sidebarW, size.Y - footerH), Col(SidebarBg));
        drawList.AddLine(origin + new Vector2(0, headerH), origin + new Vector2(size.X, headerH), Col(Line));
        drawList.AddLine(origin + new Vector2(sidebarW, headerH), origin + new Vector2(sidebarW, size.Y - footerH), Col(Line));
        drawList.AddLine(origin + new Vector2(0, size.Y - footerH), origin + new Vector2(size.X, size.Y - footerH), Col(Line));

        DrawTitleBar(size, headerH);
        DrawSidebar(headerH, sidebarW);

        ImGui.SetCursorPos(new Vector2(sidebarW + 1, headerH + 1));
        using (var child = ImRaii.Child("##page", new Vector2(size.X - sidebarW - 1, size.Y - headerH - footerH - 2), false))
        {
            if (child.Success)
                DrawPage();
        }

        DrawFooter(size, footerH);
    }

    private void DrawTitleBar(Vector2 size, float headerH)
    {
        var pad = 18f * S;
        var logoSize = 24f * S;

        ImGui.SetCursorPos(new Vector2(pad, (headerH - logoSize) / 2f));
        DrawLogo(logoSize);

        ImGui.SameLine(0, 12f * S);
        using (titleFont.Push())
        {
            ImGui.SetCursorPosY((headerH - ImGui.GetTextLineHeight()) / 2f);
            ImGui.TextUnformatted("BetterChat");
        }
        var titleMin = ImGui.GetItemRectMin();
        var titleMax = ImGui.GetItemRectMax();

        var loggedIn = Plugin.CurrentCharacterId != 0;
        ImGui.SameLine(0, 12f * S);
        ImGui.SetCursorPosY((headerH - FrameH) / 2f);
        if (loggedIn)
            Pill(plugin.LocalName, Success, dot: true, centerInFrame: true);
        else
            Pill("Hors ligne", TextDim, dot: true, centerInFrame: true);

        ImGui.SetWindowFontScale(0.95f); // soleil / lune un peu plus petits que le titre

        // Soleil la journée, lune le soir (heure réelle du PC), à la taille du titre
        // et centré verticalement sur lui.
        ImGui.SameLine(0, 10f * S);
        var dayIcon = IsDaytime ? FontAwesomeIcon.Sun : FontAwesomeIcon.Moon;
        var box = titleMax.Y - titleMin.Y;
        var boxMin = new Vector2(ImGui.GetCursorScreenPos().X, (titleMin.Y + titleMax.Y) / 2f - box / 2f);
        ImGui.SetCursorScreenPos(boxMin);
        ImGui.Dummy(new Vector2(box, box));
        if (ImGui.IsItemHovered())
            Tip.Show(IsDaytime ? "Journée (8h00 - 20h00)" : "Soirée (20h00 - 8h00)");
        DrawIconCentered(dayIcon, boxMin, boxMin + new Vector2(box, box), IsDaytime ? SunColor : MoonColor);
        ImGui.SetWindowFontScale(1f);

        var buttonY = (headerH - FrameH) / 2f;
        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH, buttonY));
        if (IconButton("##close", FontAwesomeIcon.Times, "Fermer"))
            IsOpen = false;

        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH * 2 - 6f * S, buttonY));
        if (IconButton("##fold", collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                       collapsed ? "Déplier les réglages (ou double-clic sur l'en-tête)" : "Replier les réglages (ou double-clic sur l'en-tête)"))
            ToggleWindowCollapsed();

        ImGui.SetCursorPos(new Vector2(size.X - pad - FrameH * 3 - 12f * S, buttonY));
        if (IconButton("##chat", Cfg.ChatVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                       Cfg.ChatVisible ? "Revenir au chat du jeu" : "Afficher BetterChat"))
            plugin.ToggleChat();

        // Double-clic sur l'en-tête (hors boutons) : replie ou déplie la fenêtre.
        var origin = ImGui.GetWindowPos();
        var mouse = ImGui.GetMousePos();
        var inHeader = mouse.X >= origin.X && mouse.X < origin.X + size.X &&
                       mouse.Y >= origin.Y && mouse.Y < origin.Y + headerH;
        if (inHeader && ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() &&
            ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            ToggleWindowCollapsed();
    }

    private void DrawSidebar(float headerH, float sidebarW)
    {
        var button = 40f * S;
        var gap = 8f * S;
        var x = (sidebarW - button) / 2f;
        var origin = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();

        // Surlignage de la page active : il glisse jusqu'à la nouvelle page.
        var activeIndex = Array.FindIndex(Pages, p => p.Page == page);
        var slide = Animate(ImGui.GetID("##navHighlight"), activeIndex, 18f);
        var highlightMin = origin + new Vector2(x, headerH + 14f * S + slide * (button + gap));
        var highlightMax = highlightMin + new Vector2(button, button);
        drawList.AddRectFilled(highlightMin, highlightMax, Col(AccentSoft), 10f * S);
        drawList.AddRect(highlightMin, highlightMax, Col(AccentBorder), 10f * S);
        drawList.AddRectFilled(new Vector2(origin.X, highlightMin.Y + 9f * S), new Vector2(origin.X + 3f * S, highlightMax.Y - 9f * S), Col(Accent), 2f * S);

        for (var i = 0; i < Pages.Length; i++)
        {
            var (target, icon, label) = Pages[i];
            ImGui.SetCursorPos(new Vector2(x, headerH + 14f * S + i * (button + gap)));
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(button, button);
            if (ImGui.InvisibleButton($"##nav{i}", new Vector2(button, button)))
                page = target;

            var hovered = ImGui.IsItemHovered();
            var active = page == target;
            if (!active && hovered)
                drawList.AddRectFilled(min, max, Col(HoverOverlay), 10f * S);

            DrawIconCentered(icon, min, max, active ? Accent : hovered ? TextMain : TextDim);
            if (hovered)
                Tip.Show(label);
        }
    }

    private void DrawFooter(Vector2 size, float footerH)
    {
        var pad = 18f * S;
        var status = FooterStatus();
        var statusSize = ImGui.CalcTextSize(status);
        ImGui.SetCursorPos(new Vector2(size.X - pad - statusSize.X, size.Y - footerH + (footerH - statusSize.Y) / 2f));
        ImGui.TextColored(TextDim, status);
    }

    private string FooterStatus()
    {
        if (plugin.OtherChatPluginLoaded)
            return "Un autre plugin de chat est actif : le chat du jeu n'est pas masqué";
        if (Plugin.CurrentCharacterId == 0)
            return "Connecte-toi en jeu pour utiliser le chat";

        return $"{plugin.Messages.Lines.Count} messages en mémoire";
    }

    private void DrawLogo(float size)
    {
        logoTask ??= LoadLogoAsync();
        if (logoTask is { IsCompletedSuccessfully: true, Result: { } logo })
        {
            ImGui.Image(logo.Handle, new Vector2(size, size));
            return;
        }

        // Le temps que le logo charge : pastille avec une icône de bulle.
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size, size);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, Col(Accent), 8f * S);
        DrawIconCentered(FontAwesomeIcon.CommentDots, min, max, OnAccent);
        ImGui.Dummy(new Vector2(size, size));
    }

    private static async Task<IDalamudTextureWrap?> LoadLogoAsync()
    {
        try
        {
            await using var stream = typeof(ConfigWindow).Assembly.GetManifestResourceStream("BetterChat.icon.png");
            if (stream == null)
                return null;
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return await Plugin.TextureProvider.CreateFromImageAsync(memory.ToArray());
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Logo introuvable");
            return null;
        }
    }

    private void DrawPage()
    {
        var pad = 24f * S;
        pageWidth = ImGui.GetContentRegionAvail().X - pad * 2;
        cardInner = pageWidth;

        // Transition : la nouvelle page apparaît en fondu en remontant légèrement.
        var pageBefore = lastPage;
        var appear = PageAppear();
        if (pageBefore != page)
            ImGui.SetScrollY(0);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Max(0.02f, appear) * ImGui.GetStyle().Alpha);

        ImGui.SetCursorPos(new Vector2(pad, pad * 0.8f + (1f - appear) * 16f * S));
        ImGui.BeginGroup();
        switch (page)
        {
            case Page.Home: DrawHomePage(); break;
            case Page.Tabs: DrawTabsPage(); break;
            case Page.Display: DrawDisplayPage(); break;
            case Page.Alerts: DrawAlertsPage(); break;
            case Page.About: DrawAboutPage(); break;
        }
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, pad));
    }

    // =================================================================== Accueil

    private void DrawHomePage()
    {
        var game = plugin.Game;
        var replaced = Cfg.ChatVisible && Cfg.HideNativeChat && !plugin.OtherChatPluginLoaded;
        Hero(replaced ? "Le chat du jeu est remplacé." : "BetterChat est en retrait.",
             replaced ? "Entrée ouvre la saisie, comme d'habitude." : "Le chat du jeu reste affiché.",
             Cfg.ChatVisible ? "Chat affiché" : "Chat masqué",
             GameChat.ChannelLabel(game.CurrentChannel));

        if (plugin.OtherChatPluginLoaded)
        {
            Card(() =>
            {
                IconText(FontAwesomeIcon.ExclamationTriangle, Danger);
                ImGui.SameLine();
                ImGui.TextUnformatted("Un autre plugin de chat est actif");
                TextDimmed("Chat 2 remplace aussi le chat du jeu : les deux se disputeraient la touche Entrée. BetterChat ne masque rien tant qu'il est chargé. Désactive-le dans /xlplugins pour utiliser BetterChat.");
            });
        }

        var changed = false;
        SectionTitle("L'essentiel");
        Card(() =>
        {
            changed |= ToggleRow("Afficher BetterChat", "Raccourci : /bchat. Masqué, le chat du jeu revient.", Cfg.ChatVisible, v =>
            {
                if (v != Cfg.ChatVisible)
                    plugin.ToggleChat();
            });
            changed |= ToggleRow("Masquer le chat du jeu", "Désactivé, les deux chats sont affichés côte à côte.", Cfg.HideNativeChat, v => Cfg.HideNativeChat = v);
            changed |= ToggleRow("Verrouiller la fenêtre", "Empêche de la déplacer ou de la redimensionner par erreur.", Cfg.Locked, v => Cfg.Locked = v);
            var style = (int)Cfg.Style;
            SettingRow("Présentation", "Moderne : le nom au-dessus des messages. Compacte : tout sur une ligne.", 280f * S, () =>
            {
                var chosen = Segmented("##homeStyle", new[]
                {
                    ("Moderne", FontAwesomeIcon.Comments),
                    ("Compacte", FontAwesomeIcon.AlignLeft),
                }, style, 280f * S);
                if (chosen != style)
                {
                    Cfg.Style = (ChatStyle)chosen;
                    changed = true;
                }
            }, FrameH + 20f * S);
        });

        SectionTitle("Pour bien démarrer");
        Card(() =>
        {
            TextDimmed("Entrée ouvre la saisie, Tab change de canal, les flèches rappellent tes derniers messages.");
            TextDimmed("Tape « /p bonjour » ou clique sur la pastille à gauche de la saisie pour choisir où écrire.");
            TextDimmed("Clique sur un nom, un objet ou une carte dans un message. Clic droit sur un message pour le copier.");
            TextDimmed("Ctrl + F cherche dans l'onglet affiché, par exemple : de:Jean canal:CL date:hier.");
            TextDimmed("Clic droit sur un onglet : ses options, le détacher dans une fenêtre, l'exporter dans un fichier.");
        });

        if (changed)
            SaveSoon();
    }

    // =================================================================== Onglets

    private void DrawTabsPage()
    {
        Hero("Onglets", "Choisis ce que chaque onglet affiche.", $"{Cfg.Tabs.Count} onglet(s)");

        editedTab = Math.Clamp(editedTab, 0, Cfg.Tabs.Count - 1);
        var structureChanged = false;

        Card(() =>
        {
            for (var i = 0; i < Cfg.Tabs.Count; i++)
                structureChanged |= DrawTabRow(i);
        });

        if (FancyButton("##newTab", "Ajouter un onglet", FontAwesomeIcon.Plus, ButtonSize("Ajouter un onglet", FontAwesomeIcon.Plus), primary: true))
        {
            Cfg.Tabs.Add(new ChatTabSettings { Name = $"Onglet {Cfg.Tabs.Count + 1}", Kinds = ChatTabPresets.General().Kinds });
            editedTab = Cfg.Tabs.Count - 1;
            structureChanged = true;
        }

        ImGui.SameLine();
        if (FancyButton("##resetTabs", "Onglets par défaut", FontAwesomeIcon.Undo, ButtonSize("Onglets par défaut", FontAwesomeIcon.Undo), primary: false, danger: true) &&
            ImGui.GetIO().KeyCtrl)
        {
            Cfg.Tabs = ChatTabPresets.Default();
            Cfg.ActiveTab = 0;
            editedTab = 0;
            structureChanged = true;
        }
        if (ImGui.IsItemHovered())
            Tip.Show("Ctrl + clic : remplace tous tes onglets par ceux d'origine.");

        ImGui.Dummy(new Vector2(0, 4f * S));

        if (structureChanged)
        {
            SaveSoon();
            plugin.RebuildFeeds();
            editedTab = Math.Clamp(editedTab, 0, Cfg.Tabs.Count - 1);
        }

        DrawTabEditor(Cfg.Tabs[editedTab]);
    }

    private bool DrawTabRow(int index)
    {
        var tab = Cfg.Tabs[index];
        var changed = false;
        var buttons = FrameH * 3 + 8f * S;

        Row(() =>
        {
            var selected = index == editedTab;
            if (ImGui.Selectable($"{tab.Name}##row{index}", selected, ImGuiSelectableFlags.None, new Vector2(cardInner - buttons - 16f * S, FrameH)))
                editedTab = index;
        }, buttons, () =>
        {
            if (IconButton($"##up{index}", FontAwesomeIcon.ChevronUp, "Monter", disabled: index == 0))
            {
                Move(index, index - 1);
                changed = true;
            }

            ImGui.SameLine(0, 4f * S);
            if (IconButton($"##down{index}", FontAwesomeIcon.ChevronDown, "Descendre", disabled: index >= Cfg.Tabs.Count - 1))
            {
                Move(index, index + 1);
                changed = true;
            }

            ImGui.SameLine(0, 4f * S);
            var canDelete = Cfg.Tabs.Count > 1;
            if (IconButton($"##del{index}", FontAwesomeIcon.Trash, canDelete ? "Ctrl + clic : supprimer" : "Il faut garder au moins un onglet", disabled: !canDelete, hoverColor: Danger) &&
                ImGui.GetIO().KeyCtrl)
            {
                Cfg.Tabs.RemoveAt(index);
                changed = true;
            }
        });

        return changed;
    }

    private void Move(int from, int to)
    {
        var tab = Cfg.Tabs[from];
        Cfg.Tabs.RemoveAt(from);
        Cfg.Tabs.Insert(to, tab);
        editedTab = to;
        if (Cfg.ActiveTab == from)
            Cfg.ActiveTab = to;
        else if (Cfg.ActiveTab == to)
            Cfg.ActiveTab = from;
    }

    private void DrawTabEditor(ChatTabSettings tab)
    {
        var changed = false;
        var filtersChanged = false;

        SectionTitle("Réglages de l'onglet", tab.Name);
        Card(() =>
        {
            SettingRow("Nom", null, 260f * S, () =>
            {
                var name = tab.Name;
                ImGui.SetNextItemWidth(260f * S);
                if (ImGui.InputText("##tabName", ref name, 32) && name.Trim().Length > 0)
                {
                    tab.Name = name;
                    changed = true;
                }
            });
            changed |= ToggleRow("Compter les messages non lus", "Une pastille sur l'onglet. Mentions et messages privés restent signalés en rouge.", tab.ShowUnread, v => tab.ShowUnread = v);

            // Le chat garde au moins un onglet : le dernier ne peut pas en sortir.
            using (ImRaii.Disabled(!tab.Detached && !plugin.CanDetachTab))
                ToggleRow("Dans sa propre fenêtre", "L'onglet sort du chat, pour le placer où tu veux : le combat dans un coin de l'écran, par exemple.", tab.Detached, v => plugin.SetDetached(tab, v));

            SettingRow("Canal à l'ouverture", "En ouvrant cet onglet, la saisie passe sur ce canal.", 220f * S, () =>
            {
                ImGui.SetNextItemWidth(220f * S);
                using var combo = ImRaii.Combo("##tabChannel", tab.InputChannel == ChatKind.Other ? "Ne rien changer" : ChatKinds.Info(tab.InputChannel).Label);
                if (!combo.Success)
                    return;

                if (ImGui.Selectable("Ne rien changer", tab.InputChannel == ChatKind.Other))
                {
                    tab.InputChannel = ChatKind.Other;
                    changed = true;
                }

                foreach (var info in ChatKinds.All.Where(i => ChatChannels.Command(i.Kind).Length > 0))
                {
                    if (ImGui.Selectable(info.Label, tab.InputChannel == info.Kind))
                    {
                        tab.InputChannel = info.Kind;
                        changed = true;
                    }
                }
            });
        });

        foreach (var section in Enum.GetValues<ChatSection>())
        {
            var kinds = ChatKinds.All.Where(i => i.Section == section).ToList();
            var enabled = kinds.Count(k => tab.Kinds.Contains(k.Kind));
            SectionTitle(ChatKinds.SectionLabel(section), $"{enabled} / {kinds.Count}");
            Card(() =>
            {
                if (ImGui.SmallButton($"Tout cocher##{section}"))
                {
                    foreach (var kind in kinds.Where(k => !tab.Kinds.Contains(k.Kind)))
                        tab.Kinds.Add(kind.Kind);
                    filtersChanged = true;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"Tout décocher##{section}"))
                {
                    tab.Kinds.RemoveAll(k => kinds.Any(i => i.Kind == k));
                    filtersChanged = true;
                }

                // Deux colonnes : il y a beaucoup de types de messages.
                var column = cardInner / 2f;
                var start = ImGui.GetCursorPosX();
                for (var i = 0; i < kinds.Count; i++)
                {
                    if (i % 2 == 1)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(start + column);
                    }

                    var kind = kinds[i];
                    var on = tab.Kinds.Contains(kind.Kind);
                    if (ImGui.Checkbox($"{kind.Label}##{kind.Kind}", ref on))
                    {
                        if (on)
                            tab.Kinds.Add(kind.Kind);
                        else
                            tab.Kinds.Remove(kind.Kind);
                        filtersChanged = true;
                    }
                }

                if (section == ChatSection.Combat)
                {
                    ImGui.Dummy(new Vector2(0, 2f * S));
                    var battle = (int)tab.Battle;
                    SettingRow("Afficher le combat de", null, 330f * S, () =>
                    {
                        var chosen = Segmented("##battle", new[]
                        {
                            ("Tous", FontAwesomeIcon.Globe),
                            ("Mon groupe", FontAwesomeIcon.Users),
                            ("Moi", FontAwesomeIcon.User),
                        }, battle, 330f * S);
                        if (chosen != battle)
                        {
                            tab.Battle = (BattleFilter)chosen;
                            filtersChanged = true;
                        }
                    }, FrameH + 20f * S);
                }
            });
        }

        if (filtersChanged)
            plugin.FiltersChanged();
        if (changed || filtersChanged)
            SaveSoon();
    }

    // =================================================================== Affichage

    private void DrawDisplayPage()
    {
        Hero("Affichage", "Règle l'apparence de la fenêtre de chat.", Cfg.Style == ChatStyle.Modern ? "Présentation moderne" : "Présentation compacte");

        displaySection = Segmented("##display", new[]
        {
            ("Chat", FontAwesomeIcon.Comments),
            ("Couleurs", FontAwesomeIcon.Palette),
        }, displaySection);

        var changed = false;
        switch (displaySection)
        {
            case 0:
                Card(() =>
                {
                    changed |= ToggleRow("Afficher l'heure", null, Cfg.ShowTimestamps, v => Cfg.ShowTimestamps = v);
                    changed |= ToggleRow("Afficher le canal", "Une petite étiquette (Groupe, Compagnie libre, LS1...) à côté du nom.", Cfg.ShowChannelTags, v => Cfg.ShowChannelTags = v);
                    changed |= ToggleRow("Contour sombre autour du texte", "Garde le texte lisible même sans fond. Les icônes reçoivent un fond discret.", Cfg.TextOutline, v => Cfg.TextOutline = v);
                    using (ImRaii.Disabled(!Cfg.TextOutline))
                        changed |= SliderRow("Intensité du contour", null, Cfg.OutlineStrength, 0.1f, 1f, "%.2f", v => Cfg.OutlineStrength = v);
                    changed |= SliderRow("Taille du texte", null, Cfg.FontSize, 10f, 36f, "%.0f", v => Cfg.FontSize = MathF.Round(v));
                    changed |= ToggleRow("Icônes de job", "À côté du nom des membres du groupe et des joueurs à proximité.", Cfg.ShowJobIcons, v => Cfg.ShowJobIcons = v);
                    using (ImRaii.Disabled(!Cfg.ShowJobIcons))
                        changed |= SliderRow("Taille des icônes de job", "Par rapport à la hauteur du texte.", Cfg.JobIconPercent, 50f, 110f, "%.0f %%", v => Cfg.JobIconPercent = MathF.Round(v));
                    changed |= ToggleRow("Séparateurs", "Une ligne « Nouveaux messages » là où tu t'étais arrêté, une à chaque reconnexion et une quand la date change.", Cfg.ShowSeparators, v => Cfg.ShowSeparators = v);
                });

                SectionTitle("Saisie");
                Card(() =>
                {
                    changed |= ToggleRow("Garder le focus après l'envoi", "Pour enchaîner les messages. Désactivé : le jeu reprend le clavier, comme d'habitude.", Cfg.KeepFocusAfterSend, v => Cfg.KeepFocusAfterSend = v);
                    changed |= ToggleRow("Proposer les commandes", "En tapant « / » : commandes du jeu et des plugins, complétées avec Tab.", Cfg.CommandSuggestions, v => Cfg.CommandSuggestions = v);
                    changed |= ToggleRow("Aperçu avant l'envoi", "Montre ce que donneront <t>, <flag> et les autres raccourcis du jeu.", Cfg.ShowMacroPreview, v => Cfg.ShowMacroPreview = v);
                });

                SectionTitle("Fenêtre");
                Card(() =>
                {
                    changed |= SliderRow("Opacité du fond", "0 = fond complètement retiré.", Cfg.WindowOpacity, 0f, 1f, "%.2f", v => Cfg.WindowOpacity = v);
                });
                break;

            default:
                Card(() =>
                {
                    CardLabel("Interface");
                    changed |= ColorRow("Couleur d'accent", "Onglet actif, mentions, réglages.", Cfg.AccentColor, v => Cfg.AccentColor = v);
                    changed |= ColorRow("Texte secondaire", "L'heure des messages.", Cfg.SecondaryColor, v => Cfg.SecondaryColor = v);
                    changed |= ToggleRow("Fond animé des réglages", "Halos lumineux et particules derrière cette fenêtre.", Cfg.AnimatedSettingsBackground, v => Cfg.AnimatedSettingsBackground = v);
                });

                Card(() =>
                {
                    CardLabel("Canaux");
                    changed |= ToggleRow("Reprendre les couleurs du jeu", "Celles de Configuration des personnages > Chat.", Cfg.UseGameColors, v => Cfg.UseGameColors = v);
                    TextDimmed("Une couleur choisie ici passe avant celle du jeu.");
                    foreach (var kind in new[]
                             {
                                 ChatKind.Say, ChatKind.Party, ChatKind.Alliance, ChatKind.FreeCompany, ChatKind.TellIncoming,
                                 ChatKind.Linkshell1, ChatKind.CrossLinkshell1, ChatKind.Yell, ChatKind.Shout,
                             })
                    {
                        var current = plugin.Colors.Of(kind);
                        changed |= ColorRow(ChatKinds.Info(kind).Label, null, current, v => Cfg.ChannelColors[kind] = v);
                    }
                });

                if (FancyButton("##resetColors", "Couleurs par défaut", FontAwesomeIcon.Undo, ButtonSize("Couleurs par défaut", FontAwesomeIcon.Undo), primary: false))
                {
                    var defaults = new Configuration();
                    Cfg.AccentColor = defaults.AccentColor;
                    Cfg.SecondaryColor = defaults.SecondaryColor;
                    Cfg.UseGameColors = defaults.UseGameColors;
                    Cfg.ChannelColors.Clear();
                    changed = true;
                }
                break;
        }

        if (changed)
        {
            plugin.Colors.Invalidate();
            SaveSoon();
        }
    }

    // =================================================================== Messages et alertes

    private void DrawAlertsPage()
    {
        Hero("Messages et alertes", "Ce qui attire ton attention, et ce qui est gardé.");

        var changed = false;
        var mentionsChanged = false;

        SectionTitle("Mentions");
        Card(() =>
        {
            if (ToggleRow("Me signaler quand on me cite", "Le message est surligné quand il contient ton nom ou ton prénom.", Cfg.MentionOwnName, v => Cfg.MentionOwnName = v))
                mentionsChanged = true;

            ImGui.TextUnformatted("Mots à surveiller");
            TextDimmed("Le nom de ta compagnie, d'un raid... Seuls les mots entiers comptent.");

            string? removed = null;
            foreach (var word in Cfg.MentionWords)
            {
                Row(() => ImGui.TextUnformatted(word), FrameH, () =>
                {
                    if (IconButton($"##rmWord{word}", FontAwesomeIcon.Times, "Retirer", hoverColor: Danger))
                        removed = word;
                });
            }

            if (removed != null)
            {
                Cfg.MentionWords.Remove(removed);
                mentionsChanged = true;
            }

            ImGui.SetNextItemWidth(260f * S);
            var entered = ImGui.InputTextWithHint("##newWord", "Ajouter un mot...", ref newWord, 40, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Ajouter") || entered) && newWord.Trim().Length > 0)
            {
                Cfg.MentionWords.Add(newWord.Trim());
                newWord = string.Empty;
                mentionsChanged = true;
            }
        });

        SectionTitle("Sons");
        Card(() =>
        {
            changed |= ToggleRow("Son à la réception d'un message privé", null, Cfg.SoundOnTell, v => Cfg.SoundOnTell = v);
            changed |= ToggleRow("Son quand on me cite", null, Cfg.SoundOnMention, v => Cfg.SoundOnMention = v);
            changed |= ToggleRow("Clignoter dans la barre des tâches", "À la réception d'un message privé, quand tu es sur une autre fenêtre.", Cfg.FlashOnTell, v => Cfg.FlashOnTell = v);
            changed |= SliderIntRow("Son utilisé", "Les sons du jeu, de <se.1> à <se.16>.", Cfg.AlertSound, 1, 16, v => Cfg.AlertSound = v);
            if (ImGui.Button("Écouter"))
                GameChat.PlayAlert(Cfg.AlertSound);
        });

        SectionTitle("Anti-spam", plugin.SpamHidden > 0 ? $"{plugin.SpamHidden} masqué(s)" : null);
        Card(() =>
        {
            if (ToggleRow("Regrouper les messages répétés", "Le même message du même joueur devient une seule ligne, avec « x3 ».", Cfg.CollapseRepeats, v => Cfg.CollapseRepeats = v))
            {
                plugin.FiltersChanged();
                changed = true;
            }

            ImGui.TextUnformatted("Masquer les messages qui contiennent");
            TextDimmed("Un mot ou une expression : « gils pas chers », « wts »... Sans tenir compte des majuscules ni des accents.");

            string? removed = null;
            foreach (var word in Cfg.SpamWords)
            {
                Row(() => ImGui.TextUnformatted(word), FrameH, () =>
                {
                    if (IconButton($"##rmSpam{word}", FontAwesomeIcon.Times, "Retirer", hoverColor: Danger))
                        removed = word;
                });
            }

            if (removed != null)
            {
                Cfg.SpamWords.Remove(removed);
                plugin.SpamRulesChanged();
                changed = true;
            }

            ImGui.SetNextItemWidth(260f * S);
            var entered = ImGui.InputTextWithHint("##newSpam", "Ajouter un mot ou une expression...", ref newSpamWord, 80, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Ajouter##spam") || entered) && newSpamWord.Trim().Length > 0)
            {
                Cfg.SpamWords.Add(newSpamWord.Trim());
                newSpamWord = string.Empty;
                plugin.SpamRulesChanged();
                changed = true;
            }
        });

        SectionTitle("Couleur par joueur", $"{Cfg.PlayerColors.Count}");
        Card(() =>
        {
            TextDimmed("Pour repérer tes amis ou ton groupe statique. Clique aussi sur un nom dans le chat pour lui choisir une couleur.");

            string? removed = null;
            foreach (var (name, color) in Cfg.PlayerColors.ToList())
            {
                var edited = new Vector3(color.X, color.Y, color.Z);
                Row(() => ImGui.TextColored(color with { W = 1f }, name), FrameH * 2f + 4f * S, () =>
                {
                    if (ImGui.ColorEdit3($"##playerColor{name}", ref edited, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                        plugin.SetPlayerColor(name, new Vector4(edited, 1f), save: false);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        changed = true;

                    ImGui.SameLine(0, 4f * S);
                    if (IconButton($"##rmPlayerColor{name}", FontAwesomeIcon.Times, "Retirer", hoverColor: Danger))
                        removed = name;
                });
            }

            if (removed != null)
                plugin.RemovePlayerColor(removed);

            ImGui.SetNextItemWidth(260f * S);
            var entered = ImGui.InputTextWithHint("##newPlayerColor", "Nom du joueur...", ref newColorPlayer, 40, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Ajouter##playerColor") || entered) && newColorPlayer.Trim().Length > 0)
            {
                plugin.SetPlayerColor(newColorPlayer.Trim(), Cfg.AccentColor with { W = 1f });
                newColorPlayer = string.Empty;
            }
        });

        SectionTitle("Joueurs masqués", $"{Cfg.MutedPlayers.Count}");
        Card(() =>
        {
            if (Cfg.MutedPlayers.Count == 0)
            {
                TextDimmed("Personne. Clique sur le nom d'un joueur dans le chat pour masquer ses messages (toi seul ne les vois plus).");
                return;
            }

            string? unmuted = null;
            foreach (var name in Cfg.MutedPlayers)
            {
                Row(() => ImGui.TextUnformatted(name), FrameH, () =>
                {
                    if (IconButton($"##unmute{name}", FontAwesomeIcon.Times, "Afficher à nouveau ses messages"))
                        unmuted = name;
                });
            }

            if (unmuted != null)
                plugin.UnmutePlayer(unmuted);
        });

        SectionTitle("Historique");
        Card(() =>
        {
            changed |= ToggleRow("Garder les messages entre deux sessions", "Sur ton PC uniquement, un fichier par personnage. Le combat n'est jamais enregistré.", Cfg.KeepHistory, v => Cfg.KeepHistory = v);
            using (ImRaii.Disabled(!Cfg.KeepHistory))
                changed |= SliderIntRow("Messages relus au lancement", null, Cfg.HistoryLines, 100, 5000, v => Cfg.HistoryLines = v);
            changed |= SliderIntRow("Messages gardés en mémoire", "Au-delà, les plus anciens sont retirés (le combat en premier).", Cfg.MemoryLines, 1000, 30000, v =>
            {
                Cfg.MemoryLines = v;
                plugin.Messages.Capacity = v;
            });

            if (FancyButton("##clearHistory", "Effacer tous les messages", FontAwesomeIcon.Trash, ButtonSize("Effacer tous les messages", FontAwesomeIcon.Trash), primary: false, danger: true) &&
                ImGui.GetIO().KeyCtrl)
                plugin.ClearMessages();
            if (ImGui.IsItemHovered())
                Tip.Show("Ctrl + clic : efface l'affichage et le fichier de ce personnage.");
        });

        if (mentionsChanged)
        {
            plugin.RefreshMentions();
            changed = true;
        }

        if (changed)
            SaveSoon();
    }

    // =================================================================== À propos

    private void DrawAboutPage()
    {
        var version = Plugin.PluginInterface.Manifest.AssemblyVersion.ToString();
        Hero("À propos", "Un chat moderne à la place du chat du jeu.", $"Version {version}", "par Powerl3ss");

        Card(() =>
        {
            var start = ImGui.GetCursorPos();
            DrawLogo(64f * S);
            ImGui.SameLine(0, 16f * S);
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(start.Y + 6f * S);
            using (heroFont.Push())
                ImGui.TextUnformatted("BetterChat");
            TextDimmed("Tes messages partent par le chat du jeu, sur le même canal, avec les mêmes commandes. Le plugin ne change que ce que tu vois, et n'envoie rien sans que tu appuies sur Entrée.");
            ImGui.EndGroup();
        });

        Card(() =>
        {
            CardLabel("Commandes");
            CommandRow("/bchat", "Affiche ou masque le chat (masqué, celui du jeu revient)");
            CommandRow("/bchat config", "Ouvre ces réglages");
            CommandRow("/bchat debug", "Affiche les compteurs de diagnostic dans le chat");
        });

        Card(() =>
        {
            CardLabel("Diagnostic");
            var game = plugin.Game;
            TextDimmed($"Ouvertures du chat vues : {game.Activations}  |  messages en mémoire : {plugin.Messages.Lines.Count}");
            TextDimmed("Si Entrée n'ouvre plus la saisie alors que ce compteur reste à zéro, le jeu a sans doute changé : clique dans la zone de saisie, et signale-le sur GitHub.");
        });

        if (FancyButton("##repo", "Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt, ButtonSize("Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt), primary: false))
            Util.OpenLink(RepoUrl);
    }

    private void CommandRow(string command, string description)
    {
        Row(() =>
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Accent))
                ImGui.TextUnformatted(command);
            ImGui.SameLine(0, 16f * S);
            ImGui.TextColored(TextDim, description);
        }, 0f, null);
    }
}
