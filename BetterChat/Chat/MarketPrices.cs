using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace BetterChat.Chat;

/// <summary>Le prix le plus bas d'un objet, sur un monde et sur son centre de données. 0 : aucune offre.</summary>
public sealed record MarketQuote(
    string World,
    long WorldNq,
    long WorldHq,
    string DataCenter,
    long DataCenterPrice,
    bool DataCenterHq,
    string DataCenterWorld);

public enum MarketState
{
    /// <summary>L'objet ne se vend pas au marché, ou on ne sait pas encore sur quel monde on est.</summary>
    None,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// Les prix du marché, demandés au site Universalis (alimenté par les joueurs). Seulement quand le réglage est activé :
/// sinon le plugin ne contacte aucun site. Une réponse est gardée quelques minutes, pour ne pas redemander à chaque survol.
/// </summary>
public sealed class MarketPrices : IDisposable
{
    private const string Api = "https://universalis.app/api/v2";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private sealed record Entry(MarketState State, MarketQuote? Quote, DateTime TimeUtc);

    private readonly HttpClient http;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<(uint Item, uint World), Entry> cache = new();

    public MarketPrices(string version)
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BetterChat/{version}");
    }

    /// <summary>Les prix de cet objet sur le monde où l'on se trouve. Le premier appel lance la demande.</summary>
    public MarketState Get(uint itemId, out MarketQuote? quote)
    {
        quote = null;
        if (!IsMarketable(itemId) || Plugin.ObjectTable.LocalPlayer is not { } player)
            return MarketState.None;

        var world = player.CurrentWorld.ValueNullable;
        if (world == null || world.Value.RowId == 0)
            return MarketState.None;

        var key = (itemId, world.Value.RowId);
        var now = DateTime.UtcNow;
        if (cache.TryGetValue(key, out var entry))
        {
            var age = now - entry.TimeUtc;
            var stale = entry.State switch
            {
                MarketState.Ready => age > Fresh,
                MarketState.Failed => age > RetryAfterFailure,
                _ => false,
            };

            if (!stale)
            {
                quote = entry.Quote;
                return entry.State;
            }
        }

        cache[key] = new Entry(MarketState.Loading, entry?.Quote, now);
        var worldName = world.Value.Name.ExtractText();
        var dataCenter = world.Value.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
        _ = Fetch(key, worldName, dataCenter);

        quote = entry?.Quote;
        return MarketState.Loading;
    }

    private static bool IsMarketable(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId) is { } item && item.ItemSearchCategory.RowId != 0;

    private async Task Fetch((uint Item, uint World) key, string worldName, string dataCenter)
    {
        try
        {
            // Le monde : le moins cher en qualité normale et en haute qualité.
            using var worldJson = await Request($"{Api}/{key.World}/{key.Item}?listings=0&entries=0").ConfigureAwait(false);
            var worldRoot = worldJson.RootElement;
            var worldNq = Long(worldRoot, "minPriceNQ");
            var worldHq = Long(worldRoot, "minPriceHQ");

            // Le centre de données : l'offre la moins chère, et sur quel monde.
            long dcPrice = 0;
            var dcHq = false;
            var dcWorld = string.Empty;
            if (dataCenter.Length > 0)
            {
                using var dcJson = await Request($"{Api}/{Uri.EscapeDataString(dataCenter)}/{key.Item}?listings=10&entries=0").ConfigureAwait(false);
                if (dcJson.RootElement.TryGetProperty("listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var listing in listings.EnumerateArray())
                    {
                        var price = Long(listing, "pricePerUnit");
                        if (price <= 0 || (dcPrice > 0 && price >= dcPrice))
                            continue;

                        dcPrice = price;
                        dcHq = listing.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True;
                        dcWorld = listing.TryGetProperty("worldName", out var name) ? name.GetString() ?? string.Empty : string.Empty;
                    }
                }
            }

            var quote = new MarketQuote(worldName, worldNq, worldHq, dataCenter, dcPrice, dcHq, dcWorld);
            cache[key] = new Entry(MarketState.Ready, quote, DateTime.UtcNow);
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested)
        {
            Plugin.Log.Debug(ex, "Prix du marché indisponibles pour l'objet {Item}", key.Item);
            cache[key] = new Entry(MarketState.Failed, null, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // Le plugin se ferme : la réponse ne sert plus.
        }
    }

    private async Task<JsonDocument> Request(string url)
    {
        using var response = await http.GetAsync(url, stopping.Token).ConfigureAwait(false);

        // Un objet jamais vu par Universalis : aucune offre, ce n'est pas une erreur.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return JsonDocument.Parse("{}");

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(stopping.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: stopping.Token).ConfigureAwait(false);
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>« 1 234 567 gils », avec des espaces simples : la police du jeu n'a pas l'espace fine.</summary>
    public static string Gils(long amount) =>
        amount.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ') + " gils";

    public void Dispose()
    {
        stopping.Cancel();
        http.Dispose();
        stopping.Dispose();
    }
}
