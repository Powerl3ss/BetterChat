using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterChat.Core;

/// <summary>Préparation du texte saisi avant son envoi.</summary>
public static class ChatText
{
    /// <summary>Limite du jeu pour un message, en octets UTF-8 (une lettre accentuée en compte deux).</summary>
    public const int MaxBytes = 500;

    public static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>Remplace les sauts de ligne d'un texte collé, retire les caractères de contrôle et les espaces autour.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                    builder.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }
}

/// <summary>Les messages déjà envoyés, parcourus avec les flèches haut et bas.</summary>
public sealed class InputHistory
{
    private readonly List<string> entries = new();
    private readonly int limit;
    private int position = -1; // -1 : le brouillon en cours
    private string draft = string.Empty;

    public InputHistory(int limit = 100)
    {
        this.limit = limit;
    }

    public int Count => entries.Count;

    public void Add(string text)
    {
        ResetPosition();
        if (text.Length == 0 || (entries.Count > 0 && entries[^1] == text))
            return;

        entries.Add(text);
        if (entries.Count > limit)
            entries.RemoveAt(0);
    }

    public void ResetPosition()
    {
        position = -1;
        draft = string.Empty;
    }

    /// <summary>Message plus ancien. Le texte en cours est gardé pour y revenir. Null s'il n'y a rien de plus ancien.</summary>
    public string? Older(string current)
    {
        if (entries.Count == 0)
            return null;

        if (position == -1)
        {
            draft = current;
            position = entries.Count - 1;
            return entries[position];
        }

        if (position == 0)
            return null;

        return entries[--position];
    }

    /// <summary>Message plus récent, puis le brouillon. Null si on y est déjà.</summary>
    public string? Newer()
    {
        if (position == -1)
            return null;

        if (position < entries.Count - 1)
            return entries[++position];

        position = -1;
        return draft;
    }
}

/// <summary>Les canaux où écrire : commande correspondante et conversion depuis le numéro du jeu.</summary>
public static class ChatChannels
{
    private static readonly Dictionary<ChatKind, string> GameShortcuts = new();

    /// <summary>
    /// Les commandes d'un canal selon le jeu : elles changent avec sa langue.
    /// La première est celle affichée dans les menus.
    /// </summary>
    public static void SetCommands(ChatKind kind, IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
            return;

        foreach (var known in Prefixes.Where(p => p.Value == kind).Select(p => p.Key).ToList())
            Prefixes.Remove(known);

        foreach (var command in commands)
            Prefixes[command] = kind;

        GameShortcuts[kind] = commands[0];
    }

    /// <summary>Numéro du canal pour le jeu (RaptureShellModule.ChatType), ou -1 si on ne peut pas y écrire directement.</summary>
    public static int ToGameChannel(ChatKind kind)
    {
        if (kind >= ChatKind.Linkshell1 && kind <= ChatKind.Linkshell8)
            return 19 + (kind - ChatKind.Linkshell1);
        if (kind == ChatKind.CrossLinkshell1)
            return 9;
        if (kind >= ChatKind.CrossLinkshell2 && kind <= ChatKind.CrossLinkshell8)
            return 10 + (kind - ChatKind.CrossLinkshell2);

        return kind switch
        {
            ChatKind.Say => 1,
            ChatKind.Party => 2,
            ChatKind.Alliance => 3,
            ChatKind.Yell => 4,
            ChatKind.Shout => 5,
            ChatKind.FreeCompany => 6,
            ChatKind.PvpTeam => 7,
            ChatKind.NoviceNetwork => 8,
            _ => -1,
        };
    }

    /// <summary>Numéro de la linkshell (à partir de 0) pour ce canal. 0 pour les autres canaux.</summary>
    public static uint LinkshellIndex(ChatKind kind)
    {
        var channel = ToGameChannel(kind);
        return channel switch
        {
            >= 9 and <= 16 => (uint)(channel - 9),
            >= 19 and <= 26 => (uint)(channel - 19),
            _ => 0u,
        };
    }

