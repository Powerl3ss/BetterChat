using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using BetterChat.Core;

namespace BetterChat.Windows;

/// <summary>L'aperçu de la page Affichage : quelques faux messages, dessinés exactement comme dans le chat.</summary>
public sealed partial class ConfigWindow
{
    private MessageList? previewList;
    private ChatFeed? previewFeed;

    private void DrawChatPreview()
    {
        previewList ??= new MessageList(plugin) { Interactive = false };
        previewFeed ??= PreviewFeed();

        SectionTitle("Aperçu", FontAwesomeIcon.Eye, "En direct");
        Card(() =>
        {
            var font = plugin.ChatFont();
            using var pushed = font?.Push();
            // Le même cadre que la fenêtre de chat.
            using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                                    .Push(ImGuiStyleVar.ScrollbarSize, 6f * S);

            var inset = 14f * S;
            var listH = previewList.LastContentHeight > 0f ? previewList.LastContentHeight : ImGui.GetTextLineHeight() * 7f;
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(cardInner, MathF.Round(listH + inset * 2f));
            var drawList = ImGui.GetWindowDrawList();

            // Un décor coloré derrière, comme le jeu : l'opacité du fond et le contour se jugent dessus.
            // Un fond bleu crépuscule arrondi, réchauffé par un halo orangé et un halo vert.
            drawList.AddRectFilled(min, max, Col(new Vector4(0.24f, 0.33f, 0.47f, 1f)), 14f * S);
            var size = max - min;
            RoundedGlow(drawList, min, max, 14f * S, min + new Vector2(size.X * 0.82f, size.Y * 0.92f), size.X * 0.45f, new Vector4(0.85f, 0.55f, 0.30f, 1f), 0.55f);
            RoundedGlow(drawList, min, max, 14f * S, min + new Vector2(size.X * 0.15f, size.Y * 0.90f), size.X * 0.35f, new Vector4(0.35f, 0.55f, 0.40f, 1f), 0.45f);

            var chatMin = min + new Vector2(inset, inset);
            var chatMax = max - new Vector2(inset, inset);
            drawList.AddRectFilled(chatMin, chatMax, Col(ChatWindow.WindowBg with { W = Math.Clamp(Cfg.WindowOpacity, 0f, 1f) }), 12f * S);
            drawList.AddRect(chatMin, chatMax, Col(Line), 12f * S);

            ImGui.SetCursorScreenPos(chatMin);
            previewList.Draw(previewFeed, chatMax - chatMin);

            ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
            ImGui.Dummy(new Vector2(cardInner, 0f));
        }, glow: true);
    }

    private static ChatFeed PreviewFeed()
    {
        var now = DateTime.UtcNow;
        var yesterday = DateTime.Today.AddDays(-1).AddHours(21).AddMinutes(14).ToUniversalTime();

        var log = new ChatLog();
        log.Add(PreviewLine(yesterday, ChatKind.FreeCompany, "Lyra Sunfall", 24, 27, "Bonne soirée à tous, à demain !"));
        log.Add(PreviewLine(now.AddMinutes(-12), ChatKind.Party, "Kaël Ardent", 21, 0, "Je pull dans 5 secondes."));
        log.Add(PreviewLine(now.AddMinutes(-12), ChatKind.Party, "Kaël Ardent", 21, 0, "Pensez aux réductions de dégâts."));
        log.Add(PreviewLine(now.AddMinutes(-3), ChatKind.TellIncoming, "Mira Ombrelune", 38, 32, "Tu viens au raid ce soir ?"));

        var feed = new ChatFeed(new ChatTabSettings
        {
            Name = "Aperçu",
            Kinds = new List<ChatKind> { ChatKind.FreeCompany, ChatKind.Party, ChatKind.TellIncoming },
        });
        feed.Update(log, 0, _ => false);
        return feed;
    }

    private static ChatLine PreviewLine(DateTime timeUtc, ChatKind kind, string author, uint job, uint status, string text)
    {
        var link = ChatLink.Player(author, string.Empty);
        return new ChatLine
        {
            TimeUtc = timeUtc,
            Kind = kind,
            Author = link,
            AuthorJob = job,
            AuthorStatus = status,
            Sender = { new ChatSpan(author, link: link) },
            Body = { new ChatSpan(text) },
            FromHistory = true,
        };
    }
}
