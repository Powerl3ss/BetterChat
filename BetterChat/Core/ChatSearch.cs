using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BetterChat.Core;

/// <summary>Texte ramené à sa forme la plus simple : minuscules et sans accents. « Épée » et « epee » se retrouvent.</summary>
public static class TextFold
{
    public static string Of(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            builder.Append(c switch
            {
                'à' or 'â' or 'ä' or 'á' or 'ã' => 'a',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'î' or 'ï' or 'í' or 'ì' => 'i',
                'ô' or 'ö' or 'ó' or 'ò' or 'õ' => 'o',
                'ù' or 'û' or 'ü' or 'ú' => 'u',
                'ÿ' or 'ý' => 'y',
                'ç' => 'c',
                'ñ' => 'n',
                '’' => '\'',
                _ => c,
            });
        }

        return builder.ToString();
    }
}

/// <summary>
/// La recherche d'un onglet. Des mots simples cherchent dans le nom et le texte, et trois filtres peuvent s'y ajouter :
/// <c>de:Jean</c>, <c>canal:CL</c>, <c>date:hier</c>. Des guillemets gardent une expression entière :
/// <c>de:"Jean Dupont"</c>. Tout ce qui est demandé doit correspondre.
/// </summary>
public sealed class ChatSearch
{
    public static readonly ChatSearch Empty = new();

    private readonly List<string> words = new();
    private readonly List<string> authors = new();
    private readonly List<string> channels = new();
    private DateTime? date;

    public bool IsEmpty => words.Count == 0 && authors.Count == 0 && channels.Count == 0 && date == null;

    /// <param name="today">Date du jour, pour comprendre « hier » ou une date sans année.</param>
    public static ChatSearch Parse(string text, DateTime today)
    {
        var search = new ChatSearch();
        foreach (var (prefix, value) in Tokens(text))
        {
            var folded = TextFold.Of(value);
            switch (prefix)
            {
                case "de" or "from" when folded.Length > 0:
                    search.authors.Add(folded);
                    break;

                case "canal" or "channel" when folded.Length > 0:
                    search.channels.Add(folded);
                    break;

                case "date" when ParseDate(folded, today) is { } day:
                    search.date = day;
                    break;

                case null:
                    if (folded.Length > 0)
                        search.words.Add(folded);
                    break;

                default:
                    // Un préfixe inconnu ou une date illisible restent un mot ordinaire : « 12:30 » se cherche tel quel.
                    search.words.Add(TextFold.Of($"{prefix}:{value}"));
                    break;
            }
        }

        return search;
    }

    public bool Matches(ChatLine line)
    {
        if (date is { } day && line.TimeUtc.ToLocalTime().Date != day)
            return false;

        if (authors.Count > 0)
        {
            var author = line.FoldedAuthor;
            if (!authors.All(a => author.Contains(a, StringComparison.Ordinal)))
                return false;
        }

        if (channels.Count > 0 && !channels.All(c => ChatKinds.SearchNames(line.Kind).Any(n => n.Contains(c, StringComparison.Ordinal))))
            return false;

        if (words.Count > 0)
        {
            var text = line.FoldedText;
            if (!words.All(w => text.Contains(w, StringComparison.Ordinal)))
                return false;
        }

        return true;
    }

    /// <summary>Découpe aux espaces, en respectant les guillemets et en séparant un éventuel préfixe (<c>de:</c>).</summary>
    private static IEnumerable<(string? Prefix, string Value)> Tokens(string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            string? prefix = null;
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != ':' && text[position] != '"')
                position++;

            if (position < text.Length && text[position] == ':' && position > start)
            {
                prefix = text[start..position].ToLowerInvariant();
                position++;
                start = position;
            }
            else
            {
                position = start;
            }

