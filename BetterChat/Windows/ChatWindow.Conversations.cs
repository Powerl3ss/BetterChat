using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>Une pastille par personne dans un onglet de messages privés : chaque conversation se suit à part.</summary>
public sealed partial class ChatWindow
{
    private const string ConversationMenu = "##conversationMenu";
    private string? conversationMenuKey;

    private float ConversationBarHeight() =>
        plugin.ActiveFeed.Settings.Kinds.Contains(ChatKind.TellIncoming) && plugin.Conversations.All.Count > 0
            ? MathF.Round(ImGui.GetTextLineHeight() + 14f * S)
            : 0f;

    private void DrawConversationBar(Vector2 size, float top, float height)
    {
        var feed = plugin.ActiveFeed;
        var conversations = plugin.Conversations.All.ToList();

        // La personne affichée a pu sortir de la liste : l'onglet revient à toutes les conversations.
        if (feed.Partner != null && conversations.All(c => c.Key != feed.Partner))
            feed.SetPartner(null);

        var origin = ImGui.GetWindowPos();
        var thickness = MathF.Max(1f, MathF.Round(S));
        ImGui.GetWindowDrawList().AddRectFilled(origin + new Vector2(0f, top + height - thickness), origin + new Vector2(size.X, top + height), Col(Line));

        var pillH = MathF.Round(ImGui.GetTextLineHeight() + 6f * S);
        var y = MathF.Round(top + (height - pillH) / 2f);
        var x = EdgeMargin * S;
        var limit = size.X - EdgeMargin * S;

        if (DrawConversationPill("##allTells", "Tous", null, feed.Partner == null, ref x, y, pillH, limit))
            feed.SetPartner(null);

        foreach (var conversation in conversations)
        {
            if (x >= limit)
                break;

            if (!DrawConversationPill($"##tell{conversation.Key}", conversation.Person.Text, conversation, feed.Partner == conversation.Key, ref x, y, pillH, limit))
                continue;

            // Ouvrir une conversation, c'est aussi pouvoir lui répondre tout de suite.
            feed.SetPartner(conversation.Key);
            plugin.Conversations.MarkRead(conversation.Key);
            plugin.Game.SwitchToTell(conversation.Person);
        }

        DrawConversationMenu();
    }

    /// <summary>Une pastille, si elle tient dans la largeur. Renvoie vrai au clic.</summary>
    private bool DrawConversationPill(string id, string label, TellConversation? conversation, bool selected, ref float x, float y, float height, float limit)
    {
        var lineH = ImGui.GetTextLineHeight();
        var padX = 9f * S;
        var badge = conversation is { Unread: > 0 } ? (conversation.Unread > 9 ? "9+" : conversation.Unread.ToString()) : null;
        var labelW = Measure(label);
        var width = MathF.Ceiling(labelW + padX * 2f + (badge != null ? 6f * S + BadgeWidth(badge) : 0f));
        if (x + width > limit)
        {
            x = limit;
            return false;
        }

        ImGui.SetCursorPos(new Vector2(x, y));
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var background = selected ? Accent with { W = 0.22f } : new Vector4(1f, 1f, 1f, hovered ? 0.10f : 0.05f);
        drawList.AddRectFilled(min, min + new Vector2(width, height), Col(background), height / 2f);

        var textY = MathF.Round(min.Y + (height - lineH) / 2f);
        DrawOutlinedText(drawList, new Vector2(min.X + padX, textY), selected ? Accent : TextMain, label);
        if (badge != null)
            DrawBadge(badge, min.X + padX + labelW + 6f * S, textY, true);

        if (conversation != null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            conversationMenuKey = conversation.Key;
            ImGui.OpenPopup(ConversationMenu);
        }

        x += width + 6f * S;
        return clicked;
    }

    private void DrawConversationMenu()
    {
        if (!Tip.BeginMenu(ConversationMenu))
            return;

        if (conversationMenuKey is { } key && Tip.MenuItem("Fermer la conversation", FontAwesomeIcon.Times))
        {
            plugin.Conversations.Remove(key);
            if (plugin.ActiveFeed.Partner == key)
                plugin.ActiveFeed.SetPartner(null);
        }

        Tip.EndMenu();
    }
}
