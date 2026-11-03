using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterChat.Core;

/// <summary>Type d'un message. Les numéros sont ceux du jeu, ce qui permet une simple conversion.</summary>
public enum ChatKind : ushort
{
    Other = 0,
    Debug = 1,
    Urgent = 2,
    Notice = 3,
    Say = 10,
    Shout = 11,
    TellOutgoing = 12,
    TellIncoming = 13,
    Party = 14,
    Alliance = 15,
    Linkshell1 = 16,
    Linkshell2 = 17,
    Linkshell3 = 18,
    Linkshell4 = 19,
    Linkshell5 = 20,
    Linkshell6 = 21,
    Linkshell7 = 22,
    Linkshell8 = 23,
    FreeCompany = 24,
    NoviceNetwork = 27,
    CustomEmote = 28,
    StandardEmote = 29,
    Yell = 30,
    CrossParty = 32,
    PvpTeam = 36,
    CrossLinkshell1 = 37,
    Damage = 41,
    Miss = 42,
    Action = 43,
    Item = 44,
    Healing = 45,
    GainBuff = 46,
    GainDebuff = 47,
    LoseBuff = 48,
    LoseDebuff = 49,
    GlamourNotifications = 54,
    Alarm = 55,
    Echo = 56,
    System = 57,
    BattleSystem = 58,
    GatheringSystem = 59,
    Error = 60,
    NpcDialogue = 61,
    LootNotice = 62,
    Progress = 64,
    LootRoll = 65,
    Crafting = 66,
    Gathering = 67,
    NpcAnnouncement = 68,
    FreeCompanyAnnouncement = 69,
    FreeCompanyLoginLogout = 70,
    RetainerSale = 71,
    PeriodicRecruitment = 72,
    Sign = 73,
    RandomNumber = 74,
    NoviceNetworkSystem = 75,
    Orchestrion = 76,
    PvpTeamAnnouncement = 77,
    PvpTeamLoginLogout = 78,
    MessageBook = 79,
    GmTell = 80,
    GmSay = 81,
    GmShout = 82,
    GmYell = 83,
    GmParty = 84,
    GmFreeCompany = 85,
    GmLinkshell1 = 86,
    GmLinkshell2 = 87,
    GmLinkshell3 = 88,
    GmLinkshell4 = 89,
    GmLinkshell5 = 90,
    GmLinkshell6 = 91,
    GmLinkshell7 = 92,
    GmLinkshell8 = 93,
    GmNoviceNetwork = 94,
    CrossLinkshell2 = 101,
    CrossLinkshell3 = 102,
    CrossLinkshell4 = 103,
    CrossLinkshell5 = 104,
    CrossLinkshell6 = 105,
    CrossLinkshell7 = 106,
    CrossLinkshell8 = 107,
}

/// <summary>Regroupement des types de messages dans les réglages d'un onglet.</summary>
public enum ChatSection
{
    Conversation,
    Linkshells,
    Emotes,
    Announcements,
    Activities,
    Combat,
    GameMasters,
}

/// <summary>Qui est à l'origine d'un message de combat.</summary>
public enum ChatSource : byte
{
    Unknown = 0,
    Self = 1,
    Party = 2,
    Others = 3,
}

/// <param name="Label">Nom complet, affiché dans les menus et les réglages.</param>
/// <param name="Tag">Étiquette courte, affichée devant les messages.</param>
/// <param name="Color">Couleur de secours (RGBA), si le jeu ne fournit pas la sienne.</param>
/// <param name="HasAuthor">Le message vient d'un joueur (son nom s'affiche et il peut être cliqué).</param>
public sealed record ChatKindInfo(ChatKind Kind, string Label, string Tag, ChatSection Section, uint Color, bool HasAuthor);

public static class ChatKinds
{
    private const uint White = 0xE8ECF4FF;
    private const uint Grey = 0xB4BAC8FF;
    private const uint Red = 0xFF8A8AFF;
    private const uint Green = 0xA4E89BFF;
    private const uint Yellow = 0xFFDC82FF;
    private const uint Blue = 0x7FC4FFFF;
    private const uint Pink = 0xFFB0E0FF;
    private const uint Teal = 0x7FE3D4FF;
    private const uint Orange = 0xFFB070FF;
    private const uint Purple = 0xC6A6FFFF;
    private const uint GmColor = 0xFFD24DFF;

    public static readonly IReadOnlyList<ChatKindInfo> All = BuildAll();

    private static readonly Dictionary<ChatKind, ChatKindInfo> ByKind = All.ToDictionary(i => i.Kind);

    private static readonly ChatKindInfo Unknown = new(ChatKind.Other, "Autres messages", "Autre", ChatSection.Announcements, Grey, false);

