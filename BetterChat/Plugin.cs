using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using BetterChat.Chat;
using BetterChat.Core;
using BetterChat.Windows;

namespace BetterChat;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/bchat";
    private const string LongCommandName = "/betterchat";
    private const string ChatPrefix = "[BetterChat] ";

    // Les messages en attente partent sur le disque toutes les quelques secondes, sans bloquer le jeu.
    private static readonly TimeSpan HistoryFlushDelay = TimeSpan.FromSeconds(4);

    // Les commandes des autres plugins peuvent changer : la liste proposée est relue de temps en temps.
    private static readonly TimeSpan CommandsRefreshDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PluginCheckDelay = TimeSpan.FromSeconds(5);

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("BetterChat");

    public ChatLog Messages { get; } = new();
    public GameChat Game { get; }
    public ChannelColors Colors { get; }
    public ChatHistoryStore History { get; }
    public RecentTells RecentTells { get; } = new();
    public CommandSuggester Commands { get; } = new();
    internal GameIconAtlas Icons { get; } = new();

    /// <summary>Les messages déjà envoyés, rappelés avec les flèches depuis n'importe quelle zone de saisie.</summary>
    public InputHistory SentMessages { get; } = new();

    public List<ChatFeed> Feeds { get; } = new();

    private readonly SpamFilter spam = new();
    private readonly Dictionary<string, DetachedTabWindow> detachedWindows = new();
    private readonly Dictionary<string, Vector4> playerColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileDialogManager fileDialog = new();
    private readonly MentionMatcher mentions = new();
    private readonly HashSet<ChatKind> wantedKinds = new();
    private readonly ChatWindow chatWindow;
    private readonly ConfigWindow configWindow;

    // Une barre est posée à la prochaine connexion. Null : au tout premier passage, on ne sait pas encore si on est en jeu.
    private bool? sessionStartDue;
    private bool detachedWindowsDirty = true;
    private int filterVersion;
    private ulong loadedCharacter;
    private string mentionedName = string.Empty;
    private DateTime nextHistoryFlush = DateTime.MinValue;
    private DateTime nextCommandsRefresh = DateTime.MinValue;
    private DateTime nextPluginCheck = DateTime.MinValue;
    private bool otherChatPlugin;

    /// <summary>Identifiant du personnage connecté (0 si personne).</summary>
    public static ulong CurrentCharacterId => ClientState.IsLoggedIn && PlayerState.IsLoaded ? PlayerState.ContentId : 0;

    public string LocalName => PlayerState.IsLoaded && !string.IsNullOrEmpty(PlayerState.CharacterName) ? PlayerState.CharacterName : string.Empty;

    public ChatFeed ActiveFeed => Feeds[Math.Clamp(Configuration.ActiveTab, 0, Feeds.Count - 1)];

    /// <summary>Messages masqués par l'anti-spam depuis le lancement du jeu.</summary>
    public int SpamHidden { get; private set; }

    /// <summary>Le chat garde toujours au moins un onglet.</summary>
    public bool CanDetachTab => Configuration.Tabs.Count(t => !t.Detached) > 1;

    /// <summary>Un autre plugin de chat chargé en même temps se disputerait la fenêtre du jeu et la touche Entrée.</summary>
    public bool OtherChatPluginLoaded
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now >= nextPluginCheck)
            {
                nextPluginCheck = now + PluginCheckDelay;
                otherChatPlugin = PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "ChatTwo");
            }

            return otherChatPlugin;
        }
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Infobulles : même style dans les trois plugins, avec la couleur d'accent choisie.
        BetterChat.Windows.Tip.AccentProvider = () => Configuration.AccentColor;
        if (Configuration.Tabs.Count == 0)
            Configuration.Tabs = ChatTabPresets.Default();

        Game = new GameChat();
        Colors = new ChannelColors(Configuration);
        History = new ChatHistoryStore(
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "history"),
            (ex, what) => Log.Error(ex, what));

        Messages.Capacity = Configuration.MemoryLines;
        spam.SetRules(Configuration.SpamWords);
        foreach (var (name, color) in Configuration.PlayerColors)
            playerColors[name] = color;

        RebuildFeeds();
        RefreshMentions();

        chatWindow = new ChatWindow(this) { IsOpen = Configuration.ChatVisible };
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(chatWindow);
        WindowSystem.AddWindow(configWindow);

        Game.Activated += chatWindow.OnGameActivated;
        Game.CanTakeActivation = () => chatWindow.IsShownNow && !ChatWindow.HiddenByGame;
        Game.BlockActivation = () => ChatWindow.HiddenByGame;

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Affiche/masque le chat. « /bchat config » : réglages.",
        };
        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(LongCommandName, new CommandInfo(OnCommand) { HelpMessage = "Comme /bchat.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleChat;
        ChatGui.ChatMessageUnhandled += OnChatMessage;
        ClientState.Logout += OnLogout;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessageUnhandled -= OnChatMessage;
        ClientState.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleChat;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LongCommandName);

        Game.Activated -= chatWindow.OnGameActivated;
        Game.CanTakeActivation = null;
        Game.BlockActivation = null;
        Game.Dispose(); // rend le chat du jeu

        WindowSystem.RemoveAllWindows();
        chatWindow.Dispose();
        configWindow.Dispose();

        History.Flush();
    }

    private void DrawUi()
    {
        WindowSystem.Draw();
        fileDialog.Draw();
    }

    // ---------------------------------------------------------------- Commande

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                ToggleConfigUi();
                break;
            case "debug":
                Print(DiagnosticLine());
                break;
            default:
                ToggleChat();
                break;
        }
    }

    // ------------------------------------------------------- Événements du jeu

    private void OnLogout(int type, int code) => sessionStartDue = true;

    private void OnChatMessage(IChatMessage message)
    {
        var line = MessageConverter.Convert(message);
        if (!wantedKinds.Contains(line.Kind))
            return;

        // Tes propres messages n'ont pas de lien sur ton nom, et il peut être précédé de ton numéro de groupe.
        var isOwn = LocalName.Length > 0 && line.AuthorKey.EndsWith(LocalName, StringComparison.Ordinal);
        line.AuthorJob = JobOfAuthor(line, isOwn);

        // Un message masqué reste en mémoire (retirer la règle le fait réapparaître), mais il n'alerte jamais.
        if (spam.Matches(line))
        {
            SpamHidden++;
        }
        else
        {
            if (!isOwn && ChatKinds.Info(line.Kind).HasAuthor && mentions.Matches(line.BodyText))
                line.Mentioned = true;

            if (ChatKinds.IsTell(line.Kind) && line.Author != null && !isOwn)
                RecentTells.Note(line.Author);

            if (!IsMuted(line))
                PlayAlertFor(line);
        }

        Messages.Add(line);

        // Pendant le chargement de l'historique, les messages sont écrits une fois celui-ci fusionné.
        if (Configuration.KeepHistory && loadedCharacter != 0 && pendingHistory == null && !ChatKinds.IsBattle(line.Kind))
            History.Enqueue(line);
    }

    /// <summary>Le job de l'auteur, s'il est dans le groupe ou assez près pour que le jeu le connaisse.</summary>
    private static uint JobOfAuthor(ChatLine line, bool isOwn)
    {
        if (!ChatKinds.Info(line.Kind).HasAuthor)
            return 0;

        if (isOwn && line.Kind != ChatKind.TellOutgoing)
            return PlayerState.ClassJob.RowId;

        var name = line.Author?.Text;
        if (string.IsNullOrEmpty(name))
            return 0;

        foreach (var member in PartyList)
        {
            if (member.Name.TextValue == name)
                return member.ClassJob.RowId;
        }

        foreach (var player in ObjectTable.PlayerObjects)
        {
            if (player.Name.TextValue == name)
                return player.ClassJob.RowId;
        }

        return 0;
    }

    /// <summary>L'historique est lu : il passe avant les messages arrivés entre-temps, qui sont alors écrits à leur tour.</summary>
    private void MergeHistory()
    {
        var task = pendingHistory!;
        pendingHistory = null;
        if (pendingHistoryCharacter != loadedCharacter)
            return; // changé de personnage pendant la lecture

        List<ChatLine> loaded;
        try
        {
            loaded = task.Result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lecture de l'historique du chat impossible");
            loaded = new List<ChatLine>();
        }

        var current = Messages.Lines.ToList();
        Messages.Clear();
        foreach (var line in loaded)
        {
            if (wantedKinds.Contains(line.Kind) || line.Kind == ChatKind.SessionStart)
                Messages.Add(line);
        }

        foreach (var line in current)
        {
            Messages.Add(line);
            if (Configuration.KeepHistory && !ChatKinds.IsBattle(line.Kind))
                History.Enqueue(line);
        }
    }

    private void PlayAlertFor(ChatLine line)
    {
        if ((line.Kind == ChatKind.TellIncoming && Configuration.SoundOnTell) ||
            (line.Mentioned && Configuration.SoundOnMention))
            GameChat.PlayAlert(Configuration.AlertSound);

        if (line.Kind == ChatKind.TellIncoming && Configuration.FlashOnTell)
            WindowAlert.FlashIfInBackground();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Game.Update(Configuration.HideNativeChat && Configuration.ChatVisible && !OtherChatPluginLoaded);

        var character = CurrentCharacterId;
        if (character != 0 && character != loadedCharacter)
            LoadCharacter(character);
        AddSessionStartIfDue(character);

        if (pendingHistory is { IsCompleted: true })
            MergeHistory();

        if (detachedWindowsDirty)
            SyncDetachedWindows();

        UpdateFeeds();

        var now = DateTime.UtcNow;
        if (now >= nextHistoryFlush)
        {
            nextHistoryFlush = now + HistoryFlushDelay;
            History.FlushInBackground();
        }
    }

    // Historique en cours de lecture (en arrière-plan), et le personnage auquel il appartient.
    private Task<List<ChatLine>>? pendingHistory;
    private ulong pendingHistoryCharacter;

    /// <summary>
    /// Un autre personnage : son historique remplace le précédent, sans perdre les messages déjà arrivés.
    /// Le fichier est lu en arrière-plan (le jeu ne se fige pas), puis fusionné dans MergeHistory.
    /// </summary>
    private void LoadCharacter(ulong character)
    {
        // Messages reçus avant que le personnage soit connu : gardés, ils ne sont dans aucun historique.
        var early = loadedCharacter == 0 ? Messages.Lines.ToList() : new List<ChatLine>();

        loadedCharacter = character;
        History.SelectCharacter(character);
        RecentTells.Clear();
        Messages.Clear();
        foreach (var line in early)
            Messages.Add(line);

        if (Configuration.KeepHistory)
        {
            pendingHistory = History.LoadInBackground(Configuration.HistoryLines);
            pendingHistoryCharacter = character;
        }
        else
        {
            pendingHistory = null;
        }

        var name = LocalName;
        if (name != mentionedName)
            RefreshMentions();
    }

    /// <summary>
    /// La barre « Nouvelle connexion », au moment où le personnage arrive en jeu. Rien si le plugin démarre alors
    /// qu'on joue déjà (mise à jour, rechargement) : ce n'est pas une connexion.
    /// </summary>
    private void AddSessionStartIfDue(ulong character)
    {
        sessionStartDue ??= character == 0;
        if (character == 0 || sessionStartDue != true)
            return;

        sessionStartDue = false;
        var line = ChatLine.SessionStart(DateTime.UtcNow);
        Messages.Add(line);

        // Pendant le chargement de l'historique, elle est écrite une fois celui-ci fusionné, comme les messages.
        if (Configuration.KeepHistory && loadedCharacter != 0 && pendingHistory == null)
            History.Enqueue(line);
    }

    private void UpdateFeeds()
    {
        var active = ActiveFeed;
        foreach (var feed in Feeds)
        {
            // Un onglet détaché est toujours à l'écran : il n'a pas de messages « non lus ».
            feed.IsActive = ReferenceEquals(feed, active) || feed.Settings.Detached;
            feed.CollapseRepeats = Configuration.CollapseRepeats;
            feed.Update(Messages, filterVersion, IsHidden);
        }
    }

    // ------------------------------------------------------------ Réglages et actions

    /// <summary>À appeler quand les onglets changent (ajout, suppression, filtres).</summary>
    public void RebuildFeeds()
    {
        Feeds.Clear();
        foreach (var tab in Configuration.Tabs)
            Feeds.Add(new ChatFeed(tab));

        wantedKinds.Clear();
        foreach (var kind in Configuration.Tabs.SelectMany(t => t.Kinds))
            wantedKinds.Add(kind);

        Configuration.ActiveTab = Math.Clamp(Configuration.ActiveTab, 0, Feeds.Count - 1);
        KeepActiveTabAttached();
        detachedWindowsDirty = true;
        filterVersion++;
        UpdateFeeds();
    }

    /// <summary>À appeler quand les filtres d'un onglet ou la liste des joueurs masqués changent.</summary>
    public void FiltersChanged()
    {
        wantedKinds.Clear();
        foreach (var kind in Configuration.Tabs.SelectMany(t => t.Kinds))
            wantedKinds.Add(kind);

        filterVersion++;
    }

    public void RefreshMentions()
    {
        var words = new List<string>(Configuration.MentionWords);
        mentionedName = LocalName;
        if (Configuration.MentionOwnName && mentionedName.Length > 0)
        {
            words.Add(mentionedName);
            words.Add(mentionedName.Split(' ')[0]); // le prénom seul, comme on s'appelle en jeu
        }

        mentions.SetWords(words);
    }

    public ChatFeed? FeedFor(ChatTabSettings tab) => Feeds.Find(f => ReferenceEquals(f.Settings, tab));

    /// <summary>Sort un onglet dans sa propre fenêtre, ou le remet dans le chat.</summary>
    public void SetDetached(ChatTabSettings tab, bool detached)
    {
        if (tab.Detached == detached || (detached && !CanDetachTab))
            return;

        tab.Detached = detached;
        if (detached)
            tab.Locked = false; // une fenêtre qui vient de sortir doit pouvoir être placée

        FeedFor(tab)?.SetSearch(string.Empty); // la recherche du chat ne le concerne plus
        KeepActiveTabAttached();
        detachedWindowsDirty = true;
        Configuration.Save();
    }

    /// <summary>L'onglet affiché dans le chat ne peut pas être détaché, et il en faut toujours un.</summary>
    private void KeepActiveTabAttached()
    {
        var tabs = Configuration.Tabs;
        if (tabs.All(t => t.Detached))
            tabs[0].Detached = false;

        if (tabs[Configuration.ActiveTab].Detached)
            Configuration.ActiveTab = tabs.FindIndex(t => !t.Detached);
    }

    /// <summary>Ajoute et retire les fenêtres, hors du dessin : on ne touche pas à la liste pendant qu'elle est parcourue.</summary>
    private void SyncDetachedWindows()
    {
        detachedWindowsDirty = false;

        foreach (var (id, window) in detachedWindows.ToList())
        {
            if (!window.Tab.Detached || !Configuration.Tabs.Contains(window.Tab))
            {
                WindowSystem.RemoveWindow(window);
                detachedWindows.Remove(id);
            }
        }

        foreach (var tab in Configuration.Tabs)
        {
            if (!tab.Detached || detachedWindows.ContainsKey(tab.Id))
                continue;

            var window = new DetachedTabWindow(this, chatWindow, tab) { IsOpen = true };
            detachedWindows[tab.Id] = window;
            WindowSystem.AddWindow(window);
        }
    }

    public void SelectTab(int index)
    {
        if (index < 0 || index >= Feeds.Count || Feeds[index].Settings.Detached)
            return;

        Configuration.ActiveTab = index;
        Configuration.Save();

        var feed = Feeds[index];
        feed.MarkRead();

        var channel = feed.Settings.InputChannel;
        if (channel != ChatKind.Other)
            Game.SwitchChannel(channel);
    }

    public bool IsMuted(ChatLine line) =>
        line.Author != null && Configuration.MutedPlayers.Count > 0 &&
        Configuration.MutedPlayers.Contains(line.Author.Text, StringComparer.OrdinalIgnoreCase);

    private bool IsHidden(ChatLine line) => IsMuted(line) || spam.Matches(line);

    /// <summary>À appeler quand la liste de l'anti-spam change : les messages déjà reçus suivent la nouvelle règle.</summary>
    public void SpamRulesChanged()
    {
        spam.SetRules(Configuration.SpamWords);
        FiltersChanged();
    }

    public Vector4? PlayerColor(ChatLink? link) =>
        link is { Kind: LinkKind.Player } && playerColors.Count > 0 && playerColors.TryGetValue(link.Text, out var color) ? color : null;

    public void SetPlayerColor(string name, Vector4 color, bool save = true)
    {
        // Une seule entrée par joueur, quelle que soit la façon d'écrire son nom.
        foreach (var key in Configuration.PlayerColors.Keys.Where(k => k != name && string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            Configuration.PlayerColors.Remove(key);

        Configuration.PlayerColors[name] = color;
        playerColors[name] = color;
        if (save)
            Configuration.Save();
    }

    public void RemovePlayerColor(string name)
    {
        Configuration.PlayerColors.Remove(name);
        playerColors.Remove(name);
        Configuration.Save();
    }

    /// <summary>Propose d'enregistrer ce que l'onglet affiche (recherche comprise) dans un fichier texte.</summary>
    public void ExportFeed(ChatFeed feed)
    {
        // Le texte est figé maintenant : ce qui s'exporte est ce qui était à l'écran au moment du clic.
        var text = ChatExporter.Format(feed.Visible, feed.RepeatCount);
        var count = feed.Visible.Count;

        var safeName = string.Concat(feed.Settings.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = $"BetterChat {safeName} {DateTime.Now:yyyy-MM-dd HH-mm}";
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        fileDialog.SaveFileDialog("Exporter la conversation", ".txt", fileName, ".txt", (confirmed, path) =>
        {
            if (!confirmed)
                return;

            Task.Run(() =>
            {
                try
                {
                    File.WriteAllText(path, text);
                    Framework.RunOnTick(() => Print($"{count} message(s) exporté(s) dans {path}"));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Export de la conversation impossible");
                    Framework.RunOnTick(() => Print($"Export impossible : {ex.Message}"));
                }
            });
        }, folder, true);
    }

    public void MutePlayer(string name)
    {
        if (Configuration.MutedPlayers.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;

        Configuration.MutedPlayers.Add(name);
        Configuration.Save();
        FiltersChanged();
    }

    public void UnmutePlayer(string name)
    {
        Configuration.MutedPlayers.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        Configuration.Save();
        FiltersChanged();
    }

    /// <summary>Efface les messages affichés et ceux du disque pour ce personnage.</summary>
    public void ClearMessages()
    {
        History.Delete();
        Messages.Clear();
        foreach (var feed in Feeds)
            feed.MarkRead();
    }

    public void RefreshCommandsIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now < nextCommandsRefresh)
            return;
        nextCommandsRefresh = now + CommandsRefreshDelay;

        var list = new List<CommandSuggestion>();
        foreach (var row in DataManager.GetExcelSheet<TextCommand>())
        {
            var description = FirstLine(row.Description.ExtractText());
            list.Add(new CommandSuggestion(row.Command.ExtractText(), description));
            list.Add(new CommandSuggestion(row.ShortCommand.ExtractText(), description));
            list.Add(new CommandSuggestion(row.Alias.ExtractText(), description));
            list.Add(new CommandSuggestion(row.ShortAlias.ExtractText(), description));
        }

        foreach (var (command, info) in CommandManager.Commands)
            list.Add(new CommandSuggestion(command, FirstLine(info.HelpMessage)));

        Commands.SetCommands(list);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return end < 0 ? text : text[..end];
    }

    public string DiagnosticLine() =>
        $"Ouvertures du chat vues : {Game.Activations}  |  " +
        $"canal : {GameChat.ChannelLabel(Game.CurrentChannel)}  |  messages en mémoire : {Messages.Lines.Count}";

    // ------------------------------------------------------------ Fenêtres

    public void ToggleConfigUi() => configWindow.Toggle();

    public void OpenConfigAtTabs()
    {
        configWindow.ShowTabsPage();
        configWindow.IsOpen = true;
    }

    public void ToggleChat()
    {
        chatWindow.Toggle();
        Configuration.ChatVisible = chatWindow.IsOpen;
        Configuration.Save();
    }

    public static void Print(string message) => ChatGui.Print(ChatPrefix + message);
}
