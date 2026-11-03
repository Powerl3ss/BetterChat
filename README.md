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
- Recherche dans l'onglet affiché (Ctrl + F), par mot ou par joueur.
- Noms, objets, cartes et adresses web cliquables. Survoler un objet montre son icône et son niveau. Une adresse s'affiche en entier avant de s'ouvrir.
- Tes messages sont retrouvés au prochain lancement (sur ton PC uniquement, un fichier par personnage, jamais le combat).
- Au repos, le cadre s'efface doucement pour libérer l'écran, puis revient au survol ou à l'arrivée d'un message.

**Écrire**
- Entrée ouvre la saisie, comme dans le jeu. La pastille de couleur montre où partira le message, et suit ce que tu tapes (`/p bonjour`).
- Tab passe au canal suivant, parmi ceux que tu peux vraiment utiliser (groupe, compagnie, linkshells...).
- Les flèches rappellent tes derniers messages, sans perdre ce que tu étais en train d'écrire.
- En tapant `/`, les commandes du jeu et des plugins sont proposées et se complètent avec Tab.
- Un clic sur un nom : message privé, copier, masquer ses messages. Les derniers correspondants restent dans le menu des canaux.

**Être prévenu**
- Ton nom et les mots que tu choisis surlignent le message, avec un son si tu veux.
- Un son à la réception d'un message privé.

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

- Pas de liste d'autotraduction.
- Pas de sélection de texte à la souris : clic droit sur un message pour le copier.
- Le texte en italique du jeu est seulement un peu atténué, et les icônes dans les messages ne sont pas dessinées.

## Structure

```
BetterChat/
├── Plugin.cs               point d'entrée : services, /bchat, réception des messages
├── Configuration.cs        réglages sauvegardés
├── Chat/
│   ├── GameChat.cs         masquer le chat du jeu, touche d'ouverture, canal actif, envoi
│   ├── MessageConverter.cs message du jeu vers texte coloré et liens
│   └── ChannelColors.cs    couleur de chaque canal
├── Core/                   logique sans dépendance au jeu (testée à part)
│   ├── ChatKinds.cs        types de messages et noms affichés
│   ├── ChatLine.cs         un message, regroupement par auteur
│   ├── ChatLog.cs          messages en mémoire, onglets, non lus, recherche
│   ├── TextLayout.cs       découpage du texte en lignes
│   ├── ChatInput.cs        saisie : historique, canaux, commandes, mentions, liens
│   └── ChatHistoryStore.cs messages enregistrés sur le disque
├── images/                 logo
└── Windows/
    ├── ChatWindow*.cs      la fenêtre de chat : onglets, messages, saisie
    └── ConfigWindow*.cs    les réglages (même base que DailyToDo et DPSmeter)
Tests/BetterChat.Tests/     tests de la logique : dotnet run depuis ce dossier
```
