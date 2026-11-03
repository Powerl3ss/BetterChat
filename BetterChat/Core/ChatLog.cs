using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterChat.Core;

/// <summary>
/// Tous les messages en mémoire, du plus ancien au plus récent. Quand la limite est dépassée, le combat part
/// en premier : un long combat ne doit pas faire disparaître les discussions.
/// </summary>
public sealed class ChatLog
{
    private readonly List<ChatLine> lines = new();
    private long nextId = 1;

    /// <summary>Nombre de messages gardés. Le nettoyage part par paquets pour ne pas coûter à chaque message.</summary>
    public int Capacity { get; set; } = 8000;

    /// <summary>Change quand des anciens messages disparaissent : les vues doivent alors se reconstruire.</summary>
    public int Generation { get; private set; }

    public IReadOnlyList<ChatLine> Lines => lines;

    public long LastId => nextId - 1;

    public void Add(ChatLine line)
    {
        line.Id = nextId++;
        lines.Add(line);

        // Un dixième de marge : le nettoyage ne repasse pas sur toute la liste à chaque nouveau message.
        if (lines.Count > Capacity + Math.Max(50, Capacity / 10))
            Trim();
    }

    public void Clear()
    {
        lines.Clear();
        Generation++;
    }

    private void Trim()
    {
        var excess = lines.Count - Capacity;
        if (excess <= 0)
            return;

        var removedBattle = 0;
        lines.RemoveAll(l =>
        {
            if (removedBattle >= excess || !ChatKinds.IsBattle(l.Kind))
                return false;
            removedBattle++;
            return true;
        });

        excess = lines.Count - Capacity;
        if (excess > 0)
            lines.RemoveRange(0, excess);

        Generation++;
    }
}

/// <summary>Réglages d'un onglet. Enregistrés dans la configuration du plugin.</summary>
[Serializable]
public sealed class ChatTabSettings
{
    public string Name { get; set; } = "Onglet";
    public List<ChatKind> Kinds { get; set; } = new();

    /// <summary>Pour le combat : n'afficher que soi, ou soi et le groupe.</summary>
    public BattleFilter Battle { get; set; } = BattleFilter.Everyone;

    /// <summary>Compter les messages non lus de cet onglet.</summary>
    public bool ShowUnread { get; set; } = true;

    /// <summary>Canal choisi en ouvrant l'onglet. Autre : ne rien changer.</summary>
    public ChatKind InputChannel { get; set; } = ChatKind.Other;

    public bool Accepts(ChatLine line)
    {
        if (!Kinds.Contains(line.Kind))
            return false;

        if (!ChatKinds.IsBattle(line.Kind))
            return true;

        return Battle switch
        {
            BattleFilter.SelfOnly => line.Source is ChatSource.Self or ChatSource.Unknown,
            BattleFilter.Party => line.Source is ChatSource.Self or ChatSource.Party or ChatSource.Unknown,
            _ => true,
        };
    }
}

public enum BattleFilter
{
    Everyone,
    Party,
    SelfOnly,
}

/// <summary>Les onglets proposés à la première utilisation.</summary>
public static class ChatTabPresets
{
    public static List<ChatTabSettings> Default() => new()
    {
        General(),
        new ChatTabSettings
        {
            Name = "Groupe",
            Kinds = new() { ChatKind.Party, ChatKind.CrossParty, ChatKind.Alliance },
        },
        new ChatTabSettings { Name = "Communauté", Kinds = Community() },
        new ChatTabSettings
        {
            Name = "Privé",
            Kinds = new() { ChatKind.TellIncoming, ChatKind.TellOutgoing, ChatKind.GmTell },
        },
        new ChatTabSettings
        {
            Name = "Combat",
            Kinds = ChatKinds.InSection(ChatSection.Combat).ToList(),
            Battle = BattleFilter.Party,
            ShowUnread = false,
        },
    };

    /// <summary>Tout sauf le combat, comme le premier onglet du chat du jeu.</summary>
    public static ChatTabSettings General() => new()
    {
        Name = "Général",
        Kinds = ChatKinds.All.Where(i => i.Section != ChatSection.Combat).Select(i => i.Kind).ToList(),
    };

