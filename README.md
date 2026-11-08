# BetterChat

Un plugin [Dalamud](https://github.com/goatcorp/Dalamud) pour Final Fantasy XIV : une fenêtre de chat moderne à la place du chat du jeu. Aucun autre logiciel n'est nécessaire.

Tes messages partent toujours par le chat du jeu, sur le même canal et avec les mêmes commandes. Le plugin ne change que ce que tu vois, et n'envoie rien sans que tu appuies sur Entrée.

## Installation

1. En jeu, ouvre `/xlsettings` → onglet **Expérimental** → **Dépôts de plugins personnalisés**.
2. Ajoute cette adresse, coche la case, puis enregistre :
   ```
   https://raw.githubusercontent.com/Powerl3ss/BetterChat/main/repo.json
   ```
3. Ouvre `/xlplugins`, cherche **BetterChat** et installe-le.

Si tu utilises Chat 2, désactive-le : les deux plugins se disputeraient la touche Entrée. BetterChat le détecte et ne masque rien tant qu'il est chargé.

## Ce que ça apporte

**Lire**
- Deux présentations : moderne (le nom au-dessus de messages regroupés par auteur) ou compacte (tout sur une ligne).
- Onglets à ta façon : chacun choisit ses canaux. Pastille de messages non lus, rouge pour une mention ou un message privé.
- Onglets détachables : sors un onglet dans sa propre fenêtre avec sa zone de saisie, sa recherche et son propre verrou, par exemple le combat ou la compagnie libre dans un coin de l'écran.
- Recherche dans l'onglet affiché (la loupe), par mot, joueur, canal ou date : `de:Jean canal:CL date:hier`.
- Noms, objets, cartes, quêtes, effets, annonces de recherche de groupe et adresses web cliquables. Survoler un objet, une quête ou un effet montre ses détails, et le clic droit sur un objet ouvre son craft s'il en a un. En option, le survol d'un objet donne aussi son prix le plus bas au marché, sur ton monde et ton centre de données (demandé au site Universalis, désactivé par défaut). Une adresse s'affiche en entier avant de s'ouvrir. Les liens ajoutés par les autres plugins (« Show Death Recap »...) marchent comme dans le chat du jeu.
- Une ligne quand la date change.
- Messages épinglés : garde un message important en haut d'un onglet (clic droit sur le message).
- Messages privés par personne : une pastille par joueur au-dessus de l'onglet, pour suivre chaque conversation à part.
- Selon le contexte : en donjon, en raid ou en ville, le chat peut changer d'onglet et masquer des canaux.
- À côté du nom, au choix comme sur les plaques du jeu : l'icône de job, le rôle (tank, soigneur, DPS) ou le statut (mentor, nouvel aventurier, absent...). Et une couleur au choix pour repérer tes amis.
- Anti-spam : masque les messages selon des mots ou des expressions, et regroupe les messages répétés en « x3 ».
- Export d'une conversation dans un fichier texte (clic droit sur l'onglet).
- Les icônes du jeu dans les messages, et la sélection de texte à la souris (clic droit pour copier).
- Des réglages rangés par thème, avec un accueil en tableau de bord (activité de la semaine, messages du jour, non lus) et un aperçu du chat qui suit chaque changement d'affichage.
- Tes messages sont retrouvés au prochain lancement (sur ton PC uniquement, un fichier par personnage, jamais le combat).

**Écrire**
- Entrée ouvre la saisie, comme dans le jeu. La pastille de couleur montre où partira le message, et suit ce que tu tapes (`/p bonjour`).
- Dans une saisie vide, Tab passe au canal suivant, parmi ceux que tu peux vraiment utiliser (groupe, compagnie, linkshells...).
- Autotraduction : Tab après un mot, ou le bouton à droite de la saisie, le cherche dans la liste du jeu. Tab ou Entrée valide le mot choisi.
- Les flèches rappellent tes derniers messages, sans perdre ce que tu étais en train d'écrire.
- En tapant `/`, les commandes du jeu et des plugins sont proposées et se complètent avec Tab.
- Un aperçu montre ce que donneront `<t>`, `<flag>` et les autres raccourcis du jeu avant l'envoi.
- Répondre à un message (clic droit) : la saisie passe sur son canal, ou en message privé avec la bonne personne.
- Un message trop long pour le jeu part en plusieurs messages, coupés entre deux mots et de préférence en fin de phrase. Un compteur s'affiche à l'approche de la limite.
- Un clic sur un nom : message privé, inviter dans le groupe, examiner, plaque d'aventurier, ajouter en ami, copier, changer la couleur, masquer les messages. Les derniers correspondants restent dans le menu des canaux.

**Être prévenu**
- Ton nom et les mots que tu choisis surlignent le message, avec un son si tu veux.
- Un son à la réception d'un message privé, et le jeu clignote dans la barre des tâches si tu es sur une autre fenêtre.

## Commandes

| Commande | Effet |
|---|---|
| `/bchat` | Affiche / masque BetterChat (masqué, le chat du jeu revient) |
| `/bchat config` | Ouvre les réglages |
| `/bchat debug` | Affiche les compteurs de diagnostic dans le chat |

`/betterchat` fonctionne aussi, si `/bchat` est déjà pris par un autre plugin.

## Si quelque chose ne va pas

Le chat du jeu revient dès que le plugin est désactivé, ou avec `/bchat`.

Si Entrée n'ouvre plus la saisie, clique dans la zone de saisie : elle fonctionne toujours. Ouvre ensuite `/bchat config` → **À propos** → **Diagnostic** : si le compteur d'ouvertures reste à zéro, le jeu a sans doute changé sa façon d'ouvrir le chat.

## Limites connues

- Le texte en italique du jeu est seulement un peu atténué.

## Structure

```
BetterChat/
├── Plugin.cs               point d'entrée : services, /bchat, réception des messages
├── Configuration.cs        réglages sauvegardés
├── Chat/
│   ├── GameChat.cs         masquer le chat du jeu, touche d'ouverture, canal actif, envoi
│   ├── MessageConverter.cs message du jeu vers texte coloré et liens
│   ├── ChannelColors.cs    couleur de chaque canal
│   ├── GameIconAtlas.cs    dessin des icônes du jeu dans les messages
│   ├── MacroResolver.cs    aperçu des raccourcis du jeu (<t>, <flag>...)
│   └── WindowAlert.cs      clignotement dans la barre des tâches
├── Core/                   logique sans dépendance au jeu (testée à part)
│   ├── ChatKinds.cs        types de messages et noms affichés
│   ├── ChatLine.cs         un message, regroupement par auteur
│   ├── ChatLog.cs          messages en mémoire, onglets, non lus, recherche
│   ├── TextLayout.cs       découpage du texte en lignes
│   ├── ChatInput.cs        saisie : historique, canaux, commandes, mentions, liens
│   ├── ChatSearch.cs       recherche avancée, anti-spam, séparateurs, export, aperçu
│   ├── GameIcons.cs        icônes du jeu dans le texte, lecture de leur table
│   └── ChatHistoryStore.cs messages enregistrés sur le disque
├── images/                 logo
└── Windows/
    ├── ChatWindow*.cs      la fenêtre de chat et ses onglets
    ├── ChatInputBar.cs     la zone de saisie, commune au chat et aux onglets détachés
    ├── MessageList.cs      la liste des messages, commune au chat et aux onglets détachés
    ├── DetachedTabWindow.cs un onglet dans sa propre fenêtre
    └── ConfigWindow*.cs    les réglages (même base que DailyToDo et DPSmeter)
Tests/BetterChat.Tests/     tests de la logique : dotnet run depuis ce dossier
```
