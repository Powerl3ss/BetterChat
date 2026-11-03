using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace BetterChat.Core;

/// <summary>
/// Garde les messages d'un personnage sur le disque, pour les retrouver au prochain lancement. Un message par ligne
/// dans un fichier JSON Lines : ajouter ne réécrit pas tout, et une ligne abîmée n'empêche pas de relire les autres.
/// </summary>
public sealed class ChatHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string directory;
    private readonly Action<Exception, string>? logError;
    private readonly object sync = new();
    private readonly List<string> queue = new();
    private Task writing = Task.CompletedTask;
    private string? path;

    public ChatHistoryStore(string directory, Action<Exception, string>? logError = null)
    {
        this.directory = directory;
        this.logError = logError;
    }

    /// <summary>Choisit le fichier du personnage. Ce qui attendait d'être écrit part dans l'ancien fichier.</summary>
    public void SelectCharacter(ulong characterId)
    {
        Flush();
        lock (sync)
            path = characterId == 0 ? null : Path.Combine(directory, $"{characterId:X16}.jsonl");
    }

    /// <summary>Relit les derniers messages et réduit le fichier s'il a trop grandi.</summary>
    public List<ChatLine> Load(int maxLines)
    {
        Flush();
        return ReadRecent(maxLines);
    }

    /// <summary>
    /// Comme Load, mais sans bloquer le jeu : la lecture passe dans la file d'écriture,
    /// après ce qui attend d'être écrit, et rien d'autre n'écrit le fichier pendant ce temps.
    /// </summary>
    public Task<List<ChatLine>> LoadInBackground(int maxLines)
    {
        lock (sync)
        {
            var task = writing.ContinueWith(_ =>
            {
                WriteQueue();
                return ReadRecent(maxLines);
            }, TaskScheduler.Default);
            writing = task;
            return task;
        }
    }

    private List<ChatLine> ReadRecent(int maxLines)
    {
        var result = new List<ChatLine>();
        string? file;
        lock (sync)
            file = path;

        if (file == null || maxLines <= 0)
            return result;

        try
        {
            if (!File.Exists(file))
                return result;

            var raw = File.ReadAllLines(file, Utf8);
            var first = Math.Max(0, raw.Length - maxLines);
            for (var i = first; i < raw.Length; i++)
            {
                var line = Parse(raw[i]);
                if (line != null)
                    result.Add(line);
            }

            if (raw.Length > maxLines * 2)
                Rewrite(file, raw, first);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex, "Lecture de l'historique du chat impossible");
        }

        return result;
    }

    private static ChatLine? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var line = JsonSerializer.Deserialize<ChatLine>(text, JsonOptions);
            if (line != null)
                line.FromHistory = true;
            return line;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Écriture dans un fichier à côté, puis remplacement : une coupure ne laisse pas un fichier à moitié écrit.</summary>
    private static void Rewrite(string file, string[] raw, int first)
    {
        var temporary = file + ".tmp";
        using (var writer = new StreamWriter(temporary, false, Utf8))
        {
            for (var i = first; i < raw.Length; i++)
                writer.WriteLine(raw[i]);
        }

        File.Move(temporary, file, overwrite: true);
    }

    public void Enqueue(ChatLine line)
    {
        var json = JsonSerializer.Serialize(line, JsonOptions);
        lock (sync)
            queue.Add(json);
    }

    /// <summary>Écrit ce qui attend sans bloquer le jeu.</summary>
    public void FlushInBackground()
    {
        lock (sync)
        {
            if (queue.Count == 0 || !writing.IsCompleted)
                return;

            writing = Task.Run(WriteQueue);
        }
    }

    /// <summary>Écrit tout de suite (changement de personnage, arrêt du plugin).</summary>
    public void Flush()
    {
        Task pending;
        lock (sync)
            pending = writing;

        try
        {
            pending.Wait();
        }
        catch (AggregateException ex)
        {
            logError?.Invoke(ex, "Écriture de l'historique du chat impossible");
        }

        WriteQueue();
    }

    private void WriteQueue()
    {
        string[] lines;
        string? file;
        lock (sync)
        {
            if (queue.Count == 0)
                return;

            lines = queue.ToArray();
            queue.Clear();
            file = path;
        }

        if (file == null)
            return;

        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllLines(file, lines, Utf8);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex, "Écriture de l'historique du chat impossible");
        }
    }

    /// <summary>Efface l'historique de ce personnage. Ce qui est déjà sur le disque ne revient pas.</summary>
    public void Delete()
    {
        Task pending;
        string? file;
        lock (sync)
        {
            queue.Clear();
            pending = writing;
            file = path;
        }

        try
        {
            pending.Wait();
            if (file != null && File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex, "Suppression de l'historique du chat impossible");
        }
    }
}