    private static List<ChatKindInfo> BuildAll()
    {
        var list = new List<ChatKindInfo>
        {
            new(ChatKind.Say, "Dire", "Dire", ChatSection.Conversation, White, true),
            new(ChatKind.Yell, "Crier", "Crier", ChatSection.Conversation, Yellow, true),
            new(ChatKind.Shout, "Hurler", "Hurler", ChatSection.Conversation, Orange, true),
            new(ChatKind.TellIncoming, "Message privé reçu", "MP", ChatSection.Conversation, Pink, true),
            new(ChatKind.TellOutgoing, "Message privé envoyé", "MP", ChatSection.Conversation, Pink, true),
            new(ChatKind.Party, "Groupe", "Groupe", ChatSection.Conversation, Blue, true),
            new(ChatKind.CrossParty, "Groupe inter-mondes", "Groupe", ChatSection.Conversation, Blue, true),
            new(ChatKind.Alliance, "Alliance", "Alliance", ChatSection.Conversation, Orange, true),
            new(ChatKind.FreeCompany, "Compagnie libre", "CL", ChatSection.Conversation, Teal, true),
            new(ChatKind.NoviceNetwork, "Novices", "Novices", ChatSection.Conversation, Green, true),
            new(ChatKind.PvpTeam, "Équipe JcJ", "JcJ", ChatSection.Conversation, Red, true),
        };

        for (var i = 1; i <= 8; i++)
            list.Add(new((ChatKind)((int)ChatKind.Linkshell1 + i - 1), $"Linkshell {i}", $"LS{i}", ChatSection.Linkshells, Green, true));

        list.Add(new(ChatKind.CrossLinkshell1, "Linkshell inter-mondes 1", "CW1", ChatSection.Linkshells, Green, true));
        for (var i = 2; i <= 8; i++)
            list.Add(new((ChatKind)((int)ChatKind.CrossLinkshell2 + i - 2), $"Linkshell inter-mondes {i}", $"CW{i}", ChatSection.Linkshells, Green, true));

        list.AddRange(new ChatKindInfo[]
        {
            new(ChatKind.CustomEmote, "Emotes écrites", "Emote", ChatSection.Emotes, Grey, true),
            new(ChatKind.StandardEmote, "Emotes", "Emote", ChatSection.Emotes, Grey, false),
            new(ChatKind.RandomNumber, "Nombres aléatoires", "Dé", ChatSection.Emotes, Grey, false),

            new(ChatKind.Urgent, "Annonces urgentes", "Urgent", ChatSection.Announcements, Red, false),
            new(ChatKind.Notice, "Avis", "Avis", ChatSection.Announcements, Yellow, false),
            new(ChatKind.System, "Messages système", "Système", ChatSection.Announcements, Grey, false),
            new(ChatKind.Error, "Erreurs", "Erreur", ChatSection.Announcements, Red, false),
            new(ChatKind.Echo, "Écho", "Écho", ChatSection.Announcements, Grey, false),
            new(ChatKind.Debug, "Plugins et débogage", "Plugin", ChatSection.Announcements, Purple, false),
            new(ChatKind.Other, "Autres messages", "Autre", ChatSection.Announcements, Grey, false),
            new(ChatKind.FreeCompanyAnnouncement, "Annonces de la compagnie", "CL", ChatSection.Announcements, Teal, false),
            new(ChatKind.FreeCompanyLoginLogout, "Connexions de la compagnie", "CL", ChatSection.Announcements, Teal, false),
            new(ChatKind.PvpTeamAnnouncement, "Annonces de l'équipe JcJ", "JcJ", ChatSection.Announcements, Red, false),
            new(ChatKind.PvpTeamLoginLogout, "Connexions de l'équipe JcJ", "JcJ", ChatSection.Announcements, Red, false),
            new(ChatKind.NoviceNetworkSystem, "Annonces des novices", "Novices", ChatSection.Announcements, Green, false),
            new(ChatKind.PeriodicRecruitment, "Recrutements de groupe", "Recrut.", ChatSection.Announcements, Grey, false),
            new(ChatKind.NpcDialogue, "Dialogues de PNJ", "PNJ", ChatSection.Announcements, Grey, false),
            new(ChatKind.NpcAnnouncement, "Annonces de PNJ", "PNJ", ChatSection.Announcements, Grey, false),
            new(ChatKind.Alarm, "Alarmes", "Alarme", ChatSection.Announcements, Yellow, false),
            new(ChatKind.Sign, "Marqueurs de cible", "Signe", ChatSection.Announcements, Grey, false),
            new(ChatKind.MessageBook, "Répondeur", "Répondeur", ChatSection.Announcements, Grey, false),

            new(ChatKind.LootNotice, "Butin obtenu", "Butin", ChatSection.Activities, Grey, false),
            new(ChatKind.LootRoll, "Jets de butin", "Butin", ChatSection.Activities, Grey, false),
            new(ChatKind.Progress, "Progression", "Prog.", ChatSection.Activities, Grey, false),
            new(ChatKind.Crafting, "Artisanat", "Artisanat", ChatSection.Activities, Grey, false),
            new(ChatKind.Gathering, "Récolte", "Récolte", ChatSection.Activities, Grey, false),
            new(ChatKind.GatheringSystem, "Système de récolte", "Récolte", ChatSection.Activities, Grey, false),
            new(ChatKind.RetainerSale, "Ventes des servants", "Vente", ChatSection.Activities, Grey, false),
            new(ChatKind.GlamourNotifications, "Mirages", "Mirage", ChatSection.Activities, Grey, false),
            new(ChatKind.Orchestrion, "Orchestrion", "Musique", ChatSection.Activities, Grey, false),

            new(ChatKind.Damage, "Dégâts", "Dégâts", ChatSection.Combat, Red, false),
            new(ChatKind.Miss, "Coups manqués", "Raté", ChatSection.Combat, Grey, false),
            new(ChatKind.Action, "Actions", "Action", ChatSection.Combat, White, false),
            new(ChatKind.Item, "Objets utilisés", "Objet", ChatSection.Combat, White, false),
            new(ChatKind.Healing, "Soins", "Soin", ChatSection.Combat, Green, false),
            new(ChatKind.GainBuff, "Bonus obtenus", "Bonus", ChatSection.Combat, Blue, false),
            new(ChatKind.LoseBuff, "Bonus perdus", "Bonus", ChatSection.Combat, Blue, false),
            new(ChatKind.GainDebuff, "Malus subis", "Malus", ChatSection.Combat, Orange, false),
            new(ChatKind.LoseDebuff, "Malus retirés", "Malus", ChatSection.Combat, Orange, false),
            new(ChatKind.BattleSystem, "Système de combat", "Combat", ChatSection.Combat, Grey, false),

            new(ChatKind.GmTell, "MJ : message privé", "MJ", ChatSection.GameMasters, GmColor, true),
            new(ChatKind.GmSay, "MJ : dire", "MJ", ChatSection.GameMasters, GmColor, true),
            new(ChatKind.GmShout, "MJ : hurler", "MJ", ChatSection.GameMasters, GmColor, true),
            new(ChatKind.GmYell, "MJ : crier", "MJ", ChatSection.GameMasters, GmColor, true),
            new(ChatKind.GmParty, "MJ : groupe", "MJ", ChatSection.GameMasters, GmColor, true),
            new(ChatKind.GmFreeCompany, "MJ : compagnie", "MJ", ChatSection.GameMasters, GmColor, true),
            new(ChatKind.GmNoviceNetwork, "MJ : novices", "MJ", ChatSection.GameMasters, GmColor, true),
        });

        for (var i = 1; i <= 8; i++)
            list.Add(new((ChatKind)((int)ChatKind.GmLinkshell1 + i - 1), $"MJ : linkshell {i}", "MJ", ChatSection.GameMasters, GmColor, true));

        return list;
    }

