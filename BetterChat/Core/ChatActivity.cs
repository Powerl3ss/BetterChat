using System;
using System.Collections.Generic;

namespace BetterChat.Core;

/// <summary>
/// L'activité du chat sur les sept derniers jours, pour l'accueil des réglages : messages de joueurs par jour et par
/// tranche de deux heures, et quelques chiffres du jour.
/// </summary>
public sealed class ChatActivity
{
    public const int Days = 7;
    public const int Slots = 12;
    public const int HoursPerSlot = 24 / Slots;

    /// <summary>[jour, tranche] : le jour 0 est le plus ancien, le jour 6 aujourd'hui.</summary>
    public int[,] Grid { get; } = new int[Days, Slots];

    public DateTime FirstDay { get; private set; }
    public int Max { get; private set; }
    public int Today { get; private set; }
    public int Yesterday { get; private set; }
    public int MentionsToday { get; private set; }

    /// <param name="now">Heure locale.</param>
    public static ChatActivity Compute(IEnumerable<ChatLine> lines, DateTime now)
    {
        var activity = new ChatActivity { FirstDay = now.Date.AddDays(-(Days - 1)) };
        foreach (var line in lines)
        {
            if (!ChatKinds.Info(line.Kind).HasAuthor)
                continue;

            var time = line.TimeUtc.ToLocalTime();
            var day = (int)(time.Date - activity.FirstDay).TotalDays;
            if (day is < 0 or >= Days)
                continue;

            var count = ++activity.Grid[day, time.Hour / HoursPerSlot];
            activity.Max = Math.Max(activity.Max, count);

            if (day == Days - 1)
            {
                activity.Today++;
                if (line.Mentioned)
                    activity.MentionsToday++;
            }
            else if (day == Days - 2)
            {
                activity.Yesterday++;
            }
        }

        return activity;
    }

    /// <summary>De 0 (aucun message) à 4 (les tranches les plus actives de la semaine).</summary>
    public int Level(int day, int slot)
    {
        var count = Grid[day, slot];
        if (count == 0 || Max == 0)
            return 0;

        var ratio = count / (float)Max;
        return ratio switch
        {
            < 0.25f => 1,
            < 0.5f => 2,
            < 0.8f => 3,
            _ => 4,
        };
    }

    /// <summary>L'écart avec hier, en pour cent. Null s'il n'y avait rien hier.</summary>
    public float? ChangeFromYesterday => Yesterday == 0 ? null : (Today - Yesterday) * 100f / Yesterday;
}
