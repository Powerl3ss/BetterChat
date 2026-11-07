using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using static BetterChat.Windows.ChatWindow;

namespace BetterChat.Windows;

/// <summary>
/// La barre de recherche qui glisse sous l'en-tête d'une fenêtre. Le chat en a une, et chaque onglet détaché a la
/// sienne : chacune cherche dans ce qu'elle affiche.
/// </summary>
public sealed class SearchBar
{
    private const float RevealSpeed = 14f;

    private readonly ChatWindow chat;
    private readonly Action<string> apply;
    private bool focus;

    // Ouverture : 0 = fermée, 1 = entièrement sortie.
    private float reveal;

    /// <param name="apply">Applique le texte cherché à ce que la fenêtre affiche.</param>
    public SearchBar(ChatWindow chat, Action<string> apply)
    {
        this.chat = chat;
        this.apply = apply;
    }

    public bool IsOpen { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public void Toggle()
    {
        IsOpen = !IsOpen;
        focus = IsOpen;
        if (!IsOpen)
            Set(string.Empty);
    }

    private void Set(string text)
    {
        Text = text;
        apply(text);
    }

    /// <summary>Fait avancer l'animation et renvoie la hauteur occupée en ce moment, sur un nombre entier de pixels.</summary>
    public float Update(float fullHeight)
    {
        var target = IsOpen ? 1f : 0f;
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        reveal += (target - reveal) * (1f - MathF.Exp(-RevealSpeed * dt));
        if (MathF.Abs(target - reveal) < 0.005f)
            reveal = target;

        return MathF.Round(fullHeight * reveal);
    }

    /// <summary>La barre glisse depuis le dessous de l'en-tête en apparaissant, et les messages descendent pour lui faire place.</summary>
    /// <param name="closeColumn">L'icône la plus à droite de l'en-tête : la croix se place dans sa colonne.</param>
    public void Draw(Vector2 size, float headerHeight, float shown, float fullHeight, FontAwesomeIcon closeColumn)
    {
        if (shown <= 0f)
            return;

        var origin = ImGui.GetWindowPos();
        ImGui.PushClipRect(origin + new Vector2(0f, headerHeight), origin + new Vector2(size.X, headerHeight + shown), true);
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, MathF.Max(reveal, 0.001f) * ImGui.GetStyle().Alpha))
            DrawField(size, headerHeight + shown - fullHeight, fullHeight, closeColumn);
        ImGui.PopClipRect();
    }

    private void DrawField(Vector2 size, float top, float height, FontAwesomeIcon closeColumn)
    {
        var margin = EdgeMargin * S;
        var gap = 6f * S;
        var close = IconButtonSize;
        var closeX = RightButtonX(size.X, closeColumn, close);

        ImGui.SetCursorPos(new Vector2(margin, MathF.Round(top + (height - ImGui.GetFrameHeight()) / 2f)));
        ImGui.SetNextItemWidth(closeX - gap - margin);

        // Attend que la barre soit assez sortie : une zone encore hors du cadre ne peut pas prendre le focus.
        if (focus && reveal >= 0.5f)
        {
            ImGui.SetKeyboardFocusHere();
            focus = false;
        }

        var text = Text;
        using (ImRaii.PushColor(ImGuiCol.FrameBg, InputBg))
        {
            if (ImGui.InputTextWithHint("##searchInput", "Rechercher... (de:Jean canal:CL date:hier)", ref text, 128))
                Set(text);
        }

        // Échap quitte d'abord la zone de texte : c'est à ce moment-là qu'on ferme la recherche.
        var escape = ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Escape, false);

        ImGui.SetCursorPos(new Vector2(closeX, MathF.Round(top + (height - close) / 2f)));
        if (chat.IconButton("##closeSearch", FontAwesomeIcon.Times, close) || escape)
            Toggle();
    }
}
