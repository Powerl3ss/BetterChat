using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Config;
using BetterChat.Core;

namespace BetterChat.Chat;

/// <summary>Couleur de chaque canal : celle choisie dans le plugin, sinon celle du jeu, sinon une couleur de secours.</summary>
public sealed class ChannelColors
{
    private static readonly Dictionary<ChatKind, UiConfigOption> GameOptions = new()
    {
        [ChatKind.Say] = UiConfigOption.ColorSay,
        [ChatKind.Shout] = UiConfigOption.ColorShout,
        [ChatKind.TellIncoming] = UiConfigOption.ColorTell,
        [ChatKind.TellOutgoing] = UiConfigOption.ColorTell,
        [ChatKind.Party] = UiConfigOption.ColorParty,
        [ChatKind.CrossParty] = UiConfigOption.ColorParty,
        [ChatKind.Alliance] = UiConfigOption.ColorAlliance,
        [ChatKind.Yell] = UiConfigOption.ColorYell,
        [ChatKind.Linkshell1] = UiConfigOption.ColorLS1,
        [ChatKind.Linkshell2] = UiConfigOption.ColorLS2,
        [ChatKind.Linkshell3] = UiConfigOption.ColorLS3,
        [ChatKind.Linkshell4] = UiConfigOption.ColorLS4,
        [ChatKind.Linkshell5] = UiConfigOption.ColorLS5,
        [ChatKind.Linkshell6] = UiConfigOption.ColorLS6,
        [ChatKind.Linkshell7] = UiConfigOption.ColorLS7,
        [ChatKind.Linkshell8] = UiConfigOption.ColorLS8,
        [ChatKind.CrossLinkshell1] = UiConfigOption.ColorCWLS,
        [ChatKind.CrossLinkshell2] = UiConfigOption.ColorCWLS2,
        [ChatKind.CrossLinkshell3] = UiConfigOption.ColorCWLS3,
        [ChatKind.CrossLinkshell4] = UiConfigOption.ColorCWLS4,
        [ChatKind.CrossLinkshell5] = UiConfigOption.ColorCWLS5,
        [ChatKind.CrossLinkshell6] = UiConfigOption.ColorCWLS6,
        [ChatKind.CrossLinkshell7] = UiConfigOption.ColorCWLS7,
        [ChatKind.CrossLinkshell8] = UiConfigOption.ColorCWLS8,
        [ChatKind.FreeCompany] = UiConfigOption.ColorFCompany,
        [ChatKind.FreeCompanyAnnouncement] = UiConfigOption.ColorFCAnnounce,
        [ChatKind.NoviceNetwork] = UiConfigOption.ColorBeginner,
        [ChatKind.NoviceNetworkSystem] = UiConfigOption.ColorBeginnerAnnounce,
        [ChatKind.CustomEmote] = UiConfigOption.ColorEmoteUser,
        [ChatKind.StandardEmote] = UiConfigOption.ColorEmote,
        [ChatKind.PvpTeam] = UiConfigOption.ColorPvPGroup,
        [ChatKind.PvpTeamAnnouncement] = UiConfigOption.ColorPvPGroupAnnounce,
        [ChatKind.System] = UiConfigOption.ColorSysMsg,
        [ChatKind.BattleSystem] = UiConfigOption.ColorSysBattle,
        [ChatKind.GatheringSystem] = UiConfigOption.ColorSysGathering,
        [ChatKind.Error] = UiConfigOption.ColorSysErr,
        [ChatKind.NpcDialogue] = UiConfigOption.ColorNpcSay,
        [ChatKind.LootRoll] = UiConfigOption.ColorLoot,
        [ChatKind.Crafting] = UiConfigOption.ColorCraft,
        [ChatKind.Gathering] = UiConfigOption.ColorGathering,
        [ChatKind.Echo] = UiConfigOption.ColorEcho,
        [ChatKind.Damage] = UiConfigOption.ColorAttackSuccess,
        [ChatKind.Miss] = UiConfigOption.ColorAttackFailure,
        [ChatKind.Action] = UiConfigOption.ColorAction,
        [ChatKind.Item] = UiConfigOption.ColorItem,
        [ChatKind.Healing] = UiConfigOption.ColorCureGive,
        [ChatKind.GainBuff] = UiConfigOption.ColorBuffGive,
        [ChatKind.GainDebuff] = UiConfigOption.ColorDebuffGive,
    };

    private readonly Configuration config;
    private readonly Dictionary<ChatKind, Vector4> cache = new();
    private DateTime refreshAt = DateTime.MinValue;

    public ChannelColors(Configuration config)
    {
        this.config = config;
    }

    public Vector4 Of(ChatKind kind)
    {
        // Les couleurs du jeu changent rarement : une lecture toutes les quelques secondes suffit.
        var now = DateTime.UtcNow;
        if (now >= refreshAt)
        {
            refreshAt = now.AddSeconds(3);
            cache.Clear();
        }

        if (cache.TryGetValue(kind, out var known))
            return known;

        var color = Resolve(kind);
        cache[kind] = color;
        return color;
    }

    public void Invalidate() => refreshAt = DateTime.MinValue;

    private Vector4 Resolve(ChatKind kind)
    {
        if (config.ChannelColors.TryGetValue(kind, out var custom))
            return custom;

        if (config.UseGameColors && GameOptions.TryGetValue(kind, out var option) &&
            Plugin.GameConfig.TryGet(option, out uint value) && (value & 0xFFFFFF) != 0)
        {
            // Le jeu range sa couleur en 0xRRGGBB.
            return FromRgba(((value & 0xFFFFFF) << 8) | 0xFF);
        }

        return FromRgba(ChatKinds.Info(kind).Color);
    }

    public static Vector4 FromRgba(uint rgba) => new(
        ((rgba >> 24) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        ((rgba >> 8) & 0xFF) / 255f,
        (rgba & 0xFF) / 255f);
}
