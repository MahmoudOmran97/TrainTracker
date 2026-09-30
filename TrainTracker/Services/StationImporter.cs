using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Data;
using TrainTracker.Api.Models;

namespace TrainTracker.Api.Services;

/// <summary>
/// بيسحب كل محطات السكة الحديد في مصر من OpenStreetMap (Overpass API)
/// ويحفظها في الداتابيز: بيضيف الجديد ويحدّث الموجود.
/// </summary>
public class StationImporter(HttpClient http, AppDbContext db)
{
    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";

    private const string Query = """
        [out:json][timeout:120];
        area["ISO3166-1"="EG"][admin_level=2]->.eg;
        nwr["railway"="station"](area.eg);
        out center tags;
        """;

    private static readonly HashSet<string> IgnoredStationTypes = new() { "subway", "light_rail", "tram" };

    public async Task<(int Added, int Updated, int Skipped)> ImportAsync(CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("data", Query)
        });

        using var resp = await http.PostAsync(OverpassUrl, content, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var existing = await db.Stations
            .Where(s => s.ExternalId != null)
            .ToDictionaryAsync(s => s.ExternalId!, ct);

        int added = 0, updated = 0, skipped = 0;

        foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (!el.TryGetProperty("tags", out var tags)) { skipped++; continue; }

            // نتجاهل المترو والترام
            if (tags.TryGetProperty("station", out var st) && IgnoredStationTypes.Contains(st.GetString() ?? ""))
            { skipped++; continue; }

            // الإحداثيات: node فيه lat/lon، و way/relation فيه center
            double lat, lon;
            if (el.TryGetProperty("lat", out var latEl) && el.TryGetProperty("lon", out var lonEl))
            { lat = latEl.GetDouble(); lon = lonEl.GetDouble(); }
            else if (el.TryGetProperty("center", out var c))
            { lat = c.GetProperty("lat").GetDouble(); lon = c.GetProperty("lon").GetDouble(); }
            else { skipped++; continue; }

            var nameAr = GetTag(tags, "name:ar") ?? GetTag(tags, "name");
            if (string.IsNullOrWhiteSpace(nameAr)) { skipped++; continue; }

            var externalId = $"{el.GetProperty("type").GetString()}/{el.GetProperty("id").GetInt64()}";
            var nameEn = GetTag(tags, "name:en");

            if (existing.TryGetValue(externalId, out var station))
            {
                station.NameAr = nameAr;
                station.NameEn = nameEn;
                station.Latitude = lat;
                station.Longitude = lon;
                updated++;
            }
            else
            {
                db.Stations.Add(new Station
                {
                    ExternalId = externalId,
                    NameAr = nameAr,
                    NameEn = nameEn,
                    Latitude = lat,
                    Longitude = lon
                });
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (added, updated, skipped);
    }

    private static string? GetTag(JsonElement tags, string key) =>
        tags.TryGetProperty(key, out var v) ? v.GetString() : null;
}
