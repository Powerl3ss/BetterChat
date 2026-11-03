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

/// <summary>Le dessin d'un caractère par rapport à sa position d'écriture.</summary>
public readonly record struct GlyphBox(float Left, float Right);

/// <summary>
/// Place les chiffres d'une pastille. La police ne donne pas la même largeur à tous les chiffres : chacun est donc
/// centré dans une case de largeur fixe, et « 11 » ou « 42 » restent réguliers. Les positions sont des pixels entiers.
/// </summary>
public sealed class BadgeLayout
{
    private BadgeLayout(float[] penX, float inkMiddle, float width)
    {
        PenX = penX;
        InkMiddle = inkMiddle;
        Width = width;
    }

    /// <summary>Position d'écriture de chaque caractère.</summary>
    public float[] PenX { get; }

    /// <summary>Milieu réel de ce qui est dessiné : c'est sur lui que la pastille se centre.</summary>
    public float InkMiddle { get; }

    public float Width { get; }

    /// <param name="cellWidth">Largeur d'une case, celle du chiffre le plus large.</param>
    /// <param name="gap">Espace entre deux cases.</param>
    /// <param name="padding">Marge entre les chiffres et le bord de la pastille.</param>
    /// <param name="minWidth">Largeur minimale : la hauteur, pour qu'un seul chiffre donne un rond.</param>
    public static BadgeLayout Compute(IReadOnlyList<GlyphBox> glyphs, float cellWidth, float gap, float padding, float minWidth)
    {
        var pens = new float[glyphs.Count];
        var inkLeft = float.MaxValue;
        var inkRight = float.MinValue;

        for (var i = 0; i < glyphs.Count; i++)
        {
            var box = glyphs[i];
            var inkWidth = box.Right - box.Left;
            var cellLeft = i * (cellWidth + gap);
            var pen = MathF.Round(cellLeft + (cellWidth - inkWidth) / 2f - box.Left);

            pens[i] = pen;
            inkLeft = Math.Min(inkLeft, pen + box.Left);
            inkRight = Math.Max(inkRight, pen + box.Right);
        }

        if (glyphs.Count == 0)
            return new BadgeLayout(pens, 0f, minWidth);

        return new BadgeLayout(pens, (inkLeft + inkRight) / 2f, Math.Max(minWidth, inkRight - inkLeft + padding * 2f));
    }
}
