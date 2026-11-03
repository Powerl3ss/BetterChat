using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterChat.Chat;

/// <summary>Fait clignoter le jeu dans la barre des tâches, seulement si tu es sur une autre fenêtre.</summary>
internal static class WindowAlert
{
    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    public static void FlashIfInBackground()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero || GetForegroundWindow() == window)
                return;

            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = window,
                Flags = FlashTray | FlashUntilForeground,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            // Une alerte manquée ne doit pas gêner la réception du message.
            Plugin.Log.Warning(ex, "Impossible de faire clignoter la fenêtre du jeu");
        }
    }
}
