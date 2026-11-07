using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterChat.Core;

// ------------------------------------------------------------------ Autotraduction

/// <summary>Un mot ou une phrase de l'autotraduction du jeu.</summary>
public sealed record AutoTranslateEntry(uint Group, uint Key, string Text, string Title)
{
    public string Folded { get; } = TextFold.Of(Text);
    public string FoldedTitle { get; } = TextFold.Of(Title);
}

/// <summary>
/// Dans la saisie, un mot de l'autotraduction est entouré des deux flèches vertes du jeu. Il redevient le vrai
/// lien d'autotraduction à l'envoi, et reste lisible en attendant.
/// </summary>
public static class AutoTranslateText
{
    public const char Open = '';
    public const char Close = '';

    public readonly record struct Part(string Text, bool IsEntry);

    public static string Wrap(string text) => $"{Open}{text}{Close}";

    public static bool Contains(string text) => text.Contains(Open);

    /// <summary>Le message coupé entre texte normal et mots de l'autotraduction. Une flèche seule reste du texte.</summary>
    public static List<Part> Split(string text)
    {
        var parts = new List<Part>();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf(Open, position);
            var close = open < 0 ? -1 : text.IndexOf(Close, open + 1);
            if (close < 0)
            {
                parts.Add(new Part(text[position..], false));
                break;
            }

            if (open > position)
                parts.Add(new Part(text[position..open], false));
            parts.Add(new Part(text[(open + 1)..close], true));
            position = close + 1;
        }

        return parts;
    }

    /// <summary>
    /// Le dernier mot tapé, celui que l'autotraduction remplace. Vide après un espace, dans une commande ou juste
    /// après un mot déjà choisi.
    /// </summary>
    public static (int Start, string Word) LastWord(string input)
    {
        if (input.Length == 0 || char.IsWhiteSpace(input[^1]))
            return (input.Length, string.Empty);

        var start = input.Length;
        while (start > 0 && !char.IsWhiteSpace(input[start - 1]))
            start--;

        var word = input[start..];
        if ((start == 0 && word.StartsWith('/')) || word.Contains(Open) || word.Contains(Close))
            return (input.Length, string.Empty);

        return (start, word);
    }
}

/// <summary>Où l'autotraduction prend ses mots : une feuille du jeu, certaines lignes, certaines colonnes.</summary>
public sealed record CompletionLookup(string Sheet, IReadOnlyList<(uint First, uint Last)> Rows, IReadOnlyList<int> Columns)
{
    /// <summary>Lit par exemple <c>Mount[1-200,col-0]</c>. Null : pas de feuille (le mot est dans la ligne elle-même).</summary>
    public static CompletionLookup? Parse(string lookup)
    {
        lookup = lookup.Replace(" ", string.Empty);
        if (lookup.Length == 0 || lookup == "@")
            return null;

        var open = lookup.IndexOf('[');
        var sheet = open < 0 ? lookup : lookup[..open];
        if (sheet.Length == 0)
            return null;

        var rows = new List<(uint, uint)>();
        var columns = new List<int>();
        if (open >= 0)
        {
            var close = lookup.IndexOf(']', open);
            if (close < 0)
                return null;

            foreach (var part in lookup[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = part.IndexOf('-');
                if (part == "noun")
                    continue;
                if (part.StartsWith("col-", StringComparison.Ordinal) && int.TryParse(part[4..], out var column))
                    columns.Add(column);
                else if (dash > 0 && uint.TryParse(part[..dash], out var first) && uint.TryParse(part[(dash + 1)..], out var last))
                    rows.Add((first, last));
                else if (uint.TryParse(part, out var single))
                    rows.Add((single, single));
                else
                    return null;
            }
        }

        if (columns.Count == 0)
            columns.Add(0);

        return new CompletionLookup(sheet, rows, columns);
    }
}

public static class AutoTranslateSearch
{
    /// <summary>Le mot exact d'abord, puis ceux qui commencent par la recherche, puis ceux qui la contiennent.</summary>
    public static List<AutoTranslateEntry> Find(IReadOnlyList<AutoTranslateEntry> entries, string query, int limit)
    {
        var folded = TextFold.Of(query.Trim());
        if (folded.Length == 0)
            return new List<AutoTranslateEntry>();

        var whole = new List<AutoTranslateEntry>();
        var prefix = new List<AutoTranslateEntry>();
        var inside = new List<AutoTranslateEntry>();
        var byTitle = new List<AutoTranslateEntry>();

        foreach (var entry in entries)
        {
            if (entry.Folded == folded)
                whole.Add(entry);
            else if (entry.Folded.StartsWith(folded, StringComparison.Ordinal))
                prefix.Add(entry);
            else if (entry.Folded.Contains(folded, StringComparison.Ordinal))
                inside.Add(entry);
            else if (entry.FoldedTitle.Contains(folded, StringComparison.Ordinal))
                byTitle.Add(entry);
        }

        static IEnumerable<AutoTranslateEntry> Sorted(List<AutoTranslateEntry> list) =>
            list.OrderBy(e => e.Text.Length).ThenBy(e => e.Folded, StringComparer.Ordinal);

        return Sorted(whole).Concat(Sorted(prefix)).Concat(Sorted(inside)).Concat(Sorted(byTitle)).Take(limit).ToList();
    }
}

// ------------------------------------------------------------------ Messages privés par personne

public sealed class TellConversation
{
    public string Key { get; init; } = string.Empty;
    public ChatLink Person { get; set; } = ChatLink.Player(string.Empty, string.Empty);
    public DateTime LastUtc { get; set; }
    public int Unread { get; set; }
}

/// <summary>Les personnes avec qui tu échanges des messages privés, la plus récente en premier.</summary>
public sealed class TellConversations
{
    public const int Limit = 10;