    /// <summary>Commande affichée pour ce canal dans les menus, ou vide s'il n'y en a pas.</summary>
    public static string Command(ChatKind kind)
    {
        if (GameShortcuts.TryGetValue(kind, out var shortcut))
            return shortcut;

        if (kind >= ChatKind.Linkshell1 && kind <= ChatKind.Linkshell8)
            return $"/l{(int)kind - (int)ChatKind.Linkshell1 + 1}";
        if (kind == ChatKind.CrossLinkshell1)
            return "/cwl1";
        if (kind >= ChatKind.CrossLinkshell2 && kind <= ChatKind.CrossLinkshell8)
            return $"/cwl{(int)kind - (int)ChatKind.CrossLinkshell2 + 2}";

        return kind switch
        {
            ChatKind.Say => "/s",
            ChatKind.Yell => "/y",
            ChatKind.Shout => "/sh",
            ChatKind.Party => "/p",
            ChatKind.Alliance => "/a",
            ChatKind.FreeCompany => "/fc",
            ChatKind.NoviceNetwork => "/n",
            ChatKind.PvpTeam => "/pvpteam",
            _ => string.Empty,
        };
    }

    /// <summary>Canal actif du jeu (RaptureShellModule.ChatType) vers le type de message correspondant.</summary>
    public static ChatKind FromGameChannel(int channel) => channel switch
    {
        0 or 17 or 18 => ChatKind.TellOutgoing,
        1 => ChatKind.Say,
        2 => ChatKind.Party,
        3 => ChatKind.Alliance,
        4 => ChatKind.Yell,
        5 => ChatKind.Shout,
        6 => ChatKind.FreeCompany,
        7 => ChatKind.PvpTeam,
        8 => ChatKind.NoviceNetwork,
        >= 9 and <= 16 => ChatKinds.CrossLinkshell(channel - 8),
        >= 19 and <= 26 => ChatKinds.Linkshell(channel - 18),
        _ => ChatKind.Say,
    };

    private static readonly Dictionary<string, ChatKind> Prefixes = BuildPrefixes();

    private static Dictionary<string, ChatKind> BuildPrefixes()
    {
        var map = new Dictionary<string, ChatKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["/s"] = ChatKind.Say,
            ["/say"] = ChatKind.Say,
            ["/y"] = ChatKind.Yell,
            ["/yell"] = ChatKind.Yell,
            ["/sh"] = ChatKind.Shout,
            ["/shout"] = ChatKind.Shout,
            ["/p"] = ChatKind.Party,
            ["/party"] = ChatKind.Party,
            ["/a"] = ChatKind.Alliance,
            ["/alliance"] = ChatKind.Alliance,
            ["/fc"] = ChatKind.FreeCompany,
            ["/freecompany"] = ChatKind.FreeCompany,
            ["/n"] = ChatKind.NoviceNetwork,
            ["/novice"] = ChatKind.NoviceNetwork,
            ["/beginner"] = ChatKind.NoviceNetwork,
            ["/pvpteam"] = ChatKind.PvpTeam,
            ["/t"] = ChatKind.TellOutgoing,
            ["/tell"] = ChatKind.TellOutgoing,
            ["/r"] = ChatKind.TellOutgoing,
            ["/reply"] = ChatKind.TellOutgoing,
        };

        for (var i = 1; i <= 8; i++)
        {
            map[$"/l{i}"] = ChatKinds.Linkshell(i);
            map[$"/linkshell{i}"] = ChatKinds.Linkshell(i);
            map[$"/cwl{i}"] = ChatKinds.CrossLinkshell(i);
            map[$"/cwlinkshell{i}"] = ChatKinds.CrossLinkshell(i);
        }