    private static List<ChatKind> Community()
    {
        var kinds = ChatKinds.InSection(ChatSection.Linkshells).ToList();
        kinds.AddRange(new[]
        {
            ChatKind.FreeCompany, ChatKind.FreeCompanyAnnouncement, ChatKind.FreeCompanyLoginLogout,
            ChatKind.NoviceNetwork, ChatKind.NoviceNetworkSystem,
            ChatKind.PvpTeam, ChatKind.PvpTeamAnnouncement, ChatKind.PvpTeamLoginLogout,
        });
        return kinds;
    }
}

/// <summary>Les messages visibles d'un onglet : son filtre, la recherche en cours, ses messages non lus.</summary>
public sealed class ChatFeed
{
    private readonly List<ChatLine> visible = new();
    private readonly List<ChatLine> rebuilding = new();
    private string search = string.Empty;
    private long lastId;
    private int generation = -1;
    private int filterVersion = -1;

    public ChatFeed(ChatTabSettings settings)
    {
        Settings = settings;
    }

    public ChatTabSettings Settings { get; }

    public IReadOnlyList<ChatLine> Visible => visible;

    /// <summary>Change quand la liste a été reconstruite : l'affichage doit tout remettre en page.</summary>
    public int RebuildCount { get; private set; }

    public int Unread { get; private set; }
    public int UnreadMentions { get; private set; }

    /// <summary>Un onglet affiché n'accumule pas de non lus.</summary>
    public bool IsActive { get; set; }

    public string Search => search;

    public void SetSearch(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text == search)
            return;

        search = text;
        generation = -1; // reconstruction à la prochaine mise à jour
    }

    public void MarkRead()
    {
        Unread = 0;
        UnreadMentions = 0;
    }

    /// <param name="filterVersion">Change quand le filtre de l'onglet ou la liste des joueurs masqués change.</param>
    /// <param name="isMuted">Renvoie vrai pour un message dont l'auteur est masqué.</param>
    public void Update(ChatLog log, int filterVersion, Func<ChatLine, bool> isMuted)
    {
        if (generation != log.Generation || this.filterVersion != filterVersion)
        {
            generation = log.Generation;
            this.filterVersion = filterVersion;
            Rebuild(log, isMuted);
            return;
        }

        if (log.LastId == lastId)
            return;

        // Les nouveaux messages sont en fin de liste : on repart juste du premier qu'on n'a pas encore vu.
        var lines = log.Lines;
        var start = lines.Count;
        while (start > 0 && lines[start - 1].Id > lastId)
            start--;

        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!Settings.Accepts(line) || isMuted(line))
                continue;

            if (Matches(line))
                visible.Add(line);

            if (!IsActive && !line.FromHistory)
            {
                if (Settings.ShowUnread)
                    Unread++;
                if (line.Mentioned || line.Kind == ChatKind.TellIncoming)
                    UnreadMentions++;
            }
        }

        lastId = log.LastId;
    }

    private void Rebuild(ChatLog log, Func<ChatLine, bool> isMuted)
    {
        rebuilding.Clear();
        foreach (var line in log.Lines)
        {
            if (Settings.Accepts(line) && !isMuted(line) && Matches(line))
                rebuilding.Add(line);
        }

        visible.Clear();
        visible.AddRange(rebuilding);
        rebuilding.Clear();

        lastId = log.LastId;
        RebuildCount++;
    }

    private bool Matches(ChatLine line) => search.Length == 0 || line.SearchText.Contains(search, StringComparison.Ordinal);
}

/// <summary>Position verticale de chaque message d'une liste, pour ne dessiner que ceux à l'écran.</summary>
public sealed class LineStack
{
    private readonly List<float> tops = new();

    public int Count => tops.Count;

    public float Total { get; private set; }

    public void Clear()
    {
        tops.Clear();
        Total = 0f;
    }

    public void Add(float height)
    {
        tops.Add(Total);
        Total += height;
    }

    public float Top(int index) => tops[index];

    public float Bottom(int index) => index + 1 < tops.Count ? tops[index + 1] : Total;

    /// <summary>Index du premier message visible à cette position de défilement.</summary>
    public int FirstVisible(float y)
    {
        if (tops.Count == 0)
            return 0;

        var low = 0;
        var high = tops.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (tops[mid] <= y)
                low = mid;
            else
                high = mid - 1;
        }

        return low;
    }
}
