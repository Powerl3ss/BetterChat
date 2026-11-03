using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterChat.Core;

// Petit lanceur sans dépendance : chaque test est une méthode, un échec fait échouer la CI.
var tests = new (string Name, Action Run)[]
{
    ("Types : aucun doublon, un code inconnu reste visible", Tests.KindsAreUniqueAndUnknownIsKept),
    ("Types : les linkshells correspondent aux numéros du jeu", Tests.LinkshellNumbers),
    ("Canaux : numéro du jeu vers type de message", Tests.GameChannelMapping),
    ("Canaux : chaque canal a sa commande", Tests.ChannelCommands),
    ("Canaux : préfixe tapé reconnu", Tests.TypedPrefix),
    ("Canaux : retour vers le numéro du jeu", Tests.ToGameChannelRoundTrip),
    ("Canaux : les commandes du jeu remplacent celles par défaut", Tests.GameCommandsReplaceDefaults),
    ("Pastille : un seul chiffre donne un rond, centré", Tests.BadgeSingleDigit),
    ("Pastille : chiffres réguliers malgré des largeurs différentes", Tests.BadgeDigitsAreEven),
    ("Pastille : le dessin est centré dans la largeur", Tests.BadgeInkIsCentered),
    ("Onglets : le général ne contient pas le combat", Tests.GeneralHasNoBattle),
    ("Onglets : filtre du combat par origine", Tests.BattleFilterBySource),
    ("Vue : filtre de l'onglet et nouveaux messages", Tests.FeedFiltersAndAppends),
    ("Vue : messages non lus et mentions", Tests.FeedCountsUnread),
    ("Vue : l'historique relu ne compte pas comme non lu", Tests.HistoryIsNotUnread),
    ("Vue : recherche sur le nom et le texte", Tests.FeedSearch),
    ("Vue : un joueur masqué disparaît", Tests.FeedMutes),
    ("Vue : le nettoyage reconstruit la liste", Tests.FeedRebuildsAfterTrim),
    ("Répétitions : messages identiques regroupés", Tests.RepeatsCollapse),
    ("Répétitions : pas d'autre auteur, de long délai ni de combat", Tests.RepeatsBreak),
    ("Répétitions : ne relancent pas les non-lus", Tests.RepeatsDoNotCountAsUnread),
    ("Répétitions : recomptées après une reconstruction", Tests.RepeatsSurviveRebuild),
    ("Anti-spam : mots, expressions, sans accents ni majuscules", Tests.SpamMatchesRules),
    ("Anti-spam : seulement le texte des joueurs", Tests.SpamIgnoresSystemAndNames),
    ("Recherche : de, canal et date", Tests.SearchFilters),
    ("Recherche : guillemets et mots combinés", Tests.SearchQuotesAndWords),
    ("Recherche : dates relatives et formats", Tests.SearchDates),
    ("Recherche : préfixe inconnu ou date illisible restent du texte", Tests.SearchUnknownPrefixIsText),
    ("Recherche : accents ignorés dans l'onglet", Tests.FeedSearchIgnoresAccents),
    ("Séparateurs : repère au premier message arrivé en ton absence", Tests.NewMarkerIsFirstUnread),
    ("Séparateurs : le repère disparaît en quittant l'onglet", Tests.NewMarkerClearsOnLeave),
    ("Séparateurs : changement de date", Tests.DateSeparators),
    ("Séparateurs : texte de la date", Tests.DateLabels),
    ("Icône : le dessin est trouvé, sans les marges ni l'ombre", Tests.IconInkFound),
    ("Icône : image vide ou illisible, l'image entière sert", Tests.IconInkFallback),
    ("Connexion : la barre apparaît tout de suite, dans tous les onglets", Tests.SessionBarInEveryTab),
    ("Connexion : ni en haut de la liste, ni deux de suite", Tests.SessionBarNotDoubled),
    ("Connexion : pas de non-lu ni de double barre avec le repère", Tests.SessionBarIsNotUnread),
    ("Connexion : absente des résultats de recherche", Tests.SessionBarHiddenBySearch),
    ("Connexion : gardée dans l'historique", Tests.SessionBarIsSaved),
    ("Répétitions : pas d'une connexion à l'autre", Tests.RepeatsStopAtSession),
    ("Export : format lisible, sans symboles du jeu", Tests.ExportFormat),
    ("Aperçu : raccourcis remplacés, inconnus conservés", Tests.MacroExpansion),
    ("Mémoire : le combat part avant les discussions", Tests.TrimRemovesBattleFirst),
    ("Mémoire : la limite est respectée sans combat", Tests.TrimKeepsNewest),
    ("Groupes : même auteur, pas de nouvel en-tête", Tests.GroupingSameAuthor),
    ("Groupes : un autre auteur ou un long délai en reprend un", Tests.GroupingBreaks),
    ("Groupes : un message système n'a pas d'en-tête", Tests.GroupingSystem),
    ("Mise en page : le texte court tient sur une ligne", Tests.WrapShortText),
    ("Mise en page : coupe aux espaces", Tests.WrapAtSpaces),
    ("Mise en page : un mot trop long est coupé", Tests.WrapLongWord),
    ("Mise en page : le japonais se coupe entre deux caractères", Tests.WrapCjk),
    ("Mise en page : couleurs conservées d'un morceau à l'autre", Tests.WrapKeepsSpans),
    ("Mise en page : saut de ligne explicite", Tests.WrapNewLine),
    ("Mise en page : retrait des lignes suivantes", Tests.WrapIndent),
    ("Mise en page : aucun caractère perdu", Tests.WrapLosesNothing),
    ("Position : recherche du premier message visible", Tests.StackFindsFirstVisible),
    ("Saisie : sauts de ligne et contrôles retirés", Tests.NormalizeText),
    ("Saisie : limite comptée en octets", Tests.ByteLength),
    ("Saisie : historique avec les flèches et brouillon", Tests.InputHistoryNavigation),
    ("Saisie : pas de doublon consécutif", Tests.InputHistoryDedupes),
    ("Commandes : d'abord le début, puis le milieu", Tests.SuggestOrder),
    ("Commandes : plus rien après l'espace", Tests.SuggestStopsAfterSpace),
    ("Mentions : mots entiers, sans les majuscules", Tests.MentionsWholeWords),
    ("Liens : adresses repérées, sans la ponctuation", Tests.UrlSplit),
    ("Liens : un texte déjà lié n'est pas touché", Tests.UrlKeepsExistingLinks),
    ("Liens : www reçoit https", Tests.UrlAddsScheme),
    ("Messages privés : le plus récent d'abord, sans doublon", Tests.RecentTellsOrder),
    ("Sauvegarde : un message relu est identique", Tests.StoreRoundTrip),
    ("Sauvegarde : les accents survivent", Tests.StoreKeepsAccents),
    ("Sauvegarde : une ligne abîmée n'empêche pas de lire les autres", Tests.StoreSkipsCorruptLine),
    ("Sauvegarde : seuls les derniers messages sont relus", Tests.StoreLoadsNewest),
    ("Sauvegarde : le fichier est réduit quand il grandit trop", Tests.StoreTrimsFile),
    ("Sauvegarde : un fichier par personnage", Tests.StorePerCharacter),
    ("Sauvegarde : l'effacement est définitif", Tests.StoreDelete),
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"  ok    {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  ÉCHEC {name}\n        {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests réussis");
return failures == 0 ? 0 : 1;

