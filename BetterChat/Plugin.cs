using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
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

    public List<ChatFeed> Feeds { get; } = new();

    private readonly MentionMatcher mentions = new();
    private readonly HashSet<ChatKind> wantedKinds = new();
    private readonly ChatWindow chatWindow;
    private readonly ConfigWindow configWindow;

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
        if (Configuration.Tabs.Count == 0)
            Configuration.Tabs = ChatTabPresets.Default();

        Game = new GameChat();
        Colors = new ChannelColors(Configuration);
        History = new ChatHistoryStore(
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "history"),
            (ex, what) => Log.Error(ex, what));

        Messages.Capacity = Configuration.MemoryLines;
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

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleChat;
        ChatGui.ChatMessageUnhandled += OnChatMessage;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessageUnhandled -= OnChatMessage;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
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

    private void OnChatMessage(IChatMessage message)
    {
        var line = MessageConverter.Convert(message);
        if (!wantedKinds.Contains(line.Kind))
            return;

        // Tes propres messages n'ont pas de lien sur ton nom, et il peut être précédé de ton numéro de groupe.
        var isOwn = LocalName.Length > 0 && line.AuthorKey.EndsWith(LocalName, StringComparison.Ordinal);
        if (!isOwn && ChatKinds.Info(line.Kind).HasAuthor && mentions.Matches(line.BodyText))
            line.Mentioned = true;

        if (ChatKinds.IsTell(line.Kind) && line.Author != null && !isOwn)
            RecentTells.Note(line.Author);

        var muted = IsMuted(line);
        if (!muted)
            PlayAlertFor(line);

        Messages.Add(line);

        if (Configuration.KeepHistory && loadedCharacter != 0 && !ChatKinds.IsBattle(line.Kind))
            History.Enqueue(line);
    }

    private void PlayAlertFor(ChatLine line)
    {
        if ((line.Kind == ChatKind.TellIncoming && Configuration.SoundOnTell) ||
            (line.Mentioned && Configuration.SoundOnMention))
            GameChat.PlayAlert(Configuration.AlertSound);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Game.Update(Configuration.HideNativeChat && Configuration.ChatVisible && !OtherChatPluginLoaded);

        var character = CurrentCharacterId;
        if (character != 0 && character != loadedCharacter)
            LoadCharacter(character);

        UpdateFeeds();

        var now = DateTime.UtcNow;
        if (now >= nextHistoryFlush)
        {
            nextHistoryFlush = now + HistoryFlushDelay;
            History.FlushInBackground();
        }
    }

    /// <summary>Un autre personnage : son historique remplace le précédent, sans perdre les messages déjà arrivés.</summary>
    private void LoadCharacter(ulong character)
    {
        var early = loadedCharacter == 0 ? Messages.Lines.ToList() : new List<ChatLine>();

        loadedCharacter = character;
        History.SelectCharacter(character);
        RecentTells.Clear();
        Messages.Clear();

        if (Configuration.KeepHistory)
        {
            foreach (var line in History.Load(Configuration.HistoryLines))
            {
                if (wantedKinds.Contains(line.Kind))
                    Messages.Add(line);
            }
        }

        foreach (var line in early)
        {
            Messages.Add(line);
            if (Configuration.KeepHistory && !ChatKinds.IsBattle(line.Kind))
                History.Enqueue(line);
        }

        var name = LocalName;
        if (name != mentionedName)
            RefreshMentions();
    }

    private void UpdateFeeds()
    {
        var active = ActiveFeed;
        foreach (var feed in Feeds)
        {
            feed.IsActive = ReferenceEquals(feed, active);
            feed.Update(Messages, filterVersion, IsMuted);
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

    public void SelectTab(int index)
    {
        if (index < 0 || index >= Feeds.Count)
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
