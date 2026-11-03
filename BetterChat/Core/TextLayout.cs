using System;
using System.Collections.Generic;
using System.Text;

namespace BetterChat.Core;

/// <summary>Un bout de texte placé sur une ligne.</summary>
/// <param name="SpanIndex">Le morceau d'origine (donne la couleur et le lien).</param>
/// <param name="X">Position depuis le bord gauche de la zone de texte.</param>
public readonly record struct TextRun(int SpanIndex, string Text, float X, int Line, float Width);

public sealed class LaidOutText
{
    public static readonly LaidOutText Empty = new(new List<TextRun>(), 0);

    public LaidOutText(List<TextRun> runs, int lineCount)
    {
        Runs = runs;
        LineCount = lineCount;
    }

    public List<TextRun> Runs { get; }
    public int LineCount { get; }
}

/// <summary>
/// Coupe un texte multicolore en lignes. La mesure du texte vient de l'extérieur : l'algorithme se teste sans le jeu.
/// Les lignes se coupent aux espaces, et n'importe où dans un texte japonais ou un mot trop long.
/// </summary>
public static class TextLayout
{
    /// <param name="maxWidth">Largeur de la zone de texte.</param>
    /// <param name="startX">Départ de la première ligne (après un préfixe, par exemple).</param>
    /// <param name="wrapIndent">Retrait des lignes suivantes.</param>
    public static LaidOutText Wrap(IReadOnlyList<ChatSpan> spans, float maxWidth, Func<string, float> measure, float startX = 0f, float wrapIndent = 0f)
    {
        var runs = new List<TextRun>();
        var x = startX;
        var line = 0;
        wrapIndent = Math.Min(wrapIndent, maxWidth * 0.5f);
        maxWidth = Math.Max(maxWidth, 1f);

        // Morceau en cours sur la ligne, pour dessiner en une fois les mots consécutifs de même couleur.
        var pending = new StringBuilder();
        var pendingX = x;

        void FlushPending(int spanIndex)
        {
            if (pending.Length == 0)
                return;

            var text = pending.ToString();
            runs.Add(new TextRun(spanIndex, text, pendingX, line, measure(text)));
            pending.Clear();
        }

        void NewLine(int spanIndex)
        {
            FlushPending(spanIndex);
            line++;
            x = wrapIndent;
            pendingX = x;
        }

        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            pendingX = x;
            var text = spans[spanIndex].Text;
            var position = 0;

            while (position < text.Length)
            {
                if (text[position] == '\n')
                {
                    NewLine(spanIndex);
                    position++;
                    continue;
                }

                var end = TokenEnd(text, position);
                var word = text.Substring(position, end - position);
                var trimmed = word.TrimEnd(' ');
                var trimmedWidth = measure(trimmed);

                if (x + trimmedWidth <= maxWidth)
                {
                    pending.Append(word);
                    x += measure(word);
                    position = end;
                    continue;
                }

                // Le mot ne rentre pas. Sur une ligne déjà entamée, on passe à la suivante et on réessaie.
                if (x > wrapIndent)
                {
                    NewLine(spanIndex);
                    continue;
                }

                // Le mot est plus large que la ligne entière : on le coupe au nombre de caractères qui tiennent.
                var fit = FitCharacters(trimmed, maxWidth - x, measure);
                pending.Append(trimmed, 0, fit);
                x += measure(trimmed[..fit]);
                position += fit;

                if (position < text.Length && text[position] != '\n')
                    NewLine(spanIndex);
            }

            FlushPending(spanIndex);
        }

        return new LaidOutText(runs, line + 1);
    }

    /// <summary>Fin du mot qui commence ici, espaces suivants compris.</summary>
    private static int TokenEnd(string text, int start)
    {
        var i = start;

        // Chaque idéogramme ou kana est un mot : une ligne peut se couper entre deux.
        if (IsCjk(text[i]))
        {
            i++;
        }
        else
        {
            while (i < text.Length && text[i] != ' ' && text[i] != '\n' && !IsCjk(text[i]))
                i++;
        }

        while (i < text.Length && text[i] == ' ')
            i++;

        return i;
    }

    private static int FitCharacters(string word, float width, Func<string, float> measure)
    {
        var low = 1; // au moins un caractère, pour toujours avancer
        var high = word.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (measure(word[..mid]) <= width)
                low = mid;
            else
                high = mid - 1;
        }

        // Ne jamais couper une paire de surrogates (un seul caractère, sur deux valeurs).
        if (low < word.Length && char.IsLowSurrogate(word[low]))
            low = low > 1 ? low - 1 : low + 1;

        return Math.Min(low, word.Length);
    }

    private static bool IsCjk(char c) =>
        c is >= '　' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
}
