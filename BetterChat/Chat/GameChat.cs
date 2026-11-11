using System;
using System.Collections.Generic;
using Dalamud.Game.Config;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using BetterChat.Core;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace BetterChat.Chat;

public enum SendResult
{
    Sent,
    Empty,
    TooLong,
    InvalidCharacters,
}

/// <summary>Un canal où l'on peut écrire, avec le nom à afficher.</summary>
public sealed record ChannelChoice(ChatKind Kind, string Label);

/// <summary>
/// Tout ce qui touche au chat du jeu : masquer sa fenêtre, savoir quel canal est actif, envoyer un message et
/// détecter la touche qui ouvre le chat. Le jeu reste seul maître du canal : le plugin ne fait que lui demander d'en changer.
/// </summary>
public sealed unsafe class GameChat : IDisposable
{
    private static readonly string[] NativeWindows =
    {
        "ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3",
    };

    // Valeurs reçues par la fenêtre du chat quand le jeu veut donner le focus à sa zone de saisie.
    private const uint ActivationValueCount = 0x31;
    private const uint ActivationByKey = 0x05;
    private const uint ActivationWithText = 0x0C;
    private const int ActivationTextIndex = 2;

    // Lettres, chiffres, symboles et liens autorisés : le même filtre que la zone de saisie du jeu.
    private const AllowedEntities TextFilter =
        AllowedEntities.UppercaseLetters | AllowedEntities.LowercaseLetters | AllowedEntities.Numbers |
        AllowedEntities.SpecialCharacters | AllowedEntities.CharacterList | AllowedEntities.OtherCharacters |
        AllowedEntities.Payloads | AllowedEntities.Unknown9;

    private bool nativeHidden;
    private DateTime nextChannelRead = DateTime.MinValue;

    /// <summary>Le chat du jeu vient de réclamer le focus. Le texte éventuel (« / » par exemple) est à insérer.</summary>
    public event Action<string?>? Activated;

    /// <summary>Le plugin peut-il prendre le focus maintenant ? Sinon le jeu garde la main.</summary>
    public Func<bool>? CanTakeActivation { get; set; }

    /// <summary>Faut-il ignorer la touche du chat tant que le plugin ne peut pas prendre le focus ?</summary>
    public Func<bool>? BlockActivation { get; set; }

    /// <summary>Nombre de fois où la touche du chat a été interceptée (diagnostic).</summary>
    public int Activations { get; private set; }

    public ChatKind CurrentChannel { get; private set; } = ChatKind.Say;
    public string TellTarget { get; private set; } = string.Empty;

