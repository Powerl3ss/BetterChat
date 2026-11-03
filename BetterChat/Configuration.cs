using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using BetterChat.Core;

namespace BetterChat;

/// <summary>Présentation des messages.</summary>
public enum ChatStyle
{
    /// <summary>Le nom au-dessus des messages, comme une messagerie moderne.</summary>
    Modern,

    /// <summary>Tout sur une ligne, pour voir plus de messages.</summary>
    Compact,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Fenêtre ---
    public bool ChatVisible { get; set; } = true;
    public bool HideNativeChat { get; set; } = true;
    public bool Locked { get; set; } = false;
    public float WindowOpacity { get; set; } = 0.82f;

    // --- Messages ---
    public ChatStyle Style { get; set; } = ChatStyle.Modern;
    public float FontSize { get; set; } = 16f;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowChannelTags { get; set; } = true;
    public bool UseGameColors { get; set; } = true;
    public bool TextOutline { get; set; } = true;
    public float OutlineStrength { get; set; } = 0.90f;
    public Dictionary<ChatKind, Vector4> ChannelColors { get; set; } = new();
    public bool ShowSeparators { get; set; } = true;
    public bool ShowJobIcons { get; set; } = true;
    public float JobIconHeight { get; set; } = 100f; // hauteur du dessin de l'icône, en % de la ligne de texte
    public Dictionary<string, Vector4> PlayerColors { get; set; } = new();

    // --- Saisie ---
    public bool KeepFocusAfterSend { get; set; } = false;
    public bool CommandSuggestions { get; set; } = true;
    public bool ShowMacroPreview { get; set; } = true;

    // --- Onglets ---
    // Vide par défaut : le fichier relu s'ajouterait à une liste préremplie. Le plugin la remplit au premier lancement.
    public List<ChatTabSettings> Tabs { get; set; } = new();
    public int ActiveTab { get; set; } = 0;

    // --- Alertes ---
    public bool MentionOwnName { get; set; } = true;
    public List<string> MentionWords { get; set; } = new();
    public bool SoundOnMention { get; set; } = true;
    public bool SoundOnTell { get; set; } = true;
    public bool FlashOnTell { get; set; } = true;
    public int AlertSound { get; set; } = 1; // de 1 à 16, comme <se.1> dans le jeu

    // --- Joueurs masqués ---
    public List<string> MutedPlayers { get; set; } = new();

    // --- Anti-spam ---
    public List<string> SpamWords { get; set; } = new();
    public bool CollapseRepeats { get; set; } = true;

    // --- Historique ---
    public bool KeepHistory { get; set; } = true;
    public int HistoryLines { get; set; } = 2000;
    public int MemoryLines { get; set; } = 8000;

    // --- Couleurs ---
    public Vector4 AccentColor { get; set; } = new(0.30f, 0.65f, 1.00f, 1.00f);
    public Vector4 SecondaryColor { get; set; } = new(0.60f, 0.64f, 0.72f, 1.00f);

    // --- Fenêtre de réglages ---
    public bool AnimatedSettingsBackground { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
