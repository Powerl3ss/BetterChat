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
                        colors.Push(ColorOf(color));
                    else if (colors.Count > 0)
                        colors.Pop();
                    break;

                case EmphasisItalicPayload emphasis:
                    Flush();
                    italic = emphasis.IsEnabled;
                    break;

                case PlayerPayload player:
                    Flush();
                    link = ChatLink.Player(player.PlayerName, player.World.ValueNullable?.Name.ExtractText() ?? string.Empty);
                    firstPlayer ??= link;
                    break;

                case ItemPayload item:
                    Flush();
                    link = ChatLink.Item(item.ItemId, item.DisplayName ?? string.Empty);
                    break;

                case MapLinkPayload map:
                    Flush();
                    link = ChatLink.Map(map.TerritoryType.RowId, map.Map.RowId, map.RawX, map.RawY, map.PlaceName);
                    break;

                case RawPayload raw when raw.Equals(RawPayload.LinkTerminator):
                    Flush();
                    link = null;
                    break;

                case IconPayload icon:
                    // L'icône des mondes sépare « Nom » et « Monde » : sans elle, les deux se colleraient.
                    if (icon.Icon == BitmapFontIcon.CrossWorld)
                        current.Append('@');
                    break;

                case ITextProvider provider:
                    current.Append(provider.Text);
                    break;
            }
        }

        Flush();
        return spans;
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
