using Dalamud.Game.ClientState.Keys;

namespace BetterChat.Chat;

/// <summary>
/// Un raccourci Ctrl + touche hors d'une zone de texte. Dalamud ne transmet les touches à ImGui que pendant la saisie :
/// en dehors, seul l'état du clavier vu par le jeu permet de les détecter.
/// </summary>
internal sealed class ShortcutKey
{
    private readonly VirtualKey key;
    private bool wasDown;

    public ShortcutKey(VirtualKey key)
    {
        this.key = key;
    }

    /// <summary>Vrai à l'image où la combinaison est enfoncée. À appeler à chaque image.</summary>
    public bool Pressed()
    {
        var down = Plugin.KeyState[VirtualKey.CONTROL] && Plugin.KeyState[key];
        var pressed = down && !wasDown;
        wasDown = down;
        return pressed;
    }
}
