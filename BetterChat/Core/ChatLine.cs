using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace BetterChat.Core;

public enum LinkKind : byte
{
    None = 0,
    Player = 1,
    Item = 2,
    Map = 3,
    Url = 4,
}

/// <summary>Ce qui se passe au clic sur un morceau de texte. Une seule classe, simple à enregistrer sur le disque.</summary>
public sealed class ChatLink
{
    public LinkKind Kind { get; init; }

    /// <summary>Joueur : son nom. Lien web : l'adresse. Carte : le texte affiché.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Joueur : son monde, s'il est connu.</summary>
    public string World { get; init; } = string.Empty;

    /// <summary>Objet : son identifiant. Carte : le territoire.</summary>
    public uint Id { get; init; }

    public uint MapId { get; init; }
    public int RawX { get; init; }
    public int RawY { get; init; }

    public static ChatLink Player(string name, string world) => new() { Kind = LinkKind.Player, Text = name, World = world };
    public static ChatLink Item(uint id, string name) => new() { Kind = LinkKind.Item, Id = id, Text = name };
    public static ChatLink Map(uint territory, uint map, int rawX, int rawY, string label) =>
        new() { Kind = LinkKind.Map, Id = territory, MapId = map, RawX = rawX, RawY = rawY, Text = label };
    public static ChatLink Url(string url) => new() { Kind = LinkKind.Url, Text = url };

    /// <summary>Destinataire de « /tell », avec le monde s'il est connu.</summary>
    [JsonIgnore]
    public string TellTarget => World.Length > 0 ? $"{Text}@{World}" : Text;
}

/// <summary>Un morceau de texte d'une seule couleur.</summary>
public sealed class ChatSpan
{
    public string Text { get; init; } = string.Empty;

    /// <summary>Couleur RGBA imposée par le jeu. 0 : couleur du canal.</summary>
    public uint Color { get; init; }

    public bool Italic { get; init; }
    public ChatLink? Link { get; init; }

    public ChatSpan()
    {
    }

    public ChatSpan(string text, uint color = 0, bool italic = false, ChatLink? link = null)
    {
        Text = text;
        Color = color;
        Italic = italic;
        Link = link;
    }
}

/// <summary>Un message du chat.</summary>
public sealed class ChatLine
{
    /// <summary>Numéro croissant dans la session. Il n'est pas enregistré : un message relu en reçoit un nouveau.</summary>
    [JsonIgnore]
    public long Id { get; set; }

    public DateTime TimeUtc { get; init; }
    public ChatKind Kind { get; init; }
    public ChatSource Source { get; init; }

    public List<ChatSpan> Sender { get; init; } = new();
    public List<ChatSpan> Body { get; init; } = new();

    /// <summary>Le joueur à l'origine du message, s'il est connu.</summary>
    public ChatLink? Author { get; init; }

    /// <summary>Le message cite ton nom ou un mot surveillé.</summary>
    public bool Mentioned { get; set; }

    /// <summary>Relu depuis le disque : il ne compte pas comme non lu et ne fait pas de bruit.</summary>
    [JsonIgnore]
    public bool FromHistory { get; set; }

    /// <summary>Mise en page mémorisée par l'affichage.</summary>
    [JsonIgnore]
    public object? LayoutCache { get; set; }

    private string? senderText;
    private string? bodyText;
    private string? searchText;

    [JsonIgnore]
    public string SenderText => senderText ??= Join(Sender);

    [JsonIgnore]
    public string BodyText => bodyText ??= Join(Body);

    /// <summary>Clé pour regrouper les messages d'un même auteur.</summary>
    [JsonIgnore]
    public string AuthorKey => Author != null ? Author.Text : SenderText;

    /// <summary>Le message tel qu'on le copie : « Nom : texte ».</summary>
    [JsonIgnore]
    public string CopyText => SenderText.Length > 0 ? $"{SenderText}: {BodyText}" : BodyText;

    /// <summary>Texte en minuscules pour la recherche.</summary>
    [JsonIgnore]
    public string SearchText => searchText ??= $"{SenderText} {BodyText}".ToLowerInvariant();

    private static string Join(List<ChatSpan> spans)
    {
        if (spans.Count == 1)
            return spans[0].Text;

        var builder = new StringBuilder();
        foreach (var span in spans)
            builder.Append(span.Text);
        return builder.ToString();
    }
}

/// <summary>Règles d'affichage qui ne dépendent pas de l'interface.</summary>
public static class ChatGrouping
{
    /// <summary>Au-delà de ce délai, un nouveau message du même auteur reprend son propre en-tête.</summary>
    public const double GroupWindowMinutes = 5;

    /// <summary>Le message affiche-t-il un en-tête (nom et heure) ? Sinon il se glisse sous le précédent.</summary>
    public static bool StartsGroup(ChatLine? previous, ChatLine current)
    {
        if (!HasAuthorHeader(current))
            return false;

        return previous == null
            || !HasAuthorHeader(previous)
            || previous.Kind != current.Kind
            || !string.Equals(previous.AuthorKey, current.AuthorKey, StringComparison.Ordinal)
            || (current.TimeUtc - previous.TimeUtc).TotalMinutes > GroupWindowMinutes;
    }

    /// <summary>Les messages système et de combat n'ont pas d'auteur : ils tiennent sur une ligne, sans en-tête.</summary>
    public static bool HasAuthorHeader(ChatLine line) =>
        ChatKinds.Info(line.Kind).HasAuthor && line.Sender.Any(s => s.Text.Length > 0);

    /// <summary>Séparateur entre le nom et le texte en affichage compact.</summary>
    public static string Separator(ChatKind kind) => kind switch
    {
        ChatKind.CustomEmote => " ",
        ChatKind.TellIncoming => " » ",
        ChatKind.TellOutgoing => " « ",
        _ => ": ",
    };
}
