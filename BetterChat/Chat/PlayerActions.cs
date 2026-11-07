using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using BetterChat.Core;

namespace BetterChat.Chat;

/// <summary>
/// Les actions du menu d'un joueur que le jeu propose aussi : inviter, examiner, plaque d'aventurier, ami.
/// Chacune n'apparaît que si le jeu peut la faire : un joueur à proximité, un identifiant connu...
/// </summary>
internal sealed unsafe class PlayerActions : IDisposable
{
    // La commande « /friendlist add » n'accepte pas un nom tapé, seulement un raccourci comme <t>. Ce raccourci
    // inventé est remplacé par le nom du joueur au moment où le jeu le lit.
    private const string ResolvePlaceholderSignature = "E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B D0 49 8D 4F";
    private const int NameBufferSize = 128;

    private delegate nint ResolvePlaceholderDelegate(nint module, byte* placeholder, byte a3, byte a4);

    private readonly string placeholder = $"<{Guid.NewGuid():N}>";
    private readonly nint nameBuffer = Marshal.AllocHGlobal(NameBufferSize);
    private readonly Hook<ResolvePlaceholderDelegate>? resolveHook;
    private string? pendingName;

    public PlayerActions()
    {
        try
        {
            resolveHook = Plugin.GameInterop.HookFromSignature<ResolvePlaceholderDelegate>(ResolvePlaceholderSignature, ResolvePlaceholderDetour);
            resolveHook.Enable();
        }
        catch (Exception ex)
        {
            // Sans elle, « Ajouter en ami » n'est simplement pas proposé.
            Plugin.Log.Warning(ex, "Ajout d'ami indisponible : fonction du jeu introuvable");
        }
    }

    public void Dispose()
    {
        resolveHook?.Dispose();
        Marshal.FreeHGlobal(nameBuffer);
    }

    // ------------------------------------------------------------ Ce que le jeu sait du joueur

    private static IPlayerCharacter? Nearby(ChatLink player) =>
        Plugin.ObjectTable.PlayerObjects.OfType<IPlayerCharacter>().FirstOrDefault(p =>
            p.Name.TextValue == player.Text &&
            (player.World.Length == 0 || p.HomeWorld.ValueNullable?.Name.ExtractText() == player.World));

    private static ushort WorldOf(ChatLink player)
    {
        if (player.World.Length == 0)
            return (ushort)Plugin.PlayerState.CurrentWorld.RowId;

        var world = Plugin.DataManager.GetExcelSheet<World>().FirstOrDefault(w => w.Name.ExtractText() == player.World);
        return (ushort)world.RowId;
    }

    /// <summary>Identifiant du personnage : à proximité, dans le groupe ou dans la liste d'amis. 0 : inconnu.</summary>
    private static ulong ContentIdOf(ChatLink player)
    {
        if (Nearby(player) is { } character)
            return ((Character*)character.Address)->ContentId;

        foreach (var member in Plugin.PartyList)
        {
            if (member.Name.TextValue == player.Text)
                return (ulong)member.ContentId;
        }

        return FriendEntry(player.Text) is { } friend ? friend : 0;
    }

    private static ulong? FriendEntry(string name)
    {
        var list = (InfoProxyCommonList*)InfoProxyFriendList.Instance();
        if (list == null || list->CharData == null)
            return null;

        foreach (ref readonly var friend in list->CharDataSpan)
        {
            if (friend.NameString == name)
                return friend.ContentId;
        }

        return null;
    }

    private static bool InDuty => Plugin.Condition[ConditionFlag.BoundByDuty56];

    // ------------------------------------------------------------ Inviter

    public static bool CanInvite(ChatLink player)
    {
        // En mission, le jeu passe par d'autres fonctions selon la zone : l'invitation n'est pas proposée.
        if (InDuty)
            return false;

        var party = Plugin.PartyList;
        if (party.Any(m => m.Name.TextValue == player.Text))
            return false;

        // Seul le chef d'un groupe invite.
        if (party.Length > 0 && party[(int)party.PartyLeaderIndex]?.ContentId is { } leader && (ulong)leader != Plugin.PlayerState.ContentId)
            return false;

        // Sur un autre monde, le jeu a besoin de l'identifiant du personnage.
        return WorldOf(player) == Plugin.PlayerState.CurrentWorld.RowId || ContentIdOf(player) != 0;
    }

    public static void Invite(ChatLink player)
    {
        var world = WorldOf(player);
        var contentId = ContentIdOf(player);
        var invite = InfoProxyPartyInvite.Instance();
        if (invite == null)
            return;

        if (world == Plugin.PlayerState.CurrentWorld.RowId)
            invite->InviteToParty(contentId, player.Text, world);
        else
            invite->InviteToPartyContentId(contentId, world);
    }

    // ------------------------------------------------------------ Examiner, plaque

    public static bool CanExamine(ChatLink player) => Nearby(player) != null;

    public static void Examine(ChatLink player)
    {
        if (Nearby(player) is { } character)
            AgentInspect.Instance()->ExamineCharacter(character.EntityId, false);
    }

    public static bool CanOpenPlate(ChatLink player) => ContentIdOf(player) != 0;

    public static void OpenPlate(ChatLink player)
    {
        var agent = AgentCharaCard.Instance();
        if (agent == null)
            return;

        if (Nearby(player) is { } character)
            agent->OpenCharaCard((GameObject*)character.Address);
        else if (ContentIdOf(player) is var contentId and not 0)
            agent->OpenCharaCard(contentId);
    }

    // ------------------------------------------------------------ Ami

    public bool CanAddFriend(ChatLink player) => resolveHook != null && FriendEntry(player.Text) == null;

    public void AddFriend(ChatLink player)
    {
        if (resolveHook == null)
            return;

        var world = player.World.Length > 0 ? player.World : Plugin.PlayerState.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        pendingName = world.Length > 0 ? $"{player.Text}@{world}" : player.Text;
        GameChat.Run($"/friendlist add {placeholder}");
    }

    private nint ResolvePlaceholderDetour(nint module, byte* text, byte a3, byte a4)
    {
        if (pendingName == null || Marshal.PtrToStringUTF8((nint)text) != placeholder)
            return resolveHook!.Original(module, text, a3, a4);

        var bytes = Encoding.UTF8.GetBytes(pendingName);
        var length = Math.Min(bytes.Length, NameBufferSize - 1);
        Marshal.Copy(bytes, 0, nameBuffer, length);
        Marshal.WriteByte(nameBuffer, length, 0);
        pendingName = null;
        return nameBuffer;
    }
}