    public static string SectionLabel(ChatSection section) => section switch
    {
        ChatSection.Conversation => "Discussions",
        ChatSection.Linkshells => "Linkshells",
        ChatSection.Emotes => "Emotes",
        ChatSection.Announcements => "Annonces et système",
        ChatSection.Activities => "Activités",
        ChatSection.Combat => "Combat",
        ChatSection.GameMasters => "Maîtres de jeu",
        _ => section.ToString(),
    };

    public static ChatKindInfo Info(ChatKind kind) => ByKind.TryGetValue(kind, out var info) ? info : Unknown;

    /// <summary>Un code de message inconnu reste visible, sous « Autres messages », au lieu de se perdre.</summary>
    public static ChatKind Normalize(ushort raw)
    {
        var kind = (ChatKind)raw;
        return ByKind.ContainsKey(kind) ? kind : ChatKind.Other;
    }

    public static IEnumerable<ChatKind> InSection(ChatSection section) =>
        All.Where(i => i.Section == section).Select(i => i.Kind);

    public static bool IsBattle(ChatKind kind) => Info(kind).Section == ChatSection.Combat;

    public static bool IsTell(ChatKind kind) => kind is ChatKind.TellIncoming or ChatKind.TellOutgoing or ChatKind.GmTell;

    /// <summary>Les linkshells du jeu, dans l'ordre : 1 à 8.</summary>
    public static ChatKind Linkshell(int number) => (ChatKind)((int)ChatKind.Linkshell1 + Math.Clamp(number, 1, 8) - 1);

    /// <summary>Les linkshells inter-mondes : la 1 n'a pas le même numéro que les suivantes.</summary>
    public static ChatKind CrossLinkshell(int number) =>
        number <= 1 ? ChatKind.CrossLinkshell1 : (ChatKind)((int)ChatKind.CrossLinkshell2 + Math.Min(number, 8) - 2);
}
