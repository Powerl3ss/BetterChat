using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using BetterChat.Core;

namespace BetterChat.Chat;

/// <summary>Transforme un message du jeu en texte coloré et en liens, sans rien garder de lié au jeu.</summary>
internal static class MessageConverter
{
    public static ChatLine Convert(IChatMessage message)
    {
        var sender = ToSpans(message.Sender, out var author);
        var body = UrlFinder.Split(ToSpans(message.Message, out var firstPlayer));
        var kind = ChatKinds.Normalize((ushort)message.LogKind);

        // Les emotes et les messages système ont leur auteur dans le texte.
        if (author == null && !ChatKinds.Info(kind).HasAuthor)
            author = firstPlayer;

        return new ChatLine
        {
            TimeUtc = DateTime.UtcNow,
            Kind = kind,
            Source = SourceOf(message.SourceKind),
            Sender = sender,
            Body = body,
            Author = author,
        };
    }

    private static ChatSource SourceOf(XivChatRelationKind kind) => kind switch
    {
        XivChatRelationKind.None => ChatSource.Unknown,
        XivChatRelationKind.LocalPlayer => ChatSource.Self,
        XivChatRelationKind.PetOrCompanion => ChatSource.Self,
        XivChatRelationKind.PartyMember or XivChatRelationKind.AllianceMember => ChatSource.Party,
        XivChatRelationKind.PetOrCompanionParty or XivChatRelationKind.PetOrCompanionAlliance => ChatSource.Party,
        _ => ChatSource.Others,
    };

    private static List<ChatSpan> ToSpans(SeString text, out ChatLink? firstPlayer)
    {
        firstPlayer = null;
        var spans = new List<ChatSpan>();
        var colors = new Stack<uint>();
        var current = new StringBuilder();
        var italic = false;
        ChatLink? link = null;

        // Un lien colore son texte, mais le message ne referme pas toujours toutes ses couleurs : à la fin du lien, on
        // revient aux couleurs d'avant lui. Si le message referme ensuite ces couleurs lui-même, ces fermetures sont
        // ignorées (elles ont déjà été faites).
        var depthBeforePush = 0;
        var justPushed = false;
        var linkDepth = -1;
        var closedForLink = 0;

        void StartLink()
        {
            // La couleur du lien est posée juste avant lui : le lien commence avant elle.
            linkDepth = justPushed ? depthBeforePush : colors.Count;
        }

        // Change de style : le texte accumulé devient un morceau.
        void Flush()
        {
            if (current.Length == 0)
                return;

            spans.Add(new ChatSpan(current.ToString(), colors.Count > 0 ? colors.Peek() : 0u, italic, link));
            current.Clear();
        }

        foreach (var payload in text.Payloads)
        {
            switch (payload)
            {
                case UIForegroundPayload color:
                    Flush();
                    if (color.IsEnabled)
                    {
                        depthBeforePush = colors.Count;
                        colors.Push(ColorOf(color));
                        justPushed = true;
                        continue;
                    }

                    if (closedForLink > 0)
                        closedForLink--;
                    else if (colors.Count > 0)
                        colors.Pop();
                    break;

                case UIGlowPayload:
                    // Le halo accompagne la couleur : il ne sépare pas une couleur de son lien.
                    continue;

                case EmphasisItalicPayload emphasis:
                    Flush();
                    italic = emphasis.IsEnabled;
                    break;

                case PlayerPayload player:
                    Flush();
                    StartLink();
                    link = ChatLink.Player(player.PlayerName, player.World.ValueNullable?.Name.ExtractText() ?? string.Empty);
                    firstPlayer ??= link;
                    break;

                case ItemPayload item:
                    Flush();
                    StartLink();
                    link = ChatLink.Item(item.ItemId, item.DisplayName ?? string.Empty);
                    break;

                case MapLinkPayload map:
                    Flush();
                    StartLink();
                    link = ChatLink.Map(map.TerritoryType.RowId, map.Map.RowId, map.RawX, map.RawY, map.PlaceName);
                    break;

                case QuestPayload quest:
                    Flush();
                    StartLink();
                    link = ChatLink.Quest(quest.Quest.RowId);
                    break;

                case StatusPayload status:
                    Flush();
                    StartLink();
                    link = ChatLink.Status(status.Status.RowId);
                    break;

                case PartyFinderPayload listing when listing.LinkType != PartyFinderPayload.PartyFinderLinkType.PartyFinderNotification:
                    Flush();
                    StartLink();
                    link = ChatLink.PartyFinder(listing.ListingId);
                    break;

                case DalamudLinkPayload pluginLink:
                    Flush();
                    StartLink();
                    link = ChatLink.PluginLink(pluginLink.Plugin, pluginLink.CommandId, LinkPayloads(text, pluginLink));
                    break;

                case RawPayload raw when raw.Equals(RawPayload.LinkTerminator):
                    Flush();
                    link = null;
                    closedForLink = 0;
                    while (linkDepth >= 0 && colors.Count > linkDepth)
                    {
                        colors.Pop();
                        closedForLink++;
                    }

                    linkDepth = -1;
                    break;

                case IconPayload icon:
                    // L'icône des mondes sépare « Nom » et « Monde » : sans elle, les deux se colleraient.
                    if (icon.Icon == BitmapFontIcon.CrossWorld)
                        current.Append('@');
                    else if (GameIcons.ToChar((uint)icon.Icon) is { } iconChar)
                        current.Append(iconChar);
                    break;

                case ITextProvider provider:
                    current.Append(provider.Text);
                    closedForLink = 0;
                    break;
            }

            justPushed = false;
        }

        Flush();
        return spans;
    }

    /// <summary>Le lien de plugin et ce qu'il entoure, jusqu'à sa fin : ce que le jeu lui aurait donné au clic.</summary>
    private static byte[] LinkPayloads(SeString text, Payload start)
    {
        var payloads = text.Payloads;
        var first = payloads.IndexOf(start);
        var end = first < 0 ? -1 : payloads.FindIndex(first, p => p is RawPayload raw && raw.Equals(RawPayload.LinkTerminator));
        var part = first < 0 ? new List<Payload> { start } : payloads.GetRange(first, (end < 0 ? payloads.Count : end + 1) - first);

        try
        {
            return new SeString(part).Encode();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    private static uint ColorOf(UIForegroundPayload payload)
    {
        try
        {
            var rgba = payload.RGBA;
            return rgba == 0 ? 0u : rgba | 0xFFu;
        }
        catch (Exception)
        {
            // Couleur absente des données du jeu : le texte garde celle du canal.
            return 0u;
        }
    }
}
