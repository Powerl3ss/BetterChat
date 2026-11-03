using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace BetterChat.Chat;

/// <summary>Ce que le jeu mettra à la place de ses raccourcis (<c>&lt;t&gt;</c>, <c>&lt;flag&gt;</c>...), à l'instant présent.</summary>
internal static class MacroResolver
{
    private const string NoTarget = "(aucune cible)";

    /// <param name="name">Le raccourci sans ses chevrons, en minuscules. Null : raccourci qu'on ne sait pas montrer.</param>
    public static string? Resolve(string name)
    {
        try
        {
            switch (name)
            {
                case "t":
                    return Plugin.TargetManager.Target?.Name.TextValue ?? NoTarget;
                case "tt":
                    return Plugin.TargetManager.Target?.TargetObject?.Name.TextValue ?? NoTarget;
                case "f":
                    return Plugin.TargetManager.FocusTarget?.Name.TextValue ?? NoTarget;
                case "mo":
                    return Plugin.TargetManager.MouseOverTarget?.Name.TextValue ?? NoTarget;
                case "lt":
                    return Plugin.TargetManager.PreviousTarget?.Name.TextValue ?? NoTarget;
                case "me":
                    return Plugin.PlayerState.CharacterName;
                case "pos":
                    return Position();
                case "flag":
                    return Flag();
                case "item":
                    return Item();
                case ['1' or '2' or '3' or '4' or '5' or '6' or '7' or '8']:
                    return PartyMember(name);
            }

            // Les sons ne s'affichent pas dans le message.
            return name.StartsWith("se.", StringComparison.Ordinal) ? string.Empty : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Aperçu du raccourci {Name} impossible", name);
            return null;
        }
    }

    private static unsafe string PartyMember(string number)
    {
        var module = PronounModule.Instance();
        var member = module != null ? module->ResolvePlaceholder($"<{number}>", 0, 0) : null;
        return member != null ? member->NameString : NoTarget;
    }

    private static string Position()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return "(position inconnue)";

        var coordinates = MapUtil.GetMapCoordinates(player);
        return FormatPlace(Plugin.ClientState.TerritoryType, new Vector2(coordinates.X, coordinates.Y));
    }

    private static unsafe string Flag()
    {
        var agent = AgentMap.Instance();
        if (agent == null || agent->FlagMarkerCount == 0)
            return "(aucun drapeau)";

        var marker = agent->FlagMapMarkers[0];
        var map = Plugin.DataManager.GetExcelSheet<Map>().GetRowOrDefault(marker.MapId);
        if (map == null)
            return "(aucun drapeau)";

        return FormatPlace(marker.TerritoryId, MapUtil.WorldToMap(new Vector2(marker.XFloat, marker.YFloat), map.Value));
    }

    private static string FormatPlace(uint territory, Vector2 coordinates)
    {
        var place = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{place} ( {coordinates.X:0.0} , {coordinates.Y:0.0} )").TrimStart();
    }

    private static string Item()
    {
        var id = GameChat.LastLinkedItem;
        var name = id != 0 ? Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText() : null;
        return string.IsNullOrEmpty(name) ? "[objet lié]" : $"[{name}]";
    }
}
