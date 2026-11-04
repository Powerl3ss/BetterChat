using System;
using System.Collections.Generic;
using System.Text;

namespace BetterChat.Core;

/// <summary>
/// Les petites icônes du jeu dans les messages. Chacune voyage dans le texte sous la forme d'un caractère réservé :
/// elle se coupe et se mesure comme une lettre, et reste en place dans l'historique enregistré.
/// </summary>
public static class GameIcons
{
    private const char First = '\uF200';
    private const char Last = '\uF7FF';

    /// <summary>Le caractère d'une icône. Null si son numéro ne tient pas dans la plage réservée.</summary>
    public static char? ToChar(uint iconId) => iconId != 0 && iconId <= Last - First ? (char)(First + iconId) : null;

    public static bool IsIcon(char c) => c is >= First and <= Last;

    public static uint IdOf(char c) => (uint)(c - First);

    public static bool Contains(string text)
    {
        foreach (var c in text)
        {
            if (IsIcon(c))
                return true;
        }

        return false;
    }

    /// <summary>Le texte sans ses icônes, pour le copier ou y chercher.</summary>
    public static string Strip(string text)
    {
        if (!Contains(text))
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsIcon(c))
                builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>Où se trouve une icône dans l'image du jeu.</summary>
public readonly record struct GameIconEntry(int Left, int Top, int Width, int Height);

/// <summary>La table des icônes du jeu (fichier <c>gfdata.gfd</c>) : un en-tête de 16 octets, puis 16 octets par icône.</summary>
public sealed class GameIconTable
{
    private const int HeaderSize = 16;
    private const int EntrySize = 16;

    private readonly Dictionary<uint, GameIconEntry> entries = new();

    public static GameIconTable Parse(ReadOnlySpan<byte> file)
    {
        var table = new GameIconTable();
        if (file.Length < HeaderSize)
            return table;

        var count = BitConverter.ToInt32(file[8..12]);
        var redirects = new Dictionary<uint, uint>();

        for (var i = 0; i < count && HeaderSize + (i + 1) * EntrySize <= file.Length; i++)
        {
            var entry = file.Slice(HeaderSize + i * EntrySize, EntrySize);
            var id = BitConverter.ToUInt16(entry[0..2]);
            var redirect = BitConverter.ToUInt16(entry[12..14]);
            if (redirect != 0)
                redirects[id] = redirect;

            var icon = new GameIconEntry(
                BitConverter.ToUInt16(entry[2..4]),
                BitConverter.ToUInt16(entry[4..6]),
                BitConverter.ToUInt16(entry[6..8]),
                BitConverter.ToUInt16(entry[8..10]));

            if (icon.Width > 0 && icon.Height > 0)
                table.entries[id] = icon;
        }

        // Certaines icônes renvoient simplement vers une autre.
        foreach (var (id, target) in redirects)
        {
            if (table.entries.TryGetValue(target, out var icon))
                table.entries[id] = icon;
        }

        return table;
    }

    public bool TryGet(uint iconId, out GameIconEntry entry) => entries.TryGetValue(iconId, out entry);
}