        return map;
    }

    /// <summary>
    /// Le canal annoncé par le texte en cours de saisie (« /p salut » part dans le groupe).
    /// Null pour un message normal ou une autre commande.
    /// </summary>
    public static ChatKind? FromTypedPrefix(string text)
    {
        if (text.Length < 2 || text[0] != '/')
            return null;

        var space = text.IndexOf(' ');
        var command = space < 0 ? text : text[..space];
        return Prefixes.TryGetValue(command, out var kind) ? kind : null;
    }
}

/// <summary>Une commande proposée pendant la saisie.</summary>
public sealed record CommandSuggestion(string Command, string Description);

/// <summary>Propose les commandes qui commencent par ce qui est tapé, puis celles qui le contiennent.</summary>
public sealed class CommandSuggester
{
    private List<CommandSuggestion> commands = new();

    public int Count => commands.Count;

    public void SetCommands(IEnumerable<CommandSuggestion> list)
    {
        commands = list
            .Where(c => c.Command.Length > 1 && c.Command[0] == '/')
            .GroupBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Description.Length).First())
            .OrderBy(c => c.Command.Length)
            .ThenBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<CommandSuggestion> Suggest(string text, int max)
    {
        var result = new List<CommandSuggestion>(max);

        // Une fois l'espace tapé, on écrit les arguments de la commande : plus rien à proposer.
        if (text.Length < 2 || text[0] != '/' || text.Contains(' '))
            return result;

        foreach (var command in commands)
        {
            if (result.Count >= max)
                return result;
            if (command.Command.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                result.Add(command);
        }

        var inside = text[1..];
        foreach (var command in commands)
        {
            if (result.Count >= max)
                return result;
            if (!command.Command.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
                command.Command.Contains(inside, StringComparison.OrdinalIgnoreCase))
                result.Add(command);
        }

        return result;
    }
}

/// <summary>Détecte ton nom et les mots que tu surveilles, seulement en mots entiers.</summary>
public sealed class MentionMatcher
{
    private List<string> words = new();

    public void SetWords(IEnumerable<string> list)
    {
        words = list.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Matches(string text)
    {
        foreach (var word in words)
        {
            var start = 0;
            while (start <= text.Length - word.Length)
            {
                var found = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;

                var end = found + word.Length;
                var startsWord = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
                var endsWord = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (startsWord && endsWord)
                    return true;

                start = found + 1;
            }
        }

        return false;
    }
}

/// <summary>Repère les adresses web dans le texte, pour les rendre cliquables.</summary>
public static partial class UrlFinder
{
    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""\uE000-\uF8FF]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Découpe les morceaux de texte sans lien autour des adresses qu'ils contiennent.</summary>
    public static List<ChatSpan> Split(List<ChatSpan> spans)
    {
        var result = new List<ChatSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Link != null || !span.Text.Contains('.'))
            {
                result.Add(span);
                continue;
            }

            var position = 0;
            foreach (Match match in UrlRegex().Matches(span.Text))
            {
                // La ponctuation à la fin d'une phrase ne fait pas partie de l'adresse.
                var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'');
                if (url.Length == 0)
                    continue;

                if (match.Index > position)
                    result.Add(new ChatSpan(span.Text[position..match.Index], span.Color, span.Italic));

                var target = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
                result.Add(new ChatSpan(url, span.Color, span.Italic, ChatLink.Url(target)));
                position = match.Index + url.Length;
            }

            if (position == 0)
                result.Add(span);
            else if (position < span.Text.Length)
                result.Add(new ChatSpan(span.Text[position..], span.Color, span.Italic));
        }

        return result;
    }
}

/// <summary>Les derniers correspondants en message privé, du plus récent au plus ancien.</summary>
public sealed class RecentTells
{
    private const int Limit = 8;
    private readonly List<ChatLink> people = new();

    public IReadOnlyList<ChatLink> People => people;

    public void Note(ChatLink person)
    {
        people.RemoveAll(p => string.Equals(p.TellTarget, person.TellTarget, StringComparison.OrdinalIgnoreCase));
        people.Insert(0, person);
        if (people.Count > Limit)
            people.RemoveAt(people.Count - 1);
    }

    public void Clear() => people.Clear();
}
