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
    ("Onglets : le général ne contient pas le combat", Tests.GeneralHasNoBattle),
    ("Onglets : filtre du combat par origine", Tests.BattleFilterBySource),
    ("Vue : filtre de l'onglet et nouveaux messages", Tests.FeedFiltersAndAppends),
    ("Vue : messages non lus et mentions", Tests.FeedCountsUnread),
    ("Vue : l'historique relu ne compte pas comme non lu", Tests.HistoryIsNotUnread),
    ("Vue : recherche sur le nom et le texte", Tests.FeedSearch),
    ("Vue : un joueur masqué disparaît", Tests.FeedMutes),
    ("Vue : le nettoyage reconstruit la liste", Tests.FeedRebuildsAfterTrim),
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
