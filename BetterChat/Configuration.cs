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

    // --- Discrétion quand le chat ne sert pas ---
    public bool IdleFade { get; set; } = true;
    public int IdleSeconds { get; set; } = 12;
    public float IdleFrameOpacity { get; set; } = 0.0f;
    public float IdleTextOpacity { get; set; } = 0.85f;

    // --- Messages ---
    public ChatStyle Style { get; set; } = ChatStyle.Modern;
    public float FontSize { get; set; } = 16f;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowChannelTags { get; set; } = true;
    public bool UseGameColors { get; set; } = true;
    public bool TextShadow { get; set; } = true;
    public Dictionary<ChatKind, Vector4> ChannelColors { get; set; } = new();

    // --- Saisie ---
    public bool KeepFocusAfterSend { get; set; } = false;
    public bool CommandSuggestions { get; set; } = true;

    // --- Onglets ---
    // Vide par défaut : le fichier relu s'ajouterait à une liste préremplie. Le plugin la remplit au premier lancement.
    public List<ChatTabSettings> Tabs { get; set; } = new();
    public int ActiveTab { get; set; } = 0;

    // --- Alertes ---
    public bool MentionOwnName { get; set; } = true;
    public List<string> MentionWords { get; set; } = new();
    public bool SoundOnMention { get; set; } = true;
    public bool SoundOnTell { get; set; } = true;
    public int AlertSound { get; set; } = 1; // de 1 à 16, comme <se.1> dans le jeu

    // --- Joueurs masqués ---
    public List<string> MutedPlayers { get; set; } = new();

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