    private readonly List<TellConversation> list = new();

    public IReadOnlyList<TellConversation> All => list;

    /// <summary>La personne d'un message privé : l'auteur pour un message reçu, le destinataire pour un envoyé.</summary>
    public static string? KeyOf(ChatLine line) =>
        ChatKinds.IsTell(line.Kind) && line.Author is { Text.Length: > 0 } person ? person.Text.ToLowerInvariant() : null;

    /// <param name="seen">Le message est déjà à l'écran : il ne compte pas comme non lu.</param>
    public void Note(ChatLine line, bool seen)
    {
        if (KeyOf(line) is not { } key)
            return;

        var index = list.FindIndex(c => c.Key == key);
        var conversation = index >= 0 ? list[index] : new TellConversation { Key = key };
        if (index >= 0)
            list.RemoveAt(index);

        // Un message envoyé ne donne pas toujours le monde : on garde celui qu'on connaît.
        if (line.Author!.World.Length > 0 || conversation.Person.Text.Length == 0)
            conversation.Person = line.Author;

        conversation.LastUtc = line.TimeUtc;
        if (line.Kind == ChatKind.TellIncoming && !line.FromHistory && !seen)
            conversation.Unread++;

        list.Insert(0, conversation);
        if (list.Count > Limit)
            list.RemoveAt(list.Count - 1);
    }

    public void MarkRead(string key)
    {
        foreach (var conversation in list.Where(c => c.Key == key))
            conversation.Unread = 0;
    }

    public void Remove(string key) => list.RemoveAll(c => c.Key == key);

    public void Clear() => list.Clear();
}

// ------------------------------------------------------------------ Messages épinglés

/// <summary>Une copie du message : il reste épinglé même quand le chat l'a oublié.</summary>
public sealed class PinnedMessage
{
    public const int LimitPerTab = 10;

    public DateTime TimeUtc { get; set; }
    public ChatKind Kind { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    public string CopyText => Sender.Length > 0 ? $"{Sender}: {Text}" : Text;

    public static PinnedMessage Of(ChatLine line) => new()
    {
        TimeUtc = line.TimeUtc,
        Kind = line.Kind,
        Sender = line.ShowsSender ? GameIcons.Strip(line.SenderText).Trim() : string.Empty,
        Text = GameIcons.Strip(line.BodyText).Trim(),
    };

    public bool Is(ChatLine line) =>
        line.TimeUtc == TimeUtc && line.Kind == Kind && GameIcons.Strip(line.BodyText).Trim() == Text;
}

// ------------------------------------------------------------------ Disposition selon le contexte

public enum ChatContext
{
    Elsewhere,
    Dungeon,
    Raid,
    Town,
}

/// <summary>Ce qui change dans un contexte : l'onglet affiché et les canaux masqués.</summary>
public sealed class ContextRule
{
    /// <summary>Identifiant de l'onglet à afficher. Vide : ne pas changer d'onglet.</summary>
    public string TabId { get; set; } = string.Empty;

    public List<ChatSection> Hidden { get; set; } = new();
}

public static class ChatContexts
{
    public static readonly ChatContext[] Configurable = { ChatContext.Dungeon, ChatContext.Raid, ChatContext.Town };

    /// <summary>
    /// D'après la zone : le type de mission (feuille ContentType : 2 donjons, 21 donjons sans fond, 30 donjons
    /// V&amp;C, 5 raids, 28 fatals, 37 raids chaotiques), sinon l'usage de la zone (0 ville, 2 auberge).
    /// </summary>
    public static ChatContext Classify(uint intendedUse, uint contentType) => contentType switch
    {
        2 or 21 or 30 => ChatContext.Dungeon,
        5 or 28 or 37 => ChatContext.Raid,
        _ => intendedUse is 0 or 2 ? ChatContext.Town : ChatContext.Elsewhere,
    };

    public static string Label(ChatContext context) => context switch
    {
        ChatContext.Dungeon => "En donjon",
        ChatContext.Raid => "En raid",
        ChatContext.Town => "En ville",
        _ => "Ailleurs",
    };

    /// <summary>Les canaux d'un ensemble de sections masquées.</summary>
    public static HashSet<ChatKind> HiddenKinds(IEnumerable<ChatSection> sections) =>
        sections.SelectMany(ChatKinds.InSection).ToHashSet();
}