            string value;
            if (position < text.Length && text[position] == '"')
            {
                var close = text.IndexOf('"', position + 1);
                var end = close < 0 ? text.Length : close;
                value = text[(position + 1)..end];
                position = close < 0 ? text.Length : close + 1;
            }
            else
            {
                start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]))
                    position++;
                value = text[start..position];
            }

            yield return (prefix, value);
        }
    }

    private static DateTime? ParseDate(string value, DateTime today)
    {
        switch (value)
        {
            case "aujourd'hui" or "aujourdhui" or "today":
                return today.Date;
            case "hier" or "yesterday":
                return today.Date.AddDays(-1);
        }

        if (DateTime.TryParseExact(value, new[] { "d/M/yyyy", "yyyy-M-d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            return full.Date;

        // Sans année : la dernière fois que cette date est arrivée.
        if (DateTime.TryParseExact($"{value}/{today.Year}", "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var thisYear))
            return thisYear.Date > today.Date ? thisYear.Date.AddYears(-1) : thisYear.Date;

        return null;
    }
}

/// <summary>Masque les messages de joueurs qui contiennent un mot ou une expression choisis (vente de gils, publicités...).</summary>
public sealed class SpamFilter
{
    private List<string> rules = new();

    public void SetRules(IEnumerable<string> list)
    {
        rules = list.Select(r => TextFold.Of(r.Trim())).Where(r => r.Length > 0).Distinct().ToList();
    }

    public bool Matches(ChatLine line)
    {
        if (rules.Count == 0 || !ChatKinds.Info(line.Kind).HasAuthor)
            return false;

        var body = line.FoldedBody;
        foreach (var rule in rules)
        {
            if (body.Contains(rule, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>Ce qui s'affiche au-dessus d'un message : le repère des nouveaux messages et le changement de date.</summary>
public readonly record struct LineSeparators(bool NewMessages, DateTime? Day)
{
    public bool Any => NewMessages || Day != null;

    /// <param name="firstUnreadId">Premier message arrivé pendant que l'onglet n'était pas affiché.</param>
    public static LineSeparators Between(ChatLine? previous, ChatLine line, long? firstUnreadId, DateTime today)
    {
        var day = line.TimeUtc.ToLocalTime().Date;
        DateTime? shownDay = previous == null
            ? (day != today.Date ? day : null) // en haut de la liste, seulement si ce n'est pas aujourd'hui
            : (previous.TimeUtc.ToLocalTime().Date != day ? day : null);

        return new LineSeparators(firstUnreadId == line.Id, shownDay);
    }
}

public static class ChatDates
{
    private static readonly string[] Days = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };

    private static readonly string[] Months =
    {
        "janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre",
    };

    /// <summary>« Aujourd'hui », « Hier » ou « Mardi 3 novembre » (avec l'année si ce n'est pas la même).</summary>
    public static string Label(DateTime day, DateTime today)
    {
        if (day.Date == today.Date)
            return "Aujourd'hui";
        if (day.Date == today.Date.AddDays(-1))
            return "Hier";

        var name = Days[(int)day.DayOfWeek];
        var text = $"{char.ToUpperInvariant(name[0])}{name[1..]} {day.Day}{(day.Day == 1 ? "er" : string.Empty)} {Months[day.Month - 1]}";
        return day.Year == today.Year ? text : $"{text} {day.Year}";
    }
}

/// <summary>Une conversation en fichier texte, sans les symboles propres au jeu qui ne s'afficheraient pas ailleurs.</summary>
public static class ChatExporter
{
    public static string Format(IEnumerable<ChatLine> lines, Func<ChatLine, int> repeatCount)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            builder.Append(" [").Append(ChatKinds.Info(line.Kind).Tag).Append("] ");

            var sender = Clean(line.SenderText);
            if (sender.Length > 0)
                builder.Append(sender).Append(ChatGrouping.Separator(line.Kind));

            builder.Append(Clean(line.BodyText));

            var repeats = repeatCount(line);
            if (repeats > 1)
                builder.Append(" (x").Append(repeats).Append(')');

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is < '\uE000' or > '\uF8FF')
                builder.Append(c == '\n' ? ' ' : c);
        }

        return builder.ToString().Trim();
    }
}

/// <summary>Remplace les raccourcis du jeu (<c>&lt;t&gt;</c>, <c>&lt;flag&gt;</c>...) pour montrer ce qui partira vraiment.</summary>
public static class MacroPreview
{
    /// <param name="resolve">Texte d'un raccourci sans ses chevrons, en minuscules. Null : raccourci inconnu, laissé tel quel.</param>
    /// <param name="changed">Vrai si au moins un raccourci a été remplacé.</param>
    public static string Expand(string text, Func<string, string?> resolve, out bool changed)
    {
        changed = false;
        if (!text.Contains('<'))
            return text;

        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('<', position);
            var close = open < 0 ? -1 : text.IndexOf('>', open + 1);
            if (close < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, open - position);
            var replacement = resolve(text[(open + 1)..close].ToLowerInvariant());
            if (replacement == null)
            {
                builder.Append(text, open, close - open + 1);
            }
            else
            {
                builder.Append(replacement);
                changed = true;
            }

            position = close + 1;
        }

        return builder.ToString();
    }
}