    public GameChat()
    {
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRefresh, "ChatLog", OnChatLogRefresh);
        LoadGameCommands();
    }

    /// <summary>Numéros des canaux dans la liste des commandes du jeu.</summary>
    private static readonly (ChatKind Kind, uint[] Rows)[] ChannelCommandRows = BuildChannelCommandRows();

    private static (ChatKind Kind, uint[] Rows)[] BuildChannelCommandRows()
    {
        var rows = new List<(ChatKind, uint[])>
        {
            (ChatKind.Say, new uint[] { 102 }),
            (ChatKind.Shout, new uint[] { 103 }),
            (ChatKind.Yell, new uint[] { 117 }),
            (ChatKind.Party, new uint[] { 105 }),
            (ChatKind.Alliance, new uint[] { 119 }),
            (ChatKind.FreeCompany, new uint[] { 115 }),
            (ChatKind.PvpTeam, new uint[] { 91 }),
            (ChatKind.NoviceNetwork, new uint[] { 101 }),
            (ChatKind.TellOutgoing, new uint[] { 104, 118 }),
        };

        for (var i = 1; i <= 8; i++)
        {
            rows.Add((ChatKinds.CrossLinkshell(i), new[] { (uint)(12 + i) }));
            rows.Add((ChatKinds.Linkshell(i), new[] { (uint)(106 + i) }));
        }

        return rows.ToArray();
    }

    /// <summary>Reprend du jeu les commandes des canaux. La colonne des alias est lue d'abord : c'est là que le client français range /h et /cri.</summary>
    private static void LoadGameCommands()
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TextCommand>();
            foreach (var (kind, rows) in ChannelCommandRows)
            {
                var commands = new List<string>();
                foreach (var id in rows)
                {
                    if (sheet.GetRowOrDefault(id) is not { } row)
                        continue;

                    foreach (var text in new[] { row.ShortAlias, row.ShortCommand, row.Alias, row.Command })
                    {
                        var command = text.ExtractText();
                        if (command.Length > 1 && command[0] == '/' && !commands.Contains(command))
                            commands.Add(command);
                    }
                }

                ChatChannels.SetCommands(kind, commands);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Commandes du jeu illisibles : les commandes par défaut restent utilisées");
        }
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "ChatLog", OnChatLogRefresh);

        // Quoi qu'il arrive, le joueur doit retrouver le chat du jeu.
        SetNativeVisible(true);
    }

    // ------------------------------------------------------- À chaque image

    public void Update(bool wantHidden)
    {
        if (wantHidden)
        {
            nativeHidden = true;
            SetNativeVisible(false);
        }
        else if (nativeHidden)
        {
            nativeHidden = false;
            SetNativeVisible(true);
        }

        var now = DateTime.UtcNow;
        if (now >= nextChannelRead)
        {
            nextChannelRead = now.AddMilliseconds(200);
            ReadChannel();
        }
    }

    private static void SetNativeVisible(bool visible)
    {
        foreach (var name in NativeWindows)
        {
            var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon != null && addon->IsVisible != visible)
                addon->IsVisible = visible;
        }
    }

    // ---------------------------------------------- Touche d'ouverture du chat

    /// <summary>
    /// Le jeu rafraîchit sa fenêtre de chat pour donner le focus à sa zone de saisie. Dalamud permet d'annuler ce
    /// rafraîchissement : aucun code du jeu n'est modifié, et rien ne casse à une mise à jour du jeu.
    /// </summary>
    private void OnChatLogRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refresh)
            return;

        var values = (AtkValue*)refresh.AtkValues;
        if (!IsActivation(refresh.AtkValueCount, values))
            return;

        if (!(CanTakeActivation?.Invoke() ?? false))
        {
            // Aucun chat à l'écran : le jeu donnerait le focus à sa saisie masquée, et le clavier écrirait dans le vide.
            if (nativeHidden && (BlockActivation?.Invoke() ?? false))
                args.PreventOriginal();
            return;
        }

        args.PreventOriginal();
        Activations++;
        Activated?.Invoke(ReadActivationText(values));
    }

    private static bool IsActivation(uint valueCount, AtkValue* values) =>
        valueCount == ActivationValueCount && values != null && values->UInt is ActivationByKey or ActivationWithText;

    private static string? ReadActivationText(AtkValue* values)
    {
        var value = values + ActivationTextIndex;
        if (((int)value->Type & 0xF) != (int)AtkValueType.String || !value->String.HasValue)
            return null;

        var text = value->String.ToString();
        return text.Length > 0 ? text : null;
    }

    /// <summary>« Discussion directe » du jeu : la première lettre tapée ouvre le chat et doit y figurer.</summary>
    public static bool DirectChatEnabled() =>
        Plugin.GameConfig.TryGet(UiControlOption.DirectChat, out bool enabled) && enabled;

    // --------------------------------------------------------------- Canal actif

    private void ReadChannel()
    {
        var shell = RaptureShellModule.Instance();
        if (shell == null)
            return;

        CurrentChannel = ChatChannels.FromGameChannel(shell->ChatType);
        if (CurrentChannel != ChatKind.TellOutgoing)
        {
            TellTarget = string.Empty;
            return;
        }

        var name = shell->TellName.ToString();
        var world = shell->TellWorld.ToString();
        TellTarget = world.Length > 0 ? $"{name}@{world}" : name;
    }

    /// <summary>Le nom d'une linkshell, ou vide si cet emplacement n'est pas utilisé.</summary>
    public static string LinkshellName(int number)
    {
        var proxy = InfoProxyChat.Instance();
        if (proxy == null)
            return string.Empty;

        var name = proxy->GetLinkShellName((uint)(number - 1));
        return name.HasValue ? name.ToString() : string.Empty;
    }

    public static string CrossLinkshellName(int number)
    {
        var proxy = InfoProxyCrossWorldLinkshell.Instance();
        if (proxy == null)
            return string.Empty;

        var name = proxy->GetCrossworldLinkshellName((uint)(number - 1));
        return name != null ? name->ToString() : string.Empty;
    }

    /// <summary>Le nom affiché pour un canal, avec le vrai nom des linkshells.</summary>
    public static string ChannelLabel(ChatKind kind)
    {
        var info = ChatKinds.Info(kind);
        for (var i = 1; i <= 8; i++)
        {
            if (kind == ChatKinds.Linkshell(i))
                return Named(info, LinkshellName(i));
            if (kind == ChatKinds.CrossLinkshell(i))
                return Named(info, CrossLinkshellName(i));
        }

        return info.Tag == "MP" ? "Message privé" : info.Label;

        static string Named(ChatKindInfo info, string name) => name.Length > 0 ? $"{info.Tag} {name}" : info.Label;
    }

    /// <summary>Les canaux utilisables en ce moment. Tab les parcourt, le menu les affiche.</summary>
    public static List<ChannelChoice> AvailableChannels()
    {
        var choices = new List<ChannelChoice> { new(ChatKind.Say, "Dire") };

        var crossRealm = InfoProxyCrossRealm.IsCrossRealmParty();
        if (Plugin.PartyList.Length > 0 || crossRealm)
            choices.Add(new(ChatKind.Party, "Groupe"));
        if (Plugin.PartyList.IsAlliance || InfoProxyCrossRealm.IsAllianceRaid())
            choices.Add(new(ChatKind.Alliance, "Alliance"));
        if (Plugin.ObjectTable.LocalPlayer is { } player && player.CompanyTag.TextValue.Length > 0)
            choices.Add(new(ChatKind.FreeCompany, "Compagnie libre"));

        choices.Add(new(ChatKind.Yell, ChatKinds.Info(ChatKind.Yell).Label));
        choices.Add(new(ChatKind.Shout, ChatKinds.Info(ChatKind.Shout).Label));

        for (var i = 1; i <= 8; i++)
        {
            var name = LinkshellName(i);
            if (name.Length > 0)
                choices.Add(new(ChatKinds.Linkshell(i), $"LS{i} {name}"));
        }

        for (var i = 1; i <= 8; i++)
        {
            var name = CrossLinkshellName(i);
            if (name.Length > 0)
                choices.Add(new(ChatKinds.CrossLinkshell(i), $"CW{i} {name}"));
        }

        choices.Add(new(ChatKind.NoviceNetwork, "Novices"));
        return choices;
    }

    /// <summary>
    /// Change de canal directement, sans passer par une commande : un autre plugin ou la langue du jeu
    /// peut changer ce qu'elle fait (/sh a déjà répondu par une erreur d'argument manquant).
    /// </summary>
    public void SwitchChannel(ChatKind kind)
    {
        var channel = ChatChannels.ToGameChannel(kind);
        if (channel < 0)
            return;

        var linkshell = ChatChannels.LinkshellIndex(kind);
        Plugin.Framework.RunOnTick(() =>
        {
            var shell = RaptureShellModule.Instance();
            if (shell == null || IsEmptyLinkshell(kind))
                return;

            var noTarget = Utf8String.FromString(string.Empty);
            shell->ChangeChatChannel(channel, linkshell, noTarget, true);
            noTarget->Dtor(true);
        });
    }

    /// <summary>Un onglet peut viser une linkshell que ce personnage n'a pas.</summary>
    private static bool IsEmptyLinkshell(ChatKind kind)
    {
        for (var i = 1; i <= 8; i++)
        {
            if (kind == ChatKinds.Linkshell(i))
                return LinkshellName(i).Length == 0;
            if (kind == ChatKinds.CrossLinkshell(i))
                return CrossLinkshellName(i).Length == 0;
        }

        return false;
    }

    /// <summary>
    /// Répondre à ce message : passe la saisie sur son canal, ou en message privé avec la personne. Faux si on ne peut
    /// pas y répondre (message système, combat, canal qu'on n'a plus...).
    /// </summary>
    public bool CanReply(ChatLine line) => ReplyAction(line) != null;

    public void Reply(ChatLine line) => ReplyAction(line)?.Invoke();

    private System.Action? ReplyAction(ChatLine line)
    {
        if (line.Kind is ChatKind.TellIncoming or ChatKind.TellOutgoing)
        {
            // Pour un message envoyé, l'auteur retenu est le destinataire.
            var person = line.Author;
            return person != null && person.Text.Length > 0 ? () => SwitchToTell(person) : null;
        }

        // Une emote se passe à portée de voix : on y répond en disant.
        var kind = line.Kind is ChatKind.StandardEmote or ChatKind.CustomEmote ? ChatKind.Say : line.Kind;
        return AvailableChannels().Exists(c => c.Kind == kind) ? () => SwitchChannel(kind) : null;
    }

    /// <summary>Passe le jeu en message privé vers ce joueur (les prochains messages partent vers lui).</summary>
    public void SwitchToTell(ChatLink player) => Run($"/tell {player.TellTarget}");

    // -------------------------------------------------------------------- Envoi

    /// <summary>Envoie ce que le joueur a écrit. Le texte part sur le canal actif du jeu, ou selon la commande tapée.</summary>
    public SendResult Send(string text)
    {
        var message = ChatText.Normalize(text);
        if (message.Length == 0)
            return SendResult.Empty;

        // Trop long : découpé en plusieurs messages sur le même canal, si c'est un message et non une commande.
        var first = Encode(message, out var whole);
        var parts = first == SendResult.TooLong ? MessageSplitter.Split(message, ChatText.MaxBytes) : null;
        if (parts == null || parts.Count < 2)
        {
            if (first == SendResult.Sent)
                RunBytes(whole);
            return first;
        }

        // Tout est vérifié avant d'envoyer : jamais un message à moitié parti.
        var encoded = new List<byte[]>(parts.Count);
        foreach (var part in parts)
        {
            var result = Encode(part, out var bytes);
            if (result != SendResult.Sent)
                return result;
            encoded.Add(bytes);
        }

        for (var i = 0; i < encoded.Count; i++)
            RunBytes(encoded[i], SplitInterval * i);
        return SendResult.Sent;
    }

    /// <summary>Le temps entre deux morceaux d'un message découpé : ils arrivent dans l'ordre, sans inonder le chat.</summary>
    private static readonly TimeSpan SplitInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>Combien de messages partiront pour ce texte : 1 s'il tient, 0 s'il est trop long et ne peut pas être découpé.</summary>
    public static int PartCount(string text)
    {
        var message = ChatText.Normalize(text);
        if (ChatText.ByteLength(message) <= ChatText.MaxBytes)
            return 1;
        return MessageSplitter.Split(message, ChatText.MaxBytes)?.Count ?? 0;
    }

    /// <summary>La liste de l'autotraduction du jeu.</summary>
    internal AutoTranslateData AutoTranslate { get; } = new();

    /// <summary>
    /// Le message encodé pour le jeu. Les mots entre flèches vertes deviennent de vrais liens d'autotraduction ; un mot
    /// qu'on ne retrouve pas dans la liste (modifié à la main) part comme du texte normal.
    /// </summary>
    private SendResult Encode(string message, out byte[] encoded)
    {
        encoded = Array.Empty<byte>();
        var bytes = new List<byte>();
        var plain = new System.Text.StringBuilder();
        foreach (var part in AutoTranslateText.Split(message))
        {
            if (part.IsEntry && AutoTranslate.Find(part.Text) is { } entry)
            {
                bytes.AddRange(AutoTranslateData.Encode(entry.Group, entry.Key));
                continue;
            }

            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(part.Text));
            plain.Append(part.Text);
        }

        if (bytes.Count > ChatText.MaxBytes)
            return SendResult.TooLong;
        if (!IsAllowedByGame(plain.ToString()))
            return SendResult.InvalidCharacters;

        encoded = bytes.ToArray();
        return SendResult.Sent;
    }

    private static bool IsAllowedByGame(string text)
    {
        var checkedText = Utf8String.FromString(text);
        try
        {
            checkedText->SanitizeString(TextFilter);
            return checkedText->ToString().Length == text.Length;
        }
        finally
        {
            checkedText->Dtor(true);
        }
    }

    /// <summary>Le jeu traite la commande au prochain tour de sa boucle, là où il s'y attend.</summary>
    internal static void Run(string text) => RunBytes(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Envoie un texte déjà encodé pour le jeu, liens d'autotraduction compris.</summary>
    private static void RunBytes(byte[] bytes, TimeSpan delay = default)
    {
        Plugin.Framework.RunOnTick(delay: delay, action: () =>
        {
            var module = UIModule.Instance();
            if (module == null)
                return;

            var entry = Utf8String.FromSequence(bytes);
            module->ProcessChatBoxEntry(entry);
            entry->Dtor(true);
        });
    }

    /// <summary>Prépare le lien de cet objet : &lt;item&gt; dans un message l'affichera.</summary>
    /// <summary>Ouvre une annonce de recherche de groupe depuis son lien dans le chat.</summary>
    /// <summary>
    /// Un lien ajouté par un plugin : son action est rendue au plugin, comme si on avait cliqué dans le chat du jeu.
    /// Faux si le plugin n'est plus chargé (un lien relu dans l'historique, par exemple).
    /// </summary>
    public static bool ClickPluginLink(ChatLink link)
    {
        if (!Plugin.ChatGui.RegisteredLinkHandlers.TryGetValue((link.Text, link.Id), out var handler))
            return false;

        try
        {
            var message = link.Data is { Length: > 0 } data ? SeString.Parse(data) : new SeString();
            handler.Invoke(link.Id, message);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Le lien du plugin {Plugin} a échoué", link.Text);
            return false;
        }
    }

    /// <summary>Ouvre la fiche du haut fait, comme un clic sur son nom dans le chat du jeu.</summary>
    public static void OpenAchievement(uint achievementId)
    {
        unsafe
        {
            var agent = AgentAchievement.Instance();
            if (agent != null)
                agent->OpenById(achievementId);
        }
    }

    public static void OpenPartyFinder(uint listingId)
    {
        unsafe
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent != null)
                agent->OpenListing(listingId);
        }
    }

    /// <summary>L'objet se fabrique-t-il ? La feuille du jeu a une ligne par objet, avec sa recette pour chaque artisan.</summary>
    public static bool HasRecipe(uint itemId)
    {
        var row = Plugin.DataManager.GetExcelSheet<RecipeLookup>().GetRowOrDefault(itemId);
        return row is { } recipes &&
               (recipes.CRP.RowId != 0 || recipes.BSM.RowId != 0 || recipes.ARM.RowId != 0 || recipes.GSM.RowId != 0 ||
                recipes.LTW.RowId != 0 || recipes.WVR.RowId != 0 || recipes.ALC.RowId != 0 || recipes.CUL.RowId != 0);
    }

    /// <summary>Ouvre le carnet d'artisanat du jeu sur la recette de cet objet.</summary>
    public static void OpenRecipe(uint itemId)
    {
        unsafe
        {
            var agent = AgentRecipeNote.Instance();
            if (agent != null)
                agent->SearchRecipeByItemId(itemId, 0);
        }
    }

    /// <summary>Dernier objet lié depuis le chat, pour montrer ce que deviendra &lt;item&gt;.</summary>
    public static uint LastLinkedItem { get; private set; }

    public static void LinkItem(uint itemId)
    {
        LastLinkedItem = itemId;
        var agent = AgentChatLog.Instance();
        if (agent != null)
            agent->LinkItem(itemId);
    }

    public static void PlayAlert(int sound) => UIGlobals.PlaySoundEffect((uint)(36 + Math.Clamp(sound, 1, 16)));
}
