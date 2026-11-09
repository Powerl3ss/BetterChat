using System;
using System.Collections.Generic;
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
    private enum Page { Home, Tabs, Context, Display, Alerts, Players, History, About }

    // La navigation, rangée par thème : chaque groupe a son petit titre dans la barre latérale.
    private static readonly (string Group, (Page Page, FontAwesomeIcon Icon, string Label)[] Items)[] Navigation =
    {
        ("Général", new[]
        {
            (Page.Home, FontAwesomeIcon.Home, "Accueil"),
            (Page.Tabs, FontAwesomeIcon.Columns, "Onglets"),
            (Page.Context, FontAwesomeIcon.MapMarkedAlt, "Contexte"),
        }),
        ("Apparence", new[]
        {
            (Page.Display, FontAwesomeIcon.Palette, "Affichage"),
        }),
        ("Messages", new[]
        {
            (Page.Alerts, FontAwesomeIcon.Bell, "Alertes"),
            (Page.Players, FontAwesomeIcon.Users, "Joueurs"),
            (Page.History, FontAwesomeIcon.ShieldAlt, "Anti-spam"),
        }),
        ("Plus", new[]
        {
            (Page.About, FontAwesomeIcon.InfoCircle, "À propos"),
        }),
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
        Size = new Vector2(1000, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedConstraints;

        // Vraies polices de grande taille (au lieu d'agrandir le texte, ce qui le rend flou).
        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        // La police par défaut est dessinée à des tailles fixes : toute autre taille est étirée et devient floue.
        // 14 et 18 points (soit 18,67 et 24 pixels) sont des tailles exactes.
        titleFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(14f * 4f / 3f)));
        heroFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(24f)));
        // 36 points (48 pixels) : les grands chiffres de l'accueil.
        statFont = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(36f * 4f / 3f)));
    }

    private readonly IFontHandle titleFont;
    private readonly IFontHandle heroFont;
    private readonly IFontHandle statFont;

    // Tailles en pixels "non mis à l'échelle" (Dalamud applique l'échelle de l'interface).
    private const float HeaderHeight = 44f;
    private static readonly WindowSizeConstraints ExpandedConstraints = new()
    {
        MinimumSize = new Vector2(860, 600),
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
        statFont.Dispose();
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
                            .Push(ImGuiStyleVar.WindowRounding, 22f * S)
                            .Push(ImGuiStyleVar.WindowBorderSize, 1f);
        windowColors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg with { W = Math.Clamp(Cfg.SettingsOpacity, 0f, 1f) })
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
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f * S)
                                .Push(ImGuiStyleVar.GrabRounding, 10f * S)
                                .Push(ImGuiStyleVar.PopupRounding, 10f * S)
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
                                 .Push(ImGuiCol.PopupBg, new Vector4(0.110f, 0.106f, 0.129f, 0.99f))
                                 .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                                 .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f))
                                 .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.20f))
                                 .Push(ImGuiCol.ScrollbarGrabActive, AccentBorder)
                                 .Push(ImGuiCol.Separator, Line);

        var size = ImGui.GetWindowSize();
        lastSize = size;
        var origin = ImGui.GetWindowPos();
        var margin = PanelGap;
        var sidebarW = 216f * S;
        var drawList = ImGui.GetWindowDrawList();

        DrawAnimatedBackground(origin, size);

        // La barre latérale est un panneau à part, posé sur le fond comme les autres.
        var sidebarMin = origin + new Vector2(margin, margin);
        var sidebarMax = origin + new Vector2(margin + sidebarW, size.Y - margin);
        drawList.AddRectFilled(sidebarMin, sidebarMax, Col(CardBg), PanelRounding);
        drawList.AddRect(sidebarMin, sidebarMax, Col(Line), PanelRounding);
        DrawSidebar(sidebarMin, sidebarMax);

        // Les pages défilent entre les mêmes marges que la barre latérale : leurs panneaux s'alignent sur elle en haut
        // et en bas.
        var pageX = margin + sidebarW;
        ImGui.SetCursorPos(new Vector2(pageX, margin));
        var noPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using (var child = ImRaii.Child("##page", new Vector2(size.X - pageX, size.Y - margin * 2f), false))
        {
            noPadding.Dispose();
            if (child.Success)
                DrawPage();
        }
    }

    private void DrawSidebar(Vector2 min, Vector2 max)
    {
        var pad = 14f * S;
        var origin = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();
        var width = max.X - min.X - pad * 2;
        var x = min.X - origin.X + pad;
        var y = min.Y - origin.Y + pad + 4f * S;

        // Le logo, le nom, et la croix pour fermer.
        var logoSize = 30f * S;
        ImGui.SetCursorPos(new Vector2(x, y));
        DrawLogo(logoSize);
        using (titleFont.Push())
        {
            ImGui.SetCursorPos(new Vector2(x + logoSize + 10f * S, y + MathF.Round((logoSize - ImGui.GetTextLineHeight()) / 2f)));
            ImGui.TextUnformatted("BetterChat");
        }

        ImGui.SetCursorPos(new Vector2(x + width - FrameH, y + (logoSize - FrameH) / 2f));
        if (IconButton("##close", FontAwesomeIcon.Times))
            IsOpen = false;

        y += logoSize + 22f * S;

        // Les pages, par groupe. Le surlignage de la page active glisse jusqu'à la nouvelle.
        var itemH = 33f * S;
        var activeY = y;
        var positions = new List<(Page Page, FontAwesomeIcon Icon, string Label, float Y)>();
        foreach (var (group, items) in Navigation)
        {
            ImGui.SetCursorPos(new Vector2(x + 8f * S, y));
            ImGui.TextColored(TextFaint, group.ToUpperInvariant());
            y += ImGui.GetTextLineHeight() + 4f * S;

            foreach (var (target, icon, label) in items)
            {
                positions.Add((target, icon, label, y));
                if (target == page)
                    activeY = y;
                y += itemH + 1f * S;
            }

            y += 8f * S;
        }

        var slide = Animate(ImGui.GetID("##navHighlight"), activeY, 18f);
        var highlightMin = origin + new Vector2(x, slide);
        drawList.AddRectFilled(highlightMin, highlightMin + new Vector2(width, itemH), Col(InputBg), 11f * S);

        foreach (var (target, icon, label, itemY) in positions)
        {
            ImGui.SetCursorPos(new Vector2(x, itemY));
            var itemMin = ImGui.GetCursorScreenPos();
            var itemMax = itemMin + new Vector2(width, itemH);
            if (ImGui.InvisibleButton($"##nav{target}", itemMax - itemMin))
                page = target;

            var hovered = ImGui.IsItemHovered();
            var active = page == target;
            if (!active && hovered)
                drawList.AddRectFilled(itemMin, itemMax, Col(HoverOverlay), 11f * S);

            var iconBox = new Vector2(itemH, itemH);
            DrawIconCentered(icon, itemMin + new Vector2(4f * S, 0f), itemMin + new Vector2(4f * S, 0f) + iconBox, active ? Accent : hovered ? TextMain : TextDim);
            var textY = MathF.Round(itemMin.Y + (itemH - ImGui.GetTextLineHeight()) / 2f);
            drawList.AddText(new Vector2(itemMin.X + itemH + 4f * S, textY), Col(active || hovered ? TextMain : TextDim), label);
        }

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
        var pad = PanelGap;
        // La barre de défilement passe dans la marge de droite.
        pageWidth = ImGui.GetContentRegionAvail().X - pad - ImGui.GetStyle().ScrollbarSize;
        cardInner = pageWidth;

        // Transition : la nouvelle page apparaît en fondu en remontant légèrement.
        var pageBefore = lastPage;
        var appear = PageAppear();
        if (pageBefore != page)
            ImGui.SetScrollY(0);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Max(0.02f, appear) * ImGui.GetStyle().Alpha);

        ImGui.SetCursorPos(new Vector2(pad, (1f - appear) * 16f * S));
        ImGui.BeginGroup();
        switch (page)
        {
            case Page.Home: DrawHomePage(); break;
            case Page.Tabs: DrawTabsPage(); break;
            case Page.Context: DrawContextPage(); break;
            case Page.Display: DrawDisplayPage(); break;
            case Page.Alerts: DrawAlertsPage(); break;
            case Page.Players: DrawPlayersPage(); break;
            case Page.History: DrawHistoryPage(); break;
            case Page.About: DrawAboutPage(); break;
        }
        // La page s'arrête au bas de son dernier élément, sans marge en plus.
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, ImGui.GetItemRectMax().Y));
        ImGui.EndGroup();
        DrawConfirmPopup();
    }


    // =================================================================== Confirmation

    // Les actions qui effacent quelque chose passent par un petit menu : un clic de plus, mais jamais par erreur.
    private string confirmLabel = string.Empty;
    private FontAwesomeIcon confirmIcon;
    private Action? confirmAction;

    private void AskConfirm(string label, FontAwesomeIcon icon, Action action)
    {
        confirmLabel = label;
        confirmIcon = icon;
        confirmAction = action;
        ImGui.OpenPopup("##confirm");
    }

    private void DrawConfirmPopup()
    {
        if (!Tip.BeginMenu("##confirm"))
            return;

        if (Tip.MenuItem(confirmLabel, confirmIcon, danger: true))
        {
            confirmAction?.Invoke();
            confirmAction = null;
        }

        if (Tip.MenuItem("Annuler", FontAwesomeIcon.Times))
            confirmAction = null;

        Tip.EndMenu();
    }

    /// <summary>En bas d'une page : remet ses réglages d'origine, après confirmation.</summary>
    private void ResetPageButton(string id, Action reset)
    {
        ImGui.Dummy(new Vector2(0, 4f * S));
        const string label = "Réinitialiser cette page";
        if (FancyButton(id, label, FontAwesomeIcon.Undo, ButtonSize(label, FontAwesomeIcon.Undo), primary: false, danger: true))
        {
            AskConfirm("Remettre les réglages d'origine", FontAwesomeIcon.Undo, () =>
            {
                reset();
                plugin.Colors.Invalidate();
                SaveSoon();
            });
        }
    }

    // =================================================================== Accueil

    private void DrawHomePage()
    {
        if (plugin.OtherChatPluginLoaded)
        {
            SectionTitle("Un autre plugin de chat est actif", FontAwesomeIcon.ExclamationTriangle);
            Card(() => TextDimmed("Chat 2 remplace aussi le chat du jeu : les deux se disputeraient la touche Entrée. BetterChat ne masque rien tant qu'il est chargé. Désactive-le dans /xlplugins pour utiliser BetterChat."));
        }

        var activity = Activity();
        DrawActivityPanel(activity);
        DrawStatCards(activity);

        var changed = false;
        CardRow("##homeBottom", 2, (i, width) =>
        {
            if (i == 0)
            {
                SectionTitle("L'essentiel", FontAwesomeIcon.Star);
                RowCard(width, () =>
                {
                    changed |= ToggleRow("Afficher BetterChat", "Raccourci : /bchat. Masqué, le chat du jeu revient.", Cfg.ChatVisible, v =>
                    {
                        if (v != Cfg.ChatVisible)
                            plugin.ToggleChat();
                    });
                    changed |= ToggleRow("Masquer le chat du jeu", "Désactivé, les deux chats sont affichés côte à côte.", Cfg.HideNativeChat, v => Cfg.HideNativeChat = v);
                    changed |= ToggleRow("Fond animé des réglages", "Halos lumineux et particules derrière cette fenêtre.", Cfg.AnimatedSettingsBackground, v => Cfg.AnimatedSettingsBackground = v);

                    // Sous son titre, sur toute la largeur : le panneau est trop étroit pour les mettre côte à côte.
                    SettingRow("Opacité du fond des réglages", "Derrière les panneaux. 0 % : le jeu se voit entièrement.", 0f, null);
                    var opacity = Cfg.SettingsOpacity * 100f;
                    ImGui.SetNextItemWidth(cardInner);
                    if (ImGui.SliderFloat("##settingsOpacity", ref opacity, 0f, 100f, "%.0f %%"))
                    {
                        Cfg.SettingsOpacity = Math.Clamp(MathF.Round(opacity) / 100f, 0f, 1f);
                        changed = true;
                    }

                    ImGui.Dummy(new Vector2(0f, 4f * S));
                    SettingRow("Présentation", "Moderne : le nom au-dessus des messages. Compacte : tout sur une ligne.", 0f, null);
                    var style = (int)Cfg.Style;
                    var chosen = Segmented("##homeStyle", new[]
                    {
                        ("Moderne", FontAwesomeIcon.Comments),
                        ("Compacte", FontAwesomeIcon.AlignLeft),
                    }, style, cardInner);
                    if (chosen != style)
                    {
                        Cfg.Style = (ChatStyle)chosen;
                        changed = true;
                    }
                });
            }
            else
            {
                SectionTitle("Pour bien démarrer", FontAwesomeIcon.Lightbulb);
                RowCard(width, () =>
                {
                    HintLine("Entrée ouvre la saisie. Les flèches haut et bas rappellent les derniers messages.");
                    HintLine("Changer de canal : la pastille à gauche de la saisie, « /p bonjour », ou Tab dans une saisie vide.");
                    HintLine("Autotraduction : le bouton à droite de la saisie, ou le début d'un mot puis Tab.");
                    HintLine("Clic droit sur un message : répondre, copier, épingler.");
                    HintLine("Les messages privés se rangent par personne, une pastille par conversation.");
                    HintLine("La loupe cherche dans l'onglet affiché : de:Jean canal:CL date:hier.");
                    HintLine("Clic droit sur un onglet : le détacher dans sa propre fenêtre, l'exporter.");
                }, glow: true);
            }
        });

        if (changed)
            SaveSoon();
    }

    /// <summary>Une astuce : un point de la couleur d'accent, puis le texte.</summary>
    private void HintLine(string text)
    {
        var start = ImGui.GetCursorScreenPos();
        var dotY = start.Y + ImGui.GetTextLineHeight() / 2f;
        ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(start.X + 3f * S, dotY), 3f * S, Col(Accent));
        ImGui.SetCursorScreenPos(start + new Vector2(14f * S, 0f));
        using (ImRaii.PushColor(ImGuiCol.Text, TextDim))
            ImGui.TextWrapped(text);
        ImGui.Dummy(new Vector2(0f, 2f * S));
    }

    // L'activité est recalculée toutes les quelques secondes : elle parcourt tous les messages en mémoire.
    private ChatActivity? activity;
    private double activityAt = -10;

    private ChatActivity Activity()
    {
        var now = ImGui.GetTime();
        if (activity == null || now - activityAt > 5)
        {
            activity = ChatActivity.Compute(plugin.Messages.Lines, DateTime.Now);
            activityAt = now;
        }

        return activity;
    }

    private static readonly string[] ShortDays = { "Dim", "Lun", "Mar", "Mer", "Jeu", "Ven", "Sam" };

    /// <summary>Les messages des joueurs sur sept jours, par tranche de deux heures : plus c'est clair, plus le chat a parlé.</summary>
    private void DrawActivityPanel(ChatActivity data)
    {
        SectionTitle("Activité de la semaine", FontAwesomeIcon.ChartBar, "7 derniers jours");
        Card(() =>
        {
            // Légende.
            var levels = new[] { ("Calme", 1), ("Moyen", 2), ("Animé", 3), ("Très animé", 4) };
            foreach (var (label, level) in levels)
            {
                var p = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddCircleFilled(p + new Vector2(4f * S, ImGui.GetTextLineHeight() / 2f), 4f * S, Col(LevelColor(level)));
                ImGui.SetCursorScreenPos(p + new Vector2(13f * S, 0f));
                ImGui.TextColored(TextDim, label);
                ImGui.SameLine(0, 16f * S);
            }

            ImGui.NewLine();
            ImGui.Dummy(new Vector2(0, 4f * S));

            var drawList = ImGui.GetWindowDrawList();
            var labelW = TextWidth("Mer") + 14f * S;
            var gap = 5f * S;
            var cellW = (cardInner - labelW - gap * (ChatActivity.Slots - 1)) / ChatActivity.Slots;
            var cellH = MathF.Round(MathF.Min(cellW * 0.7f, 26f * S));
            var origin = ImGui.GetCursorScreenPos();
            var lineH = ImGui.GetTextLineHeight();

            for (var day = 0; day < ChatActivity.Days; day++)
            {
                var y = origin.Y + day * (cellH + gap);
                var date = data.FirstDay.AddDays(day);
                var isToday = day == ChatActivity.Days - 1;
                drawList.AddText(new Vector2(origin.X, MathF.Round(y + (cellH - lineH) / 2f)), Col(isToday ? TextMain : TextDim), ShortDays[(int)date.DayOfWeek]);

                for (var slot = 0; slot < ChatActivity.Slots; slot++)
                {
                    var min = new Vector2(origin.X + labelW + slot * (cellW + gap), y);
                    drawList.AddRectFilled(min, min + new Vector2(cellW, cellH), Col(LevelColor(data.Level(day, slot))), 6f * S);
                }
            }

            // Les heures sous la grille.
            var hoursY = origin.Y + ChatActivity.Days * (cellH + gap);
            for (var slot = 0; slot < ChatActivity.Slots; slot += 2)
            {
                var text = $"{slot * ChatActivity.HoursPerSlot}h";
                var x = origin.X + labelW + slot * (cellW + gap) + (cellW - TextWidth(text)) / 2f;
                drawList.AddText(new Vector2(MathF.Round(x), hoursY), Col(TextFaint), text);
            }

            ImGui.Dummy(new Vector2(cardInner, hoursY + lineH - origin.Y));
        });
    }

    /// <summary>De la case vide (0) à la couleur d'accent pleine (4).</summary>
    private Vector4 LevelColor(int level) => level switch
    {
        0 => new Vector4(1f, 1f, 1f, 0.04f),
        1 => Accent with { W = 0.22f },
        2 => Accent with { W = 0.42f },
        3 => Accent with { W = 0.68f },
        _ => Vector4.Lerp(Accent, White, 0.25f) with { W = 1f },
    };

    /// <summary>Quatre chiffres du jour, deux par rangée.</summary>
    private void DrawStatCards(ChatActivity data)
    {
        var unread = plugin.Feeds.Sum(f => f.Unread);
        var unreadTabs = plugin.Feeds.Count(f => f.Unread > 0);
        var conversations = plugin.Conversations.All;
        var unreadTells = conversations.Sum(c => c.Unread);

        string? change = null;
        var changeColor = Accent;
        if (data.ChangeFromYesterday is { } percent)
        {
            change = $"{(percent >= 0 ? "+" : "-")}{MathF.Abs(percent):0} %";
            changeColor = percent >= 0 ? Accent : Danger;
        }

        CardRow("##stats1", 2, (i, width) =>
        {
            if (i == 0)
                StatCard(width, "##statMessages", FontAwesomeIcon.CommentDots, "Messages du jour", data.Today.ToString(),
                         change, changeColor, change != null ? "par rapport à hier" : "aucun message hier",
                         FontAwesomeIcon.ArrowRight, () => page = Page.History, glow: false);
            else
                StatCard(width, "##statMentions", FontAwesomeIcon.At, "Mentions", data.MentionsToday.ToString(),
                         null, Accent, data.MentionsToday == 0 ? "personne ne t'a cité aujourd'hui" : "aujourd'hui",
                         FontAwesomeIcon.ArrowRight, () => page = Page.Alerts, glow: false);
        });

        CardRow("##stats2", 2, (i, width) =>
        {
            if (i == 0)
                StatCard(width, "##statUnread", FontAwesomeIcon.Bell, "Non lus", unread.ToString(),
                         unreadTabs > 0 ? $"{unreadTabs} onglet(s)" : null, Accent, unreadTabs > 0 ? "à rattraper" : "tout est lu",
                         FontAwesomeIcon.CheckDouble, () =>
                         {
                             foreach (var feed in plugin.Feeds)
                                 feed.MarkRead();
                         }, glow: false);
            else
                StatCard(width, "##statTells", FontAwesomeIcon.Envelope, "Conversations", conversations.Count.ToString(),
                         unreadTells > 0 ? $"{unreadTells} non lu(s)" : null, Accent, unreadTells > 0 ? "en messages privés" : "messages privés à jour",
                         FontAwesomeIcon.CheckDouble, () =>
                         {
                             foreach (var conversation in conversations)
                                 plugin.Conversations.MarkRead(conversation.Key);
                         }, glow: true);
        });
    }

    /// <summary>
    /// Un grand chiffre : pastille d'icône et titre, un bouton rond à droite, le chiffre, puis une ligne
    /// dont le début est coloré (une évolution, un compte).
    /// </summary>
    private void StatCard(float width, string id, FontAwesomeIcon icon, string label, string value, string? highlight,
                          Vector4 highlightColor, string caption, FontAwesomeIcon actionIcon, Action action, bool glow)
    {
        RowCard(width, () =>
        {
            var start = ImGui.GetCursorPos();
            var badge = BadgeSize;
            IconBadge(icon, badge);
            ImGui.SetCursorPos(new Vector2(start.X + badge + 10f * S, start.Y + MathF.Round((badge - ImGui.GetTextLineHeight()) / 2f)));
            ImGui.TextUnformatted(label);

            ImGui.SetCursorPos(new Vector2(start.X + cardInner - badge, start.Y));
            if (CircleButton(id, actionIcon, badge))
                action();

            ImGui.SetCursorPos(new Vector2(start.X, start.Y + badge + 14f * S));
            using (statFont.Push())
                ImGui.TextUnformatted(value);

            ImGui.Dummy(new Vector2(0f, 2f * S));
            if (highlight != null)
            {
                ImGui.TextColored(highlightColor, highlight);
                ImGui.SameLine(0, 6f * S);
            }

            ImGui.TextColored(TextDim, caption);
        }, glow);
    }

    // =================================================================== Onglets

    private readonly HashSet<ChatSection> openSections = new();

    private void DrawTabsPage()
    {
        editedTab = Math.Clamp(editedTab, 0, Cfg.Tabs.Count - 1);
        var structureChanged = false;

        SectionTitle("Liste des onglets", FontAwesomeIcon.Columns, "Clic : modifier");
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
        if (FancyButton("##resetTabs", "Onglets par défaut", FontAwesomeIcon.Undo, ButtonSize("Onglets par défaut", FontAwesomeIcon.Undo), primary: false, danger: true))
        {
            AskConfirm("Remplacer tous les onglets", FontAwesomeIcon.Undo, () =>
            {
                Cfg.Tabs = ChatTabPresets.Default();
                Cfg.ActiveTab = 0;
                editedTab = 0;
                TabsStructureChanged();
            });
        }

        ImGui.Dummy(new Vector2(0, 4f * S));

        if (structureChanged)
            TabsStructureChanged();

        DrawTabEditor(Cfg.Tabs[editedTab]);
    }

    private void TabsStructureChanged()
    {
        SaveSoon();
        plugin.RebuildFeeds();
        editedTab = Math.Clamp(editedTab, 0, Cfg.Tabs.Count - 1);
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
            if (IconButton($"##up{index}", FontAwesomeIcon.ChevronUp, disabled: index == 0))
            {
                Move(index, index - 1);
                changed = true;
            }

            ImGui.SameLine(0, 4f * S);
            if (IconButton($"##down{index}", FontAwesomeIcon.ChevronDown, disabled: index >= Cfg.Tabs.Count - 1))
            {
                Move(index, index + 1);
                changed = true;
            }

            ImGui.SameLine(0, 4f * S);
            if (IconButton($"##del{index}", FontAwesomeIcon.Trash, disabled: Cfg.Tabs.Count <= 1, hoverColor: Danger))
            {
                AskConfirm($"Supprimer « {tab.Name} »", FontAwesomeIcon.Trash, () =>
                {
                    if (Cfg.Tabs.Count <= 1 || !Cfg.Tabs.Remove(tab))
                        return;

                    Cfg.ActiveTab = Math.Clamp(Cfg.ActiveTab, 0, Cfg.Tabs.Count - 1);
                    TabsStructureChanged();
                });
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

        SectionTitle("Réglages de l'onglet", FontAwesomeIcon.SlidersH, tab.Name);
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

            SettingRow("Canal à l'ouverture", "En ouvrant cet onglet, ou en cliquant dans la saisie de sa fenêtre détachée, la saisie passe sur ce canal.", 220f * S, () =>
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

        SectionTitle("Messages affichés", FontAwesomeIcon.Filter, $"{tab.Kinds.Count} type(s)");
        Card(() =>
        {
        var first = true;
        foreach (var section in Enum.GetValues<ChatSection>())
        {
            var kinds = ChatKinds.All.Where(i => i.Section == section).ToList();
            var enabled = kinds.Count(k => tab.Kinds.Contains(k.Kind));
            var open = openSections.Contains(section);

            var nowOpen = FoldingRow($"##section{section}", ChatKinds.SectionLabel(section), $"{enabled} / {kinds.Count}", open, !first, () =>
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

            if (nowOpen != open)
            {
                if (nowOpen)
                    openSections.Add(section);
                else
                    openSections.Remove(section);
            }

            first = false;
        }
        });

        if (filtersChanged)
            plugin.FiltersChanged();
        if (changed || filtersChanged)
            SaveSoon();
    }

    // =================================================================== Selon le contexte

    /// <summary>Changer d'onglet ou masquer des canaux selon l'endroit : donjon, raid, ville.</summary>
    private void DrawContextPage()
    {
        SectionTitle("Comment ça marche", FontAwesomeIcon.Lightbulb);
        Card(() => TextDimmed("En entrant dans un donjon, un raid ou une ville, le chat peut changer d'onglet et masquer des canaux. En sortant, l'onglet d'avant revient et les canaux réapparaissent."));

        foreach (var context in ChatContexts.Configurable)
        {
            var rule = Cfg.RuleFor(context)!;
            SectionTitle(ChatContexts.Label(context), ContextIcon(context), rule.TabId.Length == 0 && rule.Hidden.Count == 0 ? "Rien ne change" : "Actif");
            Card(() =>
            {
                SettingRow("Onglet à afficher", null, 220f * S, () =>
                {
                    var current = Cfg.Tabs.Find(t => t.Id == rule.TabId && !t.Detached);
                    ImGui.SetNextItemWidth(220f * S);
                    using var combo = ImRaii.Combo($"##contextTab{context}", current?.Name ?? "Ne rien changer");
                    if (!combo.Success)
                        return;

                    if (ImGui.Selectable("Ne rien changer", current == null))
                    {
                        rule.TabId = string.Empty;
                        SaveSoon();
                    }

                    foreach (var tab in Cfg.Tabs.Where(t => !t.Detached))
                    {
                        if (ImGui.Selectable($"{tab.Name}##{tab.Id}", tab == current))
                        {
                            rule.TabId = tab.Id;
                            SaveSoon();
                        }
                    }
                });

                ImGui.TextUnformatted("Masquer");
                var column = cardInner / 2f;
                var start = ImGui.GetCursorPosX();
                var sections = Enum.GetValues<ChatSection>();
                for (var i = 0; i < sections.Length; i++)
                {
                    if (i % 2 == 1)
                    {
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(start + column);
                    }

                    var section = sections[i];
                    var hidden = rule.Hidden.Contains(section);
                    if (ImGui.Checkbox($"{ChatKinds.SectionLabel(section)}##{context}{section}", ref hidden))
                    {
                        if (hidden)
                            rule.Hidden.Add(section);
                        else
                            rule.Hidden.Remove(section);
                        plugin.ContextRulesChanged();
                        SaveSoon();
                    }
                }
            });
        }

        ResetPageButton("##resetContext", () =>
        {
            foreach (var context in ChatContexts.Configurable)
            {
                var rule = Cfg.RuleFor(context)!;
                rule.TabId = string.Empty;
                rule.Hidden.Clear();
            }

            plugin.ContextRulesChanged();
        });
    }

    private static FontAwesomeIcon ContextIcon(ChatContext context) => context switch
    {
        ChatContext.Dungeon => FontAwesomeIcon.Dungeon,
        ChatContext.Raid => FontAwesomeIcon.Dragon,
        ChatContext.Town => FontAwesomeIcon.City,
        _ => FontAwesomeIcon.MapMarkerAlt,
    };

    // =================================================================== Affichage

    private void DrawDisplayPage()
    {
        DrawChatPreview();

        displaySection = Segmented("##display", new[]
        {
            ("Chat", FontAwesomeIcon.Comments),
            ("Couleurs", FontAwesomeIcon.Palette),
        }, displaySection);

        var changed = false;
        switch (displaySection)
        {
            case 0:
                SectionTitle("Messages", FontAwesomeIcon.Comments);
                Card(() =>
                {
                    changed |= ToggleRow("Afficher l'heure", null, Cfg.ShowTimestamps, v => Cfg.ShowTimestamps = v);
                    changed |= ToggleRow("Afficher le canal", "Une petite étiquette (Groupe, Compagnie libre, LS1...) à côté du nom.", Cfg.ShowChannelTags, v => Cfg.ShowChannelTags = v);
                    changed |= ToggleRow("Contour sombre autour du texte", "Garde le texte lisible même sans fond. Les icônes reçoivent un fond discret.", Cfg.TextOutline, v => Cfg.TextOutline = v);
                    using (ImRaii.Disabled(!Cfg.TextOutline))
                        changed |= SliderRow("Intensité du contour", null, Cfg.OutlineStrength, 0.1f, 1f, "%.2f", v => Cfg.OutlineStrength = v);
                    changed |= SliderRow("Taille du texte", null, Cfg.FontSize, 10f, 36f, "%.0f", v => Cfg.FontSize = MathF.Round(v));
                    changed |= SliderIntRow("Écart entre les messages", "En pixels. Les messages d'un même joueur, à la suite, restent collés.", Cfg.MessageSpacing, 0, 20, v => Cfg.MessageSpacing = v);
                    var nameIcon = (int)Cfg.NameIcon;
                    SettingRow("Icône à côté du nom", NameIconDescription(Cfg.NameIcon), 400f * S, () =>
                    {
                        var chosen = Segmented("##nameIcon", new[]
                        {
                            ("Aucune", FontAwesomeIcon.Ban),
                            ("Job", FontAwesomeIcon.IdBadge),
                            ("Rôle", FontAwesomeIcon.ShieldAlt),
                            ("Statut", FontAwesomeIcon.Circle),
                        }, nameIcon, 400f * S);
                        if (chosen != nameIcon)
                        {
                            Cfg.NameIcon = (NameIconKind)chosen;
                            changed = true;
                        }
                    }, FrameH + 20f * S);
                    using (ImRaii.Disabled(Cfg.NameIcon == NameIconKind.None))
                        changed |= SliderRow("Taille de l'icône", "Par rapport à la hauteur du texte.", Cfg.JobIconHeight, 60f, 150f, "%.0f %%", v => Cfg.JobIconHeight = MathF.Round(v));
                    changed |= ToggleRow("Séparateurs", "Une ligne quand la date change.", Cfg.ShowSeparators, v => Cfg.ShowSeparators = v);
                });

                SectionTitle("Saisie", FontAwesomeIcon.Keyboard);
                Card(() =>
                {
                    changed |= ToggleRow("Garder le focus après l'envoi", "Pour enchaîner les messages. Désactivé : le jeu reprend le clavier, comme d'habitude.", Cfg.KeepFocusAfterSend, v => Cfg.KeepFocusAfterSend = v);
                    changed |= ToggleRow("Proposer les commandes", "En tapant « / » : commandes du jeu et des plugins, complétées avec Tab.", Cfg.CommandSuggestions, v => Cfg.CommandSuggestions = v);
                    changed |= ToggleRow("Aperçu avant l'envoi", "Montre ce que donneront <t>, <flag> et les autres raccourcis du jeu.", Cfg.ShowMacroPreview, v => Cfg.ShowMacroPreview = v);
                });

                SectionTitle("Fenêtre", FontAwesomeIcon.WindowMaximize);
                Card(() =>
                {
                    changed |= SliderRow("Opacité du fond", "0 = fond complètement retiré.", Cfg.WindowOpacity, 0f, 1f, "%.2f", v => Cfg.WindowOpacity = v);
                });

                SectionTitle("Objets", FontAwesomeIcon.Coins);
                Card(() =>
                {
                    changed |= ToggleRow("Prix du marché au survol",
                                         "Le prix le plus bas sur ton monde et ton centre de données, demandé au site Universalis. Désactivé, le plugin ne contacte aucun site.",
                                         Cfg.MarketPrices, v => Cfg.MarketPrices = v);
                });
                break;

            default:
                SectionTitle("Interface", FontAwesomeIcon.Desktop);
                Card(() =>
                {
                    changed |= ColorRow("Couleur d'accent", "Onglet actif, mentions, réglages.", Cfg.AccentColor, v => Cfg.AccentColor = v);
                    changed |= ColorRow("Texte secondaire", "L'heure des messages, les séparateurs de date, les compteurs (« x3 », messages épinglés) et la catégorie des autotraductions.", Cfg.SecondaryColor, v => Cfg.SecondaryColor = v);
                });

                SectionTitle("Canaux", FontAwesomeIcon.Comments);
                Card(() =>
                {
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
                break;
        }

        ResetPageButton("##resetDisplay", () =>
        {
            var d = new Configuration();
            Cfg.ShowTimestamps = d.ShowTimestamps;
            Cfg.ShowChannelTags = d.ShowChannelTags;
            Cfg.TextOutline = d.TextOutline;
            Cfg.OutlineStrength = d.OutlineStrength;
            Cfg.FontSize = d.FontSize;
            Cfg.MessageSpacing = d.MessageSpacing;
            Cfg.NameIcon = d.NameIcon;
            Cfg.JobIconHeight = d.JobIconHeight;
            Cfg.ShowSeparators = d.ShowSeparators;
            Cfg.KeepFocusAfterSend = d.KeepFocusAfterSend;
            Cfg.CommandSuggestions = d.CommandSuggestions;
            Cfg.ShowMacroPreview = d.ShowMacroPreview;
            Cfg.WindowOpacity = d.WindowOpacity;
            Cfg.MarketPrices = d.MarketPrices;
            Cfg.AccentColor = d.AccentColor;
            Cfg.SecondaryColor = d.SecondaryColor;
            Cfg.UseGameColors = d.UseGameColors;
            Cfg.ChannelColors.Clear();
        });

        if (changed)
        {
            plugin.Colors.Invalidate();
            SaveSoon();
        }
    }

    private static string NameIconDescription(NameIconKind kind) => kind switch
    {
        NameIconKind.Job => "Le job des membres du groupe et des joueurs à proximité, comme sur leur plaque.",
        NameIconKind.Role => "Tank, soigneur ou DPS (mêlée, distance, magie), pour le groupe et les joueurs à proximité.",
        NameIconKind.Status => "Mentor, nouvel aventurier, absent... Seulement pour toi et les joueurs à proximité.",
        _ => "Rien à côté des noms.",
    };

    // =================================================================== Alertes

    private void DrawAlertsPage()
    {
        var changed = false;
        var mentionsChanged = false;

        SectionTitle("Mentions", FontAwesomeIcon.At);
        Card(() =>
        {
            if (ToggleRow("Me signaler quand on me cite", "Le message est surligné quand il contient ton nom ou ton prénom.", Cfg.MentionOwnName, v => Cfg.MentionOwnName = v))
                mentionsChanged = true;

            ImGui.TextUnformatted("Mots à surveiller");
            TextDimmed("Le nom de ta compagnie, d'un raid... Seuls les mots entiers comptent.");
            if (WordList("Word", Cfg.MentionWords, ref newWord, "Ajouter un mot...", 40))
                mentionsChanged = true;
        });

        SectionTitle("Sons", FontAwesomeIcon.VolumeUp);
        Card(() =>
        {
            changed |= ToggleRow("Son à la réception d'un message privé", null, Cfg.SoundOnTell, v => Cfg.SoundOnTell = v);
            changed |= ToggleRow("Son quand on me cite", null, Cfg.SoundOnMention, v => Cfg.SoundOnMention = v);
            changed |= ToggleRow("Clignoter dans la barre des tâches", "À la réception d'un message privé, quand tu es sur une autre fenêtre.", Cfg.FlashOnTell, v => Cfg.FlashOnTell = v);

            var sliderW = 190f * S;
            SettingRow("Son utilisé", "Les sons du jeu, de <se.1> à <se.16>.", sliderW + 4f * S + FrameH, () =>
            {
                var sound = Cfg.AlertSound;
                ImGui.SetNextItemWidth(sliderW);
                if (ImGui.SliderInt("##alertSound", ref sound, 1, 16))
                {
                    Cfg.AlertSound = sound;
                    changed = true;
                }

                // Le son se fait entendre une fois le curseur relâché, puis d'un clic sur le haut-parleur.
                if (ImGui.IsItemDeactivatedAfterEdit())
                    GameChat.PlayAlert(Cfg.AlertSound);

                ImGui.SameLine(0, 4f * S);
                if (IconButton("##playSound", FontAwesomeIcon.VolumeUp))
                    GameChat.PlayAlert(Cfg.AlertSound);
            });
        });

        ResetPageButton("##resetAlerts", () =>
        {
            var d = new Configuration();
            Cfg.MentionOwnName = d.MentionOwnName;
            Cfg.SoundOnTell = d.SoundOnTell;
            Cfg.SoundOnMention = d.SoundOnMention;
            Cfg.FlashOnTell = d.FlashOnTell;
            Cfg.AlertSound = d.AlertSound;
            plugin.RefreshMentions();
        });

        if (mentionsChanged)
        {
            plugin.RefreshMentions();
            changed = true;
        }

        if (changed)
            SaveSoon();
    }

    /// <summary>Une liste de mots avec une croix pour en retirer un, puis un champ pour en ajouter. Renvoie vrai si elle a changé.</summary>
    private bool WordList(string id, List<string> words, ref string draft, string hint, int maxLength)
    {
        var changed = false;
        string? removed = null;
        foreach (var word in words)
        {
            Row(() => ImGui.TextUnformatted(word), FrameH, () =>
            {
                if (IconButton($"##rm{id}{word}", FontAwesomeIcon.Times, hoverColor: Danger))
                    removed = word;
            });
        }

        if (removed != null)
        {
            words.Remove(removed);
            changed = true;
        }

        ImGui.SetNextItemWidth(260f * S);
        var entered = ImGui.InputTextWithHint($"##new{id}", hint, ref draft, maxLength, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button($"Ajouter##{id}") || entered) && draft.Trim().Length > 0)
        {
            words.Add(draft.Trim());
            draft = string.Empty;
            changed = true;
        }

        return changed;
    }

    // =================================================================== Joueurs

    private void DrawPlayersPage()
    {
        var changed = false;

        SectionTitle("Couleur par joueur", FontAwesomeIcon.Palette);
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
                    if (IconButton($"##rmPlayerColor{name}", FontAwesomeIcon.Times, hoverColor: Danger))
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

        SectionTitle("Joueurs masqués", FontAwesomeIcon.UserSlash);
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
                    if (IconButton($"##unmute{name}", FontAwesomeIcon.Times))
                        unmuted = name;
                });
            }

            if (unmuted != null)
                plugin.UnmutePlayer(unmuted);
        });

        if (changed)
            SaveSoon();
    }

    // =================================================================== Anti-spam et historique

    private void DrawHistoryPage()
    {
        var changed = false;

        SectionTitle("Anti-spam", FontAwesomeIcon.ShieldAlt);
        Card(() =>
        {
            if (ToggleRow("Regrouper les messages répétés", "Le même message du même joueur devient une seule ligne, avec « x3 ».", Cfg.CollapseRepeats, v => Cfg.CollapseRepeats = v))
            {
                plugin.FiltersChanged();
                changed = true;
            }

            ImGui.TextUnformatted("Masquer les messages qui contiennent");
            TextDimmed("Un mot ou une expression : « gils pas chers », « wts »... Sans tenir compte des majuscules ni des accents.");
            if (WordList("Spam", Cfg.SpamWords, ref newSpamWord, "Ajouter un mot ou une expression...", 80))
            {
                plugin.SpamRulesChanged();
                changed = true;
            }
        });

        SectionTitle("Historique", FontAwesomeIcon.History);
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

            const string clear = "Effacer tous les messages";
            if (FancyButton("##clearHistory", clear, FontAwesomeIcon.Trash, ButtonSize(clear, FontAwesomeIcon.Trash), primary: false, danger: true))
                AskConfirm("Tout effacer, fichier compris", FontAwesomeIcon.Trash, plugin.ClearMessages);
        });

        ResetPageButton("##resetHistory", () =>
        {
            var d = new Configuration();
            Cfg.CollapseRepeats = d.CollapseRepeats;
            Cfg.KeepHistory = d.KeepHistory;
            Cfg.HistoryLines = d.HistoryLines;
            Cfg.MemoryLines = d.MemoryLines;
            plugin.Messages.Capacity = d.MemoryLines;
            plugin.FiltersChanged();
        });

        if (changed)
            SaveSoon();
    }

    // =================================================================== À propos

    private void DrawAboutPage()
    {
        var version = Plugin.PluginInterface.Manifest.AssemblyVersion.ToString();
        Card(() =>
        {
            var start = ImGui.GetCursorPos();
            DrawLogo(64f * S);
            ImGui.SameLine(0, 16f * S);
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(start.Y + 6f * S);
            using (heroFont.Push())
                ImGui.TextUnformatted("BetterChat");
            ImGui.SameLine(0, 10f * S);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2f * S);
            Chip($"Version {version}");
            TextDimmed("Tes messages partent par le chat du jeu, sur le même canal, avec les mêmes commandes. Le plugin ne change que ce que tu vois, et n'envoie rien sans que tu appuies sur Entrée.");
            ImGui.Dummy(new Vector2(0f, 2f * S));
            ImGui.TextUnformatted("Créé par Powerl3ss.");
            TextDimmed("Claude, l'IA d'Anthropic, a grandement participé à son développement.");
            ImGui.EndGroup();
        });

        SectionTitle("Commandes", FontAwesomeIcon.Terminal);
        Card(() =>
        {
            CommandRow("/bchat", "Affiche ou masque le chat (masqué, celui du jeu revient)");
            CommandRow("/bchat config", "Ouvre ces réglages");
            CommandRow("/bchat debug", "Affiche les compteurs de diagnostic dans le chat");
        });

        SectionTitle("Diagnostic", FontAwesomeIcon.Stethoscope);
        Card(() =>
        {
            var game = plugin.Game;
            TextDimmed($"Ouvertures du chat vues : {game.Activations}  |  messages en mémoire : {plugin.Messages.Lines.Count}");
            TextDimmed("Si Entrée n'ouvre plus la saisie alors que ce compteur reste à zéro, le jeu a sans doute changé : clique dans la zone de saisie, et signale-le sur GitHub.");
        });

        if (FancyButton("##repo", "Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt, ButtonSize("Ouvrir la page GitHub", FontAwesomeIcon.ExternalLinkAlt), primary: false))
            Util.OpenLink(RepoUrl);
    }

    /// <summary>Une commande et ce qu'elle fait, sur une ligne simple : les commandes se suivent de près.</summary>
    private void CommandRow(string command, string description)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(16f * S, 4f * S)))
        {
            ImGui.TextColored(Accent, command);
            ImGui.SameLine();
            ImGui.TextColored(TextDim, description);
        }
    }
}
