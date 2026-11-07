using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Utility;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using BetterChat.Core;

namespace BetterChat.Chat;

/// <summary>La liste de l'autotraduction du jeu, lue une fois en arrière-plan, et l'encodage d'un mot choisi.</summary>
internal sealed class AutoTranslateData
{
    private readonly Dictionary<string, (uint Group, uint Key)> picked = new();
    private Task<List<AutoTranslateEntry>>? loading;
    private Dictionary<string, (uint Group, uint Key)>? byText;

    /// <summary>Lance la lecture si ce n'est pas déjà fait : la première recherche n'attend pas.</summary>
    public void Preload() => loading ??= Task.Run(Load);

    /// <summary>Null tant que la liste n'est pas prête.</summary>
    public IReadOnlyList<AutoTranslateEntry>? Entries
    {
        get
        {
            Preload();
            return loading is { IsCompletedSuccessfully: true } ? loading.Result : null;
        }
    }

    /// <summary>Le mot choisi dans la liste : c'est lui qui part, même si un autre a le même texte.</summary>
    public void Remember(AutoTranslateEntry entry) => picked[entry.Text] = (entry.Group, entry.Key);

    public (uint Group, uint Key)? Find(string text)
    {
        if (picked.TryGetValue(text, out var chosen))
            return chosen;

        if (byText == null && Entries is { } entries)
        {
            byText = new Dictionary<string, (uint, uint)>();
            foreach (var entry in entries)
                byText.TryAdd(entry.Text, (entry.Group, entry.Key));
        }

        return byText != null && byText.TryGetValue(text, out var found) ? found : null;
    }

    /// <summary>Le lien d'autotraduction tel que le jeu l'écrit dans un message.</summary>
    public static byte[] Encode(uint group, uint key) =>
        new SeStringBuilder()
            .BeginMacro(MacroCode.Fixed)
            .AppendUIntExpression(group - 1)
            .AppendUIntExpression(key)
            .EndMacro()
            .ToArray();

    private static List<AutoTranslateEntry> Load()
    {
        var list = new List<AutoTranslateEntry>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Completion>())
        {
            var lookupText = LookupText(row.LookupTable);
            var title = row.GroupTitle.ExtractText();

            try
            {
                var lookup = CompletionLookup.Parse(lookupText);
                if (lookup == null)
                {
                    // « @ » : une ligne d'en-tête, sans mot à proposer.
                    var text = row.Text.ExtractText();
                    if (lookupText.Trim() != "@" && text.Length > 0)
                        list.Add(new AutoTranslateEntry(row.Group, row.RowId, text, title));
                    continue;
                }

                var sheet = Plugin.DataManager.Excel.GetSheet<RawRow>(name: lookup.Sheet);
                var rowIds = lookup.Rows.Count == 0
                    ? sheet.Select(r => r.RowId)
                    : lookup.Rows.SelectMany(r => Enumerable.Range((int)r.First, (int)(r.Last - r.First + 1)).Select(i => (uint)i));

                foreach (var id in rowIds)
                {
                    if (!sheet.TryGetRow(id, out var raw))
                        continue;

                    foreach (var column in lookup.Columns)
                    {
                        var text = raw.ReadStringColumn(column).ExtractText();
                        if (text.Length > 0)
                            list.Add(new AutoTranslateEntry(row.Group, id, text, title));
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Autotraduction : table {Lookup} illisible", lookupText);
            }
        }

        return list;
    }

    private static string LookupText(ReadOnlySeString lookup)
    {
        var builder = new StringBuilder();
        foreach (var payload in lookup)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
                builder.Append(Encoding.UTF8.GetString(payload.Body.Span));
            else if (payload.MacroCode == MacroCode.Num && payload.TryGetExpression(out var number) && number.TryGetInt(out var value))
                builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
