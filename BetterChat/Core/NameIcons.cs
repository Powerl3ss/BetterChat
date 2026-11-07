namespace BetterChat.Core;

/// <summary>Ce qui s'affiche à côté du nom d'un joueur, comme sur les plaques du jeu.</summary>
public enum NameIconKind
{
    None,
    /// <summary>L'icône de son job.</summary>
    Job,
    /// <summary>Son rôle : tank, soigneur, DPS de mêlée, à distance ou lanceur de sorts.</summary>
    Role,
    /// <summary>Son statut en ligne : mentor, nouvel aventurier, absent...</summary>
    Status,
}

/// <summary>Les numéros d'icônes du jeu pour un job, un rôle ou un statut.</summary>
public static class NameIcons
{
    /// <summary>Les icônes de job se suivent à partir de ce numéro, dans l'ordre de la feuille ClassJob.</summary>
    public const uint JobIconBase = 62000;

    /// <summary>Le statut « En ligne » : rien à signaler, le jeu n'affiche pas d'icône.</summary>
    public const uint PlainOnlineStatus = 47;

    public const uint TankIcon = 62581;
    public const uint HealerIcon = 62582;
    public const uint MeleeIcon = 62584;
    public const uint RangedIcon = 62586;
    public const uint CasterIcon = 62587;

    public static uint JobIcon(uint job) => job == 0 ? 0 : JobIconBase + job;

    /// <summary>L'icône du rôle de ce job. Artisans et récolteurs n'ont pas de rôle : leur icône de job est gardée.</summary>
    public static uint RoleIcon(uint job) => job switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => TankIcon, // gladiateur, maraudeur, paladin, guerrier, chevalier noir, pistosabreur
        6 or 24 or 28 or 33 or 40 => HealerIcon, // élémentaliste, mage blanc, érudit, astromancien, sage
        2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 => MeleeIcon, // pugiliste, maître d'hast, moine, chevalier dragon, surineur, ninja, samouraï, faucheur, rôdeur vipère
        5 or 23 or 31 or 38 => RangedIcon, // archer, barde, machiniste, danseur
        7 or 25 or 26 or 27 or 35 or 36 or 42 => CasterIcon, // occultiste, mage noir, arcaniste, invocateur, mage rouge, mage bleu, pictomancien
        _ => JobIcon(job),
    };
}