internal static class Tests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void Same<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} : attendu « {expected} », obtenu « {actual} »");
    }

    private static readonly DateTime T0 = new(2026, 11, 3, 20, 0, 0, DateTimeKind.Utc);

    private static ChatLine Line(ChatKind kind, string text, string sender = "", ChatSource source = ChatSource.Unknown, bool mentioned = false, DateTime? time = null) => new()
    {
        TimeUtc = time ?? T0,
        Kind = kind,
        Source = source,
        Sender = sender.Length > 0 ? new List<ChatSpan> { new(sender) } : new List<ChatSpan>(),
        Body = new List<ChatSpan> { new(text) },
        Author = sender.Length > 0 ? ChatLink.Player(sender, "Ragnarok") : null,
        Mentioned = mentioned,
    };

    private static bool NoMute(ChatLine line) => false;

    private static ChatFeed Feed(params ChatKind[] kinds) => new(new ChatTabSettings { Name = "Test", Kinds = kinds.ToList() });

    // ---------------------------------------------------------------- Types et canaux

    public static void KindsAreUniqueAndUnknownIsKept()
    {
        Same(ChatKinds.All.Count, ChatKinds.All.Select(i => i.Kind).Distinct().Count(), "types uniques");
        Same(ChatKind.Other, ChatKinds.Normalize(9999), "code inconnu");
        Same(ChatKind.Party, ChatKinds.Normalize(14), "groupe");
    }

    public static void LinkshellNumbers()
    {
        Same(ChatKind.Linkshell1, ChatKinds.Linkshell(1), "LS1");
        Same(ChatKind.Linkshell8, ChatKinds.Linkshell(8), "LS8");
        Same(ChatKind.CrossLinkshell1, ChatKinds.CrossLinkshell(1), "CW1");
        Same(ChatKind.CrossLinkshell2, ChatKinds.CrossLinkshell(2), "CW2");
        Same(ChatKind.CrossLinkshell8, ChatKinds.CrossLinkshell(8), "CW8");
    }

    public static void GameChannelMapping()
    {
        Same(ChatKind.TellOutgoing, ChatChannels.FromGameChannel(0), "message privé");
        Same(ChatKind.TellOutgoing, ChatChannels.FromGameChannel(18), "message privé (autre numéro)");
        Same(ChatKind.Say, ChatChannels.FromGameChannel(1), "dire");
        Same(ChatKind.Party, ChatChannels.FromGameChannel(2), "groupe");
        Same(ChatKind.FreeCompany, ChatChannels.FromGameChannel(6), "compagnie");
        Same(ChatKind.CrossLinkshell1, ChatChannels.FromGameChannel(9), "CW1");
        Same(ChatKind.CrossLinkshell8, ChatChannels.FromGameChannel(16), "CW8");
        Same(ChatKind.Linkshell1, ChatChannels.FromGameChannel(19), "LS1");
        Same(ChatKind.Linkshell8, ChatChannels.FromGameChannel(26), "LS8");
    }

    public static void ChannelCommands()
    {
        Same("/p", ChatChannels.Command(ChatKind.Party), "groupe");
        Same("/l3", ChatChannels.Command(ChatKind.Linkshell3), "LS3");
        Same("/cwl1", ChatChannels.Command(ChatKind.CrossLinkshell1), "CW1");
        Same("/cwl5", ChatChannels.Command(ChatKind.CrossLinkshell5), "CW5");
        Same(string.Empty, ChatChannels.Command(ChatKind.Damage), "pas de canal");
    }

    public static void TypedPrefix()
    {
        Same(ChatKind.Party, ChatChannels.FromTypedPrefix("/p salut"), "groupe");
        Same(ChatKind.Party, ChatChannels.FromTypedPrefix("/P salut"), "majuscule");
        Same(ChatKind.Linkshell2, ChatChannels.FromTypedPrefix("/linkshell2 coucou"), "LS2");
        Same(ChatKind.CrossLinkshell3, ChatChannels.FromTypedPrefix("/cwl3"), "CW3");
        Same(ChatKind.TellOutgoing, ChatChannels.FromTypedPrefix("/tell Jean Dupont salut"), "tell");
        Same(null, ChatChannels.FromTypedPrefix("bonjour"), "message normal");
        Same(null, ChatChannels.FromTypedPrefix("/wave"), "autre commande");
    }

    public static void ToGameChannelRoundTrip()
    {
        foreach (var kind in new[] { ChatKind.Say, ChatKind.Yell, ChatKind.Shout, ChatKind.Party, ChatKind.Alliance, ChatKind.FreeCompany, ChatKind.PvpTeam, ChatKind.NoviceNetwork })
            Same(kind, ChatChannels.FromGameChannel(ChatChannels.ToGameChannel(kind)), kind.ToString());

        for (var i = 1; i <= 8; i++)
        {
            Same(ChatKinds.Linkshell(i), ChatChannels.FromGameChannel(ChatChannels.ToGameChannel(ChatKinds.Linkshell(i))), $"LS{i}");
            Same(ChatKinds.CrossLinkshell(i), ChatChannels.FromGameChannel(ChatChannels.ToGameChannel(ChatKinds.CrossLinkshell(i))), $"CW{i}");
            Same((uint)(i - 1), ChatChannels.LinkshellIndex(ChatKinds.Linkshell(i)), $"index LS{i}");
            Same((uint)(i - 1), ChatChannels.LinkshellIndex(ChatKinds.CrossLinkshell(i)), $"index CW{i}");
        }

        Same(5, ChatChannels.ToGameChannel(ChatKind.Shout), "hurler");
        Same(-1, ChatChannels.ToGameChannel(ChatKind.Damage), "pas un canal");
        Same(-1, ChatChannels.ToGameChannel(ChatKind.TellOutgoing), "message privé : il faut un destinataire");
    }

    public static void GameCommandsReplaceDefaults()
    {
        ChatChannels.SetCommands(ChatKind.Shout, new[] { "/cr", "/crier" });
        Same(null, ChatChannels.FromTypedPrefix("/sh bonjour"), "l'ancienne commande n'est plus un canal");
        Same(ChatKind.Shout, ChatChannels.FromTypedPrefix("/crier bonjour"), "nouvelle commande");
        Same("/cr", ChatChannels.Command(ChatKind.Shout), "affichée dans le menu");

        ChatChannels.SetCommands(ChatKind.Yell, Array.Empty<string>());
        Same(ChatKind.Yell, ChatChannels.FromTypedPrefix("/y salut"), "une liste vide ne retire rien");

        ChatChannels.SetCommands(ChatKind.Shout, new[] { "/sh", "/shout" });
    }

    // ---------------------------------------------------------------- Pastilles

    // Comme dans la police : largeurs différentes, et un peu de vide à gauche.
    private static readonly GlyphBox NarrowOne = new(2f, 5f);
    private static readonly GlyphBox WideTwo = new(1f, 8f);

    public static void BadgeSingleDigit()
    {
        var badge = BadgeLayout.Compute(new[] { WideTwo }, 7f, 1f, 4f, 15f);
        Same(15f, badge.Width, "aussi large que haute : un rond");
        Same(4.5f, badge.InkMiddle - badge.PenX[0], "milieu du dessin");
    }

    public static void BadgeDigitsAreEven()
    {
        var badge = BadgeLayout.Compute(new[] { NarrowOne, NarrowOne, WideTwo }, 7f, 1f, 4f, 15f);

        // Le milieu du dessin de chaque chiffre : l'écart doit être le même d'un chiffre à l'autre.
        var first = badge.PenX[0] + (NarrowOne.Left + NarrowOne.Right) / 2f;
        var second = badge.PenX[1] + (NarrowOne.Left + NarrowOne.Right) / 2f;
        var third = badge.PenX[2] + (WideTwo.Left + WideTwo.Right) / 2f;
        Same(8f, second - first, "écart entre le premier et le deuxième");
        Check(Math.Abs(third - second - 8f) <= 0.5f, $"écart entre le deuxième et le troisième : {third - second}");

        foreach (var pen in badge.PenX)
            Same(MathF.Round(pen), pen, "position sur un pixel entier");
    }

    public static void BadgeInkIsCentered()
    {
        var badge = BadgeLayout.Compute(new[] { NarrowOne, WideTwo }, 7f, 1f, 4f, 15f);
        var inkLeft = badge.PenX[0] + NarrowOne.Left;
        var inkRight = badge.PenX[1] + WideTwo.Right;

        Same((inkLeft + inkRight) / 2f, badge.InkMiddle, "milieu du dessin");
        Same(inkRight - inkLeft + 8f, badge.Width, "même marge de chaque côté");
    }

    // ---------------------------------------------------------------- Onglets et vues

    public static void GeneralHasNoBattle()
    {
        var general = ChatTabPresets.General();
        Check(general.Kinds.Contains(ChatKind.Say), "dire absent");
        Check(general.Kinds.Contains(ChatKind.Other), "les messages inconnus doivent rester visibles");
        Check(!general.Kinds.Any(ChatKinds.IsBattle), "le combat ne doit pas y être");
    }

    public static void BattleFilterBySource()
    {
        var tab = new ChatTabSettings { Kinds = new() { ChatKind.Damage, ChatKind.Say } };
        var mine = Line(ChatKind.Damage, "a", source: ChatSource.Self);
        var party = Line(ChatKind.Damage, "b", source: ChatSource.Party);
        var others = Line(ChatKind.Damage, "c", source: ChatSource.Others);

        Check(tab.Accepts(others), "tous");

        tab.Battle = BattleFilter.Party;
        Check(tab.Accepts(mine) && tab.Accepts(party) && !tab.Accepts(others), "groupe");

        tab.Battle = BattleFilter.SelfOnly;
        Check(tab.Accepts(mine) && !tab.Accepts(party), "moi");

        // Le filtre ne vise que le combat.
        Check(tab.Accepts(Line(ChatKind.Say, "d", source: ChatSource.Others)), "une discussion reste visible");
    }

    public static void FeedFiltersAndAppends()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Say, "un"));
        log.Add(Line(ChatKind.Party, "deux"));

        var feed = Feed(ChatKind.Party);
        feed.Update(log, 0, NoMute);
        Same(1, feed.Visible.Count, "après construction");

        log.Add(Line(ChatKind.Say, "trois"));
        log.Add(Line(ChatKind.Party, "quatre"));
        var rebuilds = feed.RebuildCount;
        feed.Update(log, 0, NoMute);

        Same(2, feed.Visible.Count, "après ajout");
        Same("quatre", feed.Visible[1].BodyText, "ordre");
        Same(rebuilds, feed.RebuildCount, "un ajout ne reconstruit pas toute la liste");
    }

    public static void FeedCountsUnread()
    {
        var log = new ChatLog();
        var feed = Feed(ChatKind.Party, ChatKind.TellIncoming);
        feed.Update(log, 0, NoMute);

        log.Add(Line(ChatKind.Party, "a"));
        log.Add(Line(ChatKind.Party, "b", mentioned: true));
        log.Add(Line(ChatKind.TellIncoming, "c", "Jean"));
        feed.Update(log, 0, NoMute);
        Same(3, feed.Unread, "non lus");
        Same(2, feed.UnreadMentions, "mentions et messages privés");

        feed.MarkRead();
        Same(0, feed.Unread, "après lecture");

        feed.IsActive = true;
        log.Add(Line(ChatKind.Party, "d"));
        feed.Update(log, 0, NoMute);
        Same(0, feed.Unread, "un onglet affiché n'accumule rien");
    }

    public static void HistoryIsNotUnread()
    {
        var log = new ChatLog();
        var feed = Feed(ChatKind.Party);
        feed.Update(log, 0, NoMute);

        var old = Line(ChatKind.Party, "ancien", mentioned: true);
        old.FromHistory = true;
        log.Add(old);
        feed.Update(log, 0, NoMute);

        Same(1, feed.Visible.Count, "visible");
        Same(0, feed.Unread, "non lus");
        Same(0, feed.UnreadMentions, "mentions");
    }

    public static void FeedSearch()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Say, "On part en Raid ce soir", "Jean"));
        log.Add(Line(ChatKind.Say, "Bonne nuit", "Marie"));

        var feed = Feed(ChatKind.Say);
        feed.Update(log, 0, NoMute);

        feed.SetSearch("  RAID ");
        feed.Update(log, 0, NoMute);
        Same(1, feed.Visible.Count, "sur le texte");

        feed.SetSearch("marie");
        feed.Update(log, 0, NoMute);
        Same("Bonne nuit", feed.Visible.Single().BodyText, "sur le nom");

        log.Add(Line(ChatKind.Say, "Marie arrive", "Paul"));
        log.Add(Line(ChatKind.Say, "autre chose", "Paul"));
        feed.Update(log, 0, NoMute);
        Same(2, feed.Visible.Count, "un nouveau message passe aussi par la recherche");

        feed.SetSearch(string.Empty);
        feed.Update(log, 0, NoMute);
        Same(4, feed.Visible.Count, "sans recherche");
    }

    public static void FeedMutes()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Say, "a", "Jean"));
        log.Add(Line(ChatKind.Say, "b", "Marie"));

        var muted = new HashSet<string>();
        bool IsMuted(ChatLine l) => l.Author != null && muted.Contains(l.Author.Text);

        var feed = Feed(ChatKind.Say);
        feed.Update(log, 0, IsMuted);
        Same(2, feed.Visible.Count, "avant");

        muted.Add("Jean");
        feed.Update(log, 1, IsMuted); // le changement de version des filtres reconstruit la liste
        Same("Marie", feed.Visible.Single().AuthorKey, "après");
    }

    public static void FeedRebuildsAfterTrim()
    {
        var log = new ChatLog { Capacity = 100 };
        var feed = Feed(ChatKind.Say);
        feed.Update(log, 0, NoMute);

        for (var i = 0; i < 400; i++)
            log.Add(Line(ChatKind.Say, $"m{i}"));
        feed.Update(log, 0, NoMute);

        Same(log.Lines.Count, feed.Visible.Count, "même nombre");
        Same(log.Lines[0].Id, feed.Visible[0].Id, "même début");
        Same("m399", feed.Visible[^1].BodyText, "le dernier est là");
    }

    private static ChatFeed CollapsingFeed(params ChatKind[] kinds)
    {
        var feed = Feed(kinds);
        feed.CollapseRepeats = true;
        return feed;
    }

    public static void RepeatsCollapse()
    {
        var log = new ChatLog();
        var feed = CollapsingFeed(ChatKind.Shout);
        feed.Update(log, 0, NoMute);

        log.Add(Line(ChatKind.Shout, "WTS gils", "Jean"));
        log.Add(Line(ChatKind.Shout, "WTS gils", "Jean", time: T0.AddMinutes(1)));
        log.Add(Line(ChatKind.Shout, "WTS gils", "Jean", time: T0.AddMinutes(2)));
        feed.Update(log, 0, NoMute);

        Same(1, feed.Visible.Count, "une seule ligne");
        Same(3, feed.RepeatCount(feed.Visible[0]), "trois exemplaires");

        log.Add(Line(ChatKind.Shout, "autre chose", "Jean", time: T0.AddMinutes(3)));
        feed.Update(log, 0, NoMute);
        Same(2, feed.Visible.Count, "un autre texte a sa ligne");
        Same(1, feed.RepeatCount(feed.Visible[1]), "pas répété");
    }

    public static void RepeatsBreak()
    {
        var log = new ChatLog();
        var feed = CollapsingFeed(ChatKind.Say, ChatKind.Damage);
        feed.Update(log, 0, NoMute);

        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        log.Add(Line(ChatKind.Say, "salut", "Marie"));
        log.Add(Line(ChatKind.Say, "salut", "Marie", time: T0.AddMinutes(6)));
        log.Add(Line(ChatKind.Damage, "coup"));
        log.Add(Line(ChatKind.Damage, "coup"));
        feed.Update(log, 0, NoMute);

        Same(5, feed.Visible.Count, "aucun regroupement");
    }

    public static void RepeatsDoNotCountAsUnread()
    {
        var log = new ChatLog();
        var feed = CollapsingFeed(ChatKind.Say);
        feed.Update(log, 0, NoMute);

        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        feed.Update(log, 0, NoMute);

        Same(1, feed.Unread, "un seul non-lu");
    }

    public static void RepeatsSurviveRebuild()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        log.Add(Line(ChatKind.Say, "salut", "Jean"));

        var feed = CollapsingFeed(ChatKind.Say);
        feed.Update(log, 0, NoMute);
        feed.Update(log, 1, NoMute);

        Same(1, feed.Visible.Count, "une ligne");
        Same(2, feed.RepeatCount(feed.Visible[0]), "le compte ne s'additionne pas");
    }

    public static void SpamMatchesRules()
    {
        var filter = new SpamFilter();
        filter.SetRules(new[] { "gils pas chers", " WTS ", "  ", "Épée" });

        Check(filter.Matches(Line(ChatKind.Shout, "Vente de GILS PAS CHERS ici", "Jean")), "expression sans majuscules");
        Check(filter.Matches(Line(ChatKind.Say, "wts mount", "Jean")), "mot");
        Check(filter.Matches(Line(ChatKind.Say, "Je vends une epee", "Jean")), "sans accent");
        Check(!filter.Matches(Line(ChatKind.Say, "On vend des gils ?", "Jean")), "expression incomplète");
    }

    public static void SpamIgnoresSystemAndNames()
    {
        var filter = new SpamFilter();
        filter.SetRules(new[] { "jean" });

        Check(!filter.Matches(Line(ChatKind.System, "jean s'est connecté")), "message système");
        Check(!filter.Matches(Line(ChatKind.Say, "bonjour", "Jean")), "le nom de l'auteur");
        Check(filter.Matches(Line(ChatKind.Say, "salut jean", "Marie")), "le texte");
    }

    private static readonly DateTime Today = new(2026, 11, 3);

    private static bool Finds(string query, ChatLine line) => ChatSearch.Parse(query, Today).Matches(line);

    public static void SearchFilters()
    {
        var line = Line(ChatKind.FreeCompany, "On part en raid", "Jean Dupont", time: T0);

        Check(Finds("de:jean", line), "auteur");
        Check(!Finds("de:marie", line), "autre auteur");
        Check(Finds("canal:CL", line), "canal abrégé");
        Check(Finds("canal:compagnie", line), "canal en entier");
        Check(!Finds("canal:groupe", line), "autre canal");
        Check(Finds("de:jean canal:cl raid", line), "tout ensemble");
        Check(!Finds("de:jean canal:cl donjon", line), "un mot manque");

        Check(Finds("date:03/11/2026", line), "date");
        Check(!Finds("date:02/11/2026", line), "autre date");
    }

    public static void SearchQuotesAndWords()
    {
        var line = Line(ChatKind.Say, "On part en raid ce soir", "Jean Dupont");

        Check(Finds("de:\"jean dupont\"", line), "nom complet entre guillemets");
        Check(Finds("\"en raid\"", line), "expression");
        Check(!Finds("\"raid en\"", line), "expression dans le désordre");
        Check(Finds("raid part", line), "mots dans le désordre");
        Check(ChatSearch.Parse("   ", Today).IsEmpty, "rien à chercher");
    }

    public static void SearchDates()
    {
        var at = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var line = Line(ChatKind.Say, "x", "Jean", time: at);

        Check(Finds("date:hier", line), "hier");
        Check(!Finds("date:aujourd'hui", line), "aujourd'hui");
        Check(Finds("date:02/11", line), "sans année");
        Check(Finds("date:2/11/2026", line), "avec année");
        Check(Finds("date:2026-11-02", line), "format international");

        var lastYear = new DateTime(2025, 12, 25, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Check(Finds("date:25/12", Line(ChatKind.Say, "x", "Jean", time: lastYear)), "une date à venir désigne l'année passée");
    }

    public static void SearchUnknownPrefixIsText()
    {
        Check(Finds("12:30", Line(ChatKind.Say, "rendez-vous à 12:30", "Jean")), "une heure");
        Check(Finds("date:bientot", Line(ChatKind.Say, "date:bientot", "Jean")), "une date illisible");
    }

    public static void FeedSearchIgnoresAccents()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Say, "Une belle Épée", "Éric"));

        var feed = Feed(ChatKind.Say);
        feed.SetSearch("epee de:eric", Today);
        feed.Update(log, 0, NoMute);

        Same(1, feed.Visible.Count, "trouvé");
    }

    public static void NewMarkerIsFirstUnread()
    {
        var log = new ChatLog();
        var feed = Feed(ChatKind.Say);
        feed.Update(log, 0, NoMute);

        log.Add(Line(ChatKind.Say, "un", "Jean"));
        log.Add(Line(ChatKind.Say, "deux", "Jean"));
        feed.Update(log, 0, NoMute);

        Same(feed.Visible[0].Id, feed.FirstUnreadId, "premier non-lu");
        feed.MarkRead();
        Same(feed.Visible[0].Id, feed.FirstUnreadId, "lire l'onglet garde le repère à l'écran");

        var active = Feed(ChatKind.Say);
        active.IsActive = true;
        active.Update(log, 0, NoMute);
        log.Add(Line(ChatKind.Say, "trois", "Jean"));
        active.Update(log, 0, NoMute);
        Same(null, active.FirstUnreadId, "pas de repère dans l'onglet affiché");

        active.MarkNewAt(active.Visible[^1].Id);
        Same(active.Visible[^1].Id, active.FirstUnreadId, "repère posé quand on lisait plus haut");
        active.ClearNewMarker();
        Same(null, active.FirstUnreadId, "repère retiré");
    }

    public static void NewMarkerClearsOnLeave()
    {
        var log = new ChatLog();
        var feed = Feed(ChatKind.Say);
        feed.Update(log, 0, NoMute);
        log.Add(Line(ChatKind.Say, "un", "Jean"));
        feed.Update(log, 0, NoMute);

        feed.IsActive = true;
        Check(feed.FirstUnreadId != null, "encore là à l'ouverture");
        feed.IsActive = false;
        Same(null, feed.FirstUnreadId, "parti en quittant l'onglet");
    }

    public static void DateSeparators()
    {
        var yesterday = new DateTime(2026, 11, 2, 23, 50, 0, DateTimeKind.Local).ToUniversalTime();
        var today = new DateTime(2026, 11, 3, 0, 5, 0, DateTimeKind.Local).ToUniversalTime();
        var first = Line(ChatKind.Say, "a", "Jean", time: yesterday);
        var second = Line(ChatKind.Say, "b", "Jean", time: today);
        var third = Line(ChatKind.Say, "c", "Jean", time: today.AddMinutes(1));

        Same(Today, LineSeparators.Between(first, second, null, Today).Day, "minuit passé");
        Same(null, LineSeparators.Between(second, third, null, Today).Day, "même jour");
        Same(Today.AddDays(-1), LineSeparators.Between(null, first, null, Today).Day, "premier message d'un autre jour");
        Same(null, LineSeparators.Between(null, second, null, Today).Day, "premier message du jour");

        second.Id = 42;
        Check(LineSeparators.Between(first, second, 42, Today).NewMessages, "repère");
        Check(!LineSeparators.Between(first, second, 41, Today).NewMessages, "pas ici");
    }

    private static void Pixel(byte[] image, int width, int x, int y, byte brightness, byte alpha)
    {
        var i = (y * width + x) * 4;
        image[i] = image[i + 1] = image[i + 2] = brightness;
        image[i + 3] = alpha;
    }

    public static void IconInkFound()
    {
        var image = new byte[10 * 10 * 4];
        Pixel(image, 10, 2, 1, 255, 255);
        Pixel(image, 10, 6, 4, 255, 255);
        Pixel(image, 10, 8, 9, 0, 255); // ombre noire, opaque
        Pixel(image, 10, 0, 0, 255, 40); // bord à peine visible

        Same(new IconInk(0.2f, 0.1f, 0.7f, 0.5f), IconInk.Find(image, 10, 10), "dessin");
    }

    public static void IconInkFallback()
    {
        Same(IconInk.Full, IconInk.Find(new byte[4 * 4 * 4], 4, 4), "image vide");
        Same(IconInk.Full, IconInk.Find(new byte[3], 4, 4), "image trop courte");
    }

    public static void SessionBarInEveryTab()
    {
        var log = new ChatLog();
        var party = Feed(ChatKind.Party);
        log.Add(Line(ChatKind.Party, "avant", "Jean"));
        party.Update(log, 0, NoMute);

        log.Add(ChatLine.SessionStart(T0));
        party.Update(log, 0, NoMute);

        Same(2, party.Visible.Count, "visible sans attendre de nouveau message");
        Same(ChatKind.SessionStart, party.Visible[1].Kind, "la barre");
    }

    public static void SessionBarNotDoubled()
    {
        var log = new ChatLog();
        var feed = Feed(ChatKind.Say);
        log.Add(ChatLine.SessionStart(T0));
        feed.Update(log, 0, NoMute);
        Same(0, feed.Visible.Count, "rien à séparer en haut");

        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        log.Add(ChatLine.SessionStart(T0));
        log.Add(ChatLine.SessionStart(T0));
        feed.Update(log, 0, NoMute);
        Same(2, feed.Visible.Count, "une seule barre après le message");

        feed.Update(log, 1, NoMute);
        Same(2, feed.Visible.Count, "pareil après une reconstruction");
    }

    public static void SessionBarIsNotUnread()
    {
        var log = new ChatLog();
        var feed = Feed(ChatKind.Say);
        log.Add(Line(ChatKind.Say, "avant", "Jean"));
        feed.Update(log, 0, NoMute);
        feed.MarkRead();

        var bar = ChatLine.SessionStart(T0);
        log.Add(bar);
        feed.Update(log, 0, NoMute);
        Same(0, feed.Unread, "la barre n'est pas un message à lire");
        Same(null, feed.FirstUnreadId, "ni le repère");

        var next = Line(ChatKind.Say, "après", "Jean");
        log.Add(next);
        feed.Update(log, 0, NoMute);
        Same(1, feed.Unread, "le message suivant compte");
        Check(!LineSeparators.Between(bar, next, feed.FirstUnreadId, Today).NewMessages, "pas deux barres de suite");
    }

    public static void SessionBarHiddenBySearch()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        log.Add(ChatLine.SessionStart(T0));

        var feed = Feed(ChatKind.Say);
        feed.SetSearch("connexion", Today);
        feed.Update(log, 0, NoMute);
        Same(0, feed.Visible.Count, "recherche sur son texte");

        feed.SetSearch($"date:{T0.ToLocalTime():dd/MM/yyyy}", Today);
        feed.Update(log, 0, NoMute);
        Check(feed.Visible.All(l => l.Kind != ChatKind.SessionStart), "recherche par date");
    }

    public static void SessionBarIsSaved()
    {
        var directory = TempDirectory();
        var store = Store(directory);
        store.Enqueue(ChatLine.SessionStart(T0));
        store.Flush();

        var read = Store(directory).Load(10).Single();
        Same(ChatKind.SessionStart, read.Kind, "relue");
        Same("Nouvelle connexion", read.BodyText, "son texte");
    }

    public static void RepeatsStopAtSession()
    {
        var log = new ChatLog();
        var feed = CollapsingFeed(ChatKind.Say);
        feed.Update(log, 0, NoMute);

        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        log.Add(ChatLine.SessionStart(T0));
        log.Add(Line(ChatKind.Say, "salut", "Jean"));
        feed.Update(log, 0, NoMute);

        Same(3, feed.Visible.Count, "le message, la barre, le message");
    }

    public static void DateLabels()
    {
        Same("Aujourd'hui", ChatDates.Label(Today, Today), "aujourd'hui");
        Same("Hier", ChatDates.Label(Today.AddDays(-1), Today), "hier");
        Same("Dimanche 1er novembre", ChatDates.Label(new DateTime(2026, 11, 1), Today), "premier du mois");
        Same("Jeudi 25 décembre 2025", ChatDates.Label(new DateTime(2025, 12, 25), Today), "autre année");
    }

    public static void ExportFormat()
    {
        var at = new DateTime(2026, 11, 3, 19, 50, 0, DateTimeKind.Local).ToUniversalTime();
        var first = Line(ChatKind.FreeCompany, "bonsoir \uE0BB!", "Jean", time: at);
        var second = Line(ChatKind.System, "Connexion", time: at);

        var text = ChatExporter.Format(new[] { first, second }, l => ReferenceEquals(l, first) ? 3 : 1);

        Same("2026-11-03 19:50 [Compagnie libre] Jean: bonsoir ! (x3)\n2026-11-03 19:50 [Système] Connexion\n", text, "texte");
    }

    public static void MacroExpansion()
    {
        string? Resolve(string name) => name switch
        {
            "t" => "Jean",
            "flag" => "Limsa ( 10 , 11 )",
            _ => null,
        };

        Same("Salut Jean à Limsa ( 10 , 11 )", MacroPreview.Expand("Salut <T> à <flag>", Resolve, out var changed), "remplacés");
        Check(changed, "changement signalé");

        Same("<inconnu> a<b", MacroPreview.Expand("<inconnu> a<b", Resolve, out changed), "laissés tels quels");
        Check(!changed, "rien de remplacé");

        Same("sans raccourci", MacroPreview.Expand("sans raccourci", Resolve, out changed), "texte normal");
    }

    public static void TrimRemovesBattleFirst()
    {
        var log = new ChatLog { Capacity = 100 };
        log.Add(Line(ChatKind.Say, "à garder"));
        for (var i = 0; i < 300; i++)
            log.Add(Line(ChatKind.Damage, $"coup {i}"));

        Check(log.Lines.Count <= 100 + 50, $"limite : {log.Lines.Count}");
        Same("à garder", log.Lines[0].BodyText, "la discussion reste");
    }

    public static void TrimKeepsNewest()
    {
        var log = new ChatLog { Capacity = 100 };
        for (var i = 0; i < 1000; i++)
            log.Add(Line(ChatKind.Say, $"m{i}"));

        Check(log.Lines.Count <= 150, $"limite : {log.Lines.Count}");
        Same("m999", log.Lines[^1].BodyText, "le plus récent");
    }

    // ---------------------------------------------------------------- Groupes

    public static void GroupingSameAuthor()
    {
        var first = Line(ChatKind.Party, "a", "Jean");
        var second = Line(ChatKind.Party, "b", "Jean", time: T0.AddMinutes(2));
        Check(ChatGrouping.StartsGroup(null, first), "le premier a un en-tête");
        Check(!ChatGrouping.StartsGroup(first, second), "le second se glisse dessous");
    }

    public static void GroupingBreaks()
    {
        var first = Line(ChatKind.Party, "a", "Jean");
        Check(ChatGrouping.StartsGroup(first, Line(ChatKind.Party, "b", "Marie")), "autre auteur");
        Check(ChatGrouping.StartsGroup(first, Line(ChatKind.Say, "b", "Jean")), "autre canal");
        Check(ChatGrouping.StartsGroup(first, Line(ChatKind.Party, "b", "Jean", time: T0.AddMinutes(6))), "long délai");
    }

    public static void GroupingSystem()
    {
        var system = Line(ChatKind.System, "Connexion");
        Check(!ChatGrouping.StartsGroup(null, system), "pas d'en-tête");
        Check(ChatGrouping.StartsGroup(system, Line(ChatKind.Party, "b", "Jean")), "après un message système, l'en-tête revient");
    }

    // ---------------------------------------------------------------- Mise en page

    // Chaque caractère fait 10 de large : les résultats se lisent facilement.
    private static float Measure(string text) => text.Length * 10f;

    private static List<ChatSpan> Spans(params string[] texts) => texts.Select(t => new ChatSpan(t)).ToList();

    private static List<string> LinesOf(LaidOutText layout)
    {
        var lines = new string[layout.LineCount];
        foreach (var run in layout.Runs)
            lines[run.Line] += run.Text;
        return lines.Select(l => l ?? string.Empty).ToList();
    }

    public static void WrapShortText()
    {
        var layout = TextLayout.Wrap(Spans("bonjour"), 200f, Measure);
        Same(1, layout.LineCount, "lignes");
        Same(70f, layout.Runs[0].Width, "largeur");
    }

    public static void WrapAtSpaces()
    {
        var layout = TextLayout.Wrap(Spans("un deux trois quatre"), 80f, Measure);
        var lines = LinesOf(layout);
        Same("un deux ", lines[0], "ligne 1");
        Same("trois ", lines[1], "ligne 2");
        Same("quatre", lines[2], "ligne 3");
    }

    public static void WrapLongWord()
    {
        var layout = TextLayout.Wrap(Spans("abcdefghijklmnop"), 50f, Measure);
        var lines = LinesOf(layout);
        Same(4, layout.LineCount, "lignes");
        Same("abcde", lines[0], "ligne 1");
        Same("p", lines[3], "ligne 4");
    }

    public static void WrapCjk()
    {
        var layout = TextLayout.Wrap(Spans("こんにちは世界"), 30f, Measure);
        var lines = LinesOf(layout);
        Same(3, layout.LineCount, "lignes");
        Same("こんに", lines[0], "ligne 1");
    }

    public static void WrapKeepsSpans()
    {
        var layout = TextLayout.Wrap(Spans("rouge ", "bleu"), 200f, Measure);
        Same(2, layout.Runs.Count, "deux morceaux");
        Same(0, layout.Runs[0].SpanIndex, "premier");
        Same(1, layout.Runs[1].SpanIndex, "second");
        Same(60f, layout.Runs[1].X, "le second suit le premier");
    }

    public static void WrapNewLine()
    {
        var lines = LinesOf(TextLayout.Wrap(Spans("a\nb"), 200f, Measure));
        Same("a", lines[0], "ligne 1");
        Same("b", lines[1], "ligne 2");
    }

    public static void WrapIndent()
    {
        var layout = TextLayout.Wrap(Spans("aaaa bbbb"), 60f, Measure, 0f, 20f);
        Same(2, layout.LineCount, "lignes");
        Same(20f, layout.Runs[1].X, "retrait");
    }

    public static void WrapLosesNothing()
    {
        const string text = "Un message assez long, avec de la ponctuation ! Et unmotvraimenttrèstrèstrèslongquidébordedelaligne puis こんにちは à la fin.";
        foreach (var width in new[] { 15f, 40f, 73f, 100f, 333f })
        {
            var layout = TextLayout.Wrap(Spans(text), width, Measure);
            var joined = string.Concat(layout.Runs.Select(r => r.Text));
            Same(text.Replace(" ", string.Empty), joined.Replace(" ", string.Empty), $"largeur {width}");
        }
    }

    public static void StackFindsFirstVisible()
    {
        var stack = new LineStack();
        stack.Add(10f);
        stack.Add(20f);
        stack.Add(30f);

        Same(60f, stack.Total, "total");
        Same(0, stack.FirstVisible(0f), "haut");
        Same(0, stack.FirstVisible(9f), "dans le premier");
        Same(1, stack.FirstVisible(10f), "début du second");
        Same(2, stack.FirstVisible(45f), "dans le dernier");
        Same(2, stack.FirstVisible(500f), "au-delà");
        Same(30f, stack.Bottom(1), "bas du second");
    }

    // ---------------------------------------------------------------- Saisie

    public static void NormalizeText()
    {
        Same("un deux trois", ChatText.Normalize("  un\r\ndeux\ttrois \n"), "espaces");
        Same("ab", ChatText.Normalize("a\u0001b"), "contrôle");
        Same(string.Empty, ChatText.Normalize(" \n "), "vide");
    }

    public static void ByteLength()
    {
        Same(1, ChatText.ByteLength("e"), "lettre");
        Same(2, ChatText.ByteLength("é"), "accent");
        Same(3, ChatText.ByteLength("世"), "idéogramme");
    }

    public static void InputHistoryNavigation()
    {
        var history = new InputHistory();
        history.Add("un");
        history.Add("deux");

        Same("deux", history.Older("brouillon"), "le plus récent");
        Same("un", history.Older("deux"), "le précédent");
        Same(null, history.Older("un"), "plus rien");
        Same("deux", history.Newer(), "retour");
        Same("brouillon", history.Newer(), "le brouillon revient");
        Same(null, history.Newer(), "plus rien après");
    }

    public static void InputHistoryDedupes()
    {
        var history = new InputHistory(limit: 2);
        history.Add("a");
        history.Add("a");
        Same(1, history.Count, "pas de doublon");

        history.Add("b");
        history.Add("c");
        Same(2, history.Count, "limite");
        Same("c", history.Older(string.Empty), "le dernier");
        Same("b", history.Older("c"), "l'ancien");
        Same(null, history.Older("b"), "le premier est parti");
    }

    public static void SuggestOrder()
    {
        var suggester = new CommandSuggester();
        suggester.SetCommands(new[]
        {
            new CommandSuggestion("/bchat", "chat"),
            new CommandSuggestion("/chatmode", "mode"),
            new CommandSuggestion("/chat", "court"),
            new CommandSuggestion("/CHAT", ""),
            new CommandSuggestion("pasunecommande", ""),
        });

        var result = suggester.Suggest("/chat", 8).Select(s => s.Command).ToList();
        Same("/chat", result[0], "le plus court d'abord");
        Same("/chatmode", result[1], "puis le début");
        Same("/bchat", result[2], "puis le milieu");
        Same(3, result.Count, "sans doublon ni fausse commande");
        Same("court", suggester.Suggest("/chat", 1)[0].Description, "la description la plus complète est gardée");
    }

    public static void SuggestStopsAfterSpace()
    {
        var suggester = new CommandSuggester();
        suggester.SetCommands(new[] { new CommandSuggestion("/party", string.Empty) });
        Same(1, suggester.Suggest("/par", 8).Count, "pendant la commande");
        Same(0, suggester.Suggest("/party salut", 8).Count, "après l'espace");
        Same(0, suggester.Suggest("party", 8).Count, "sans la barre");
    }

    public static void MentionsWholeWords()
    {
        var matcher = new MentionMatcher();
        matcher.SetWords(new[] { "Maxime", " ", "raid" });

        Check(matcher.Matches("salut maxime !"), "minuscules");
        Check(matcher.Matches("MAXIME"), "majuscules");
        Check(matcher.Matches("on part en raid."), "ponctuation");
        Check(!matcher.Matches("Maximes"), "dans un mot plus long");
        Check(!matcher.Matches("braid"), "à la fin d'un mot");
        Check(!matcher.Matches("rien à signaler"), "absent");
    }

    public static void UrlSplit()
    {
        var spans = UrlFinder.Split(new List<ChatSpan> { new("Voir https://exemple.fr/page. Merci", 0x11223344) });
        Same(3, spans.Count, "morceaux");
        Same("Voir ", spans[0].Text, "avant");
        Same("https://exemple.fr/page", spans[1].Text, "sans le point final");
        Same(LinkKind.Url, spans[1].Link!.Kind, "lien");
        Same(". Merci", spans[2].Text, "après");
        Same(0x11223344u, spans[2].Color, "couleur gardée");
    }

    public static void UrlKeepsExistingLinks()
    {
        var item = new ChatSpan("www.objet.fr", 0, false, ChatLink.Item(5, "Objet"));
        var spans = UrlFinder.Split(new List<ChatSpan> { item, new("sans adresse.") });
        Same(2, spans.Count, "rien à découper");
        Check(ReferenceEquals(item, spans[0]), "lien d'origine");
        Same(null, spans[1].Link, "pas de lien inventé");
    }

    public static void UrlAddsScheme()
    {
        var spans = UrlFinder.Split(new List<ChatSpan> { new("www.exemple.fr") });
        Same("https://www.exemple.fr", spans.Single().Link!.Text, "adresse");
        Same("www.exemple.fr", spans.Single().Text, "affichage");
    }

    public static void RecentTellsOrder()
    {
        var tells = new RecentTells();
        tells.Note(ChatLink.Player("Jean", "Ragnarok"));
        tells.Note(ChatLink.Player("Marie", "Odin"));
        tells.Note(ChatLink.Player("jean", "ragnarok"));

        Same(2, tells.People.Count, "sans doublon");
        Same("jean@ragnarok", tells.People[0].TellTarget, "le plus récent");
        Same("Marie@Odin", tells.People[1].TellTarget, "ensuite");
    }

    // ---------------------------------------------------------------- Sauvegarde

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "BetterChatTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ChatHistoryStore Store(string directory, ulong character = 1)
    {
        var store = new ChatHistoryStore(directory, (ex, what) => throw new Exception($"{what} : {ex.Message}"));
        store.SelectCharacter(character);
        return store;
    }

    public static void StoreRoundTrip()
    {
        var directory = TempDirectory();
        var store = Store(directory);

        var line = new ChatLine
        {
            TimeUtc = T0,
            Kind = ChatKind.Party,
            Source = ChatSource.Party,
            Sender = new() { new ChatSpan("Jean", 0, false, ChatLink.Player("Jean", "Ragnarok")) },
            Body = new()
            {
                new ChatSpan("va en ", 0x11223344),
                new ChatSpan("Limsa", 0, true, ChatLink.Map(129, 12, 300, -400, "Limsa Lominsa")),
                new ChatSpan(" avec ", 0),
                new ChatSpan("Épée", 0, false, ChatLink.Item(1234, "Épée")),
            },
            Author = ChatLink.Player("Jean", "Ragnarok"),
            Mentioned = true,
        };
        store.Enqueue(line);
        store.Flush();

        var read = Store(directory).Load(10).Single();
        Same(T0, read.TimeUtc, "heure");
        Same(ChatKind.Party, read.Kind, "type");
        Same(ChatSource.Party, read.Source, "origine");
        Same(true, read.Mentioned, "mention");
        Same(true, read.FromHistory, "marqué comme relu");
        Same("Jean@Ragnarok", read.Author!.TellTarget, "auteur");
        Same(line.BodyText, read.BodyText, "texte");
        Same(0x11223344u, read.Body[0].Color, "couleur");
        Same(true, read.Body[1].Italic, "italique");
        Same(-400, read.Body[1].Link!.RawY, "carte");
        Same(1234u, read.Body[3].Link!.Id, "objet");
    }

    public static void StoreKeepsAccents()
    {
        var directory = TempDirectory();
        var store = Store(directory);
        store.Enqueue(Line(ChatKind.Say, "Évidemment ça marche, 世界 ", "Éric"));
        store.Flush();

        var read = Store(directory).Load(10).Single();
        Same("Évidemment ça marche, 世界 ", read.BodyText, "texte");
        Same("Éric", read.SenderText, "nom");
    }

    public static void StoreSkipsCorruptLine()
    {
        var directory = TempDirectory();
        var store = Store(directory);
        store.Enqueue(Line(ChatKind.Say, "avant"));
        store.Flush();

        var file = Directory.GetFiles(directory).Single();
        File.AppendAllText(file, "{ceci n'est pas du JSON\n\n");

        store.Enqueue(Line(ChatKind.Say, "après"));
        store.Flush();

        var lines = Store(directory).Load(10);
        Same(2, lines.Count, "lignes valides");
        Same("après", lines[1].BodyText, "la suite est lue");
    }

    public static void StoreLoadsNewest()
    {
        var directory = TempDirectory();
        var store = Store(directory);
        for (var i = 0; i < 10; i++)
            store.Enqueue(Line(ChatKind.Say, $"m{i}"));
        store.Flush();

        var lines = Store(directory).Load(3);
        Same(3, lines.Count, "nombre");
        Same("m7", lines[0].BodyText, "début");
        Same("m9", lines[2].BodyText, "fin");
    }

    public static void StoreTrimsFile()
    {
        var directory = TempDirectory();
        var store = Store(directory);
        for (var i = 0; i < 100; i++)
            store.Enqueue(Line(ChatKind.Say, $"m{i}"));
        store.Flush();

        Store(directory).Load(10);

        Same(10, File.ReadAllLines(Directory.GetFiles(directory).Single()).Length, "le fichier est réduit");
        Same(1, Directory.GetFiles(directory).Length, "aucun fichier temporaire");
        Same("m99", Store(directory).Load(10)[^1].BodyText, "le dernier est gardé");
    }

    public static void StorePerCharacter()
    {
        var directory = TempDirectory();
        var store = Store(directory, 1);
        store.Enqueue(Line(ChatKind.Say, "premier"));
        store.SelectCharacter(2); // ce qui attend part dans le fichier du personnage précédent
        store.Enqueue(Line(ChatKind.Say, "second"));
        store.Flush();

        Same("premier", Store(directory, 1).Load(10).Single().BodyText, "personnage 1");
        Same("second", Store(directory, 2).Load(10).Single().BodyText, "personnage 2");
    }

    public static void StoreDelete()
    {
        var directory = TempDirectory();
        var store = Store(directory);
        store.Enqueue(Line(ChatKind.Say, "écrit"));
        store.Flush();
        store.Enqueue(Line(ChatKind.Say, "en attente"));

        store.Delete();
        store.Flush();

        Same(0, Store(directory).Load(10).Count, "plus rien");
    }
}
