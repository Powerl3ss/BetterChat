using System;
using System.Collections.Generic;
using System.Text;

namespace BetterChat.Core;

/// <summary>Le dessin réel d'une icône dans son image, en fractions de l'image : (0, 0, 1, 1) pour l'image entière.</summary>
public readonly record struct IconInk(float Left, float Top, float Right, float Bottom)
{
    public static readonly IconInk Full = new(0f, 0f, 1f, 1f);

    private const byte MinAlpha = 128;
    private const byte MinBrightness = 80;

    /// <summary>
    /// Cherche le dessin dans une image BGRA. Les icônes ont des marges et une ombre sombre qui varient :
    /// seuls les pixels nets et clairs comptent, pour les centrer sur ce que l'œil voit vraiment.
    /// </summary>
    public static IconInk Find(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return Full;

        int left = width, right = -1, top = height, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (bgra[i + 3] < MinAlpha || Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) < MinBrightness)
                    continue;

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < 0
            ? Full
            : new IconInk(left / (float)width, top / (float)height, (right + 1) / (float)width, (bottom + 1) / (float)height);
    }
}

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

/// <summary>Retrouver et extraire un morceau de texte déjà mis en page, pour la sélection à la souris.</summary>
public static class TextSelection
{
    /// <summary>Position dans le texte (en caractères depuis le début) sous un point donné, au plus près.</summary>
    /// <param name="line">Numéro de la ligne affichée.</param>
    /// <param name="x">Position depuis le bord gauche de la zone de texte.</param>
    public static int OffsetAt(LaidOutText text, int line, float x, Func<string, float> measure)
    {
        var offset = 0;
        foreach (var run in text.Runs)
        {
            if (run.Line > line)
                return offset;

            if (run.Line == line)
            {
                if (x < run.X)
                    return offset;

                if (x <= run.X + run.Width)
                    return offset + CharacterAt(run.Text, x - run.X, measure);
            }

            offset += run.Text.Length;
        }

        return offset;
    }

    private static int CharacterAt(string text, float x, Func<string, float> measure)
    {
        var previous = 0f;
        for (var i = 1; i <= text.Length; i++)
        {
            var width = measure(text[..i]);
            if (x < (previous + width) / 2f)
                return i - 1;
            previous = width;
        }

        return text.Length;
    }

    /// <summary>Le texte entre deux positions, sans un éventuel morceau à ignorer (la place réservée à une icône).</summary>
    public static string Extract(LaidOutText text, int start, int end, int skipSpan = -1)
    {
        var builder = new StringBuilder();
        var offset = 0;
        foreach (var run in text.Runs)
        {
            var from = Math.Max(start - offset, 0);
            var to = Math.Min(end - offset, run.Text.Length);
            if (from < to && run.SpanIndex != skipSpan)
                builder.Append(run.Text, from, to - from);

            offset += run.Text.Length;
        }

        return GameIcons.Strip(builder.ToString());
    }

    /// <summary>La partie surlignée d'un bout de texte. Null s'il est hors de la sélection.</summary>
    /// <param name="runStart">Position de ce bout de texte dans le message.</param>
    public static (float Left, float Right)? Highlight(TextRun run, int runStart, int start, int end, Func<string, float> measure)
    {
        var from = Math.Max(start - runStart, 0);
        var to = Math.Min(end - runStart, run.Text.Length);
        if (from >= to)
            return null;

        return (run.X + measure(run.Text[..from]), run.X + measure(run.Text[..to]));
    }
}
