using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Data;
using TrainTracker.Api.Models;

namespace TrainTracker.Api.Services;

// ---------- شكل الـ JSON المتوقع ----------
public record ScheduleImportDto(List<TrainImportDto> Trains);

public record TrainImportDto(string Number, string? Type, string? NameAr, List<StopImportDto> Stops);

/// <param name="Station">اسم المحطة بالعربي (بيتطابق مع المحطات الموجودة)</param>
/// <param name="Arrival">وقت الوصول "HH:mm" (فاضي لأول محطة)</param>
/// <param name="Departure">وقت المغادرة "HH:mm" (فاضي لآخر محطة)</param>
/// <param name="Latitude">اختياري: لو المحطة مش موجودة وعايز تنشئها</param>
/// <param name="Longitude">اختياري: لو المحطة مش موجودة وعايز تنشئها</param>
public record StopImportDto(string Station, string? Arrival, string? Departure,
    double? DistanceKm, double? Latitude, double? Longitude);

public record ScheduleImportResult(int TrainsAdded, int TrainsUpdated, int StationsCreated,
    List<string> UnmatchedStations, List<string> Problems);

/// <summary>
/// بيستورد جداول القطارات من JSON (من أي مصدر مسموح تستخدمه) ويحفظها في Trains و TrainStops.
/// القطر بيتحدد برقمه: لو موجود بيتم استبدال محطاته، ولو مش موجود بيتضاف.
/// </summary>
public class ScheduleImporter(AppDbContext db)
{
    public async Task<ScheduleImportResult> ImportAsync(ScheduleImportDto dto, CancellationToken ct = default)
    {
        var stationsByKey = new Dictionary<string, Station>();
        foreach (var s in await db.Stations.ToListAsync(ct))
            stationsByKey.TryAdd(Normalize(s.NameAr), s);

        int added = 0, updated = 0, stationsCreated = 0;
        var unmatched = new SortedSet<string>();
        var problems = new List<string>();

        foreach (var t in dto.Trains ?? new())
        {
            var number = t.Number?.Trim();
            if (string.IsNullOrEmpty(number)) { problems.Add("قطر من غير رقم — اتجاهل"); continue; }
            if (t.Stops is null || t.Stops.Count < 2) { problems.Add($"قطر {number}: لازم محطتين على الأقل"); continue; }

            // 1) نحل كل المحطات الأول. لو فيه محطة مش موجودة ومفيش إحداثيات بنتخطى القطر كله
            var resolved = new List<(StopImportDto Stop, Station Station)>();
            var missingHere = new List<string>();
            var newStations = new List<Station>();

            foreach (var stop in t.Stops)
            {
                var key = Normalize(stop.Station ?? "");
                if (key.Length == 0) { missingHere.Add("(اسم فاضي)"); continue; }

                if (stationsByKey.TryGetValue(key, out var st))
                {
                    resolved.Add((stop, st));
                }
                else if (stop.Latitude is not null && stop.Longitude is not null)
                {
                    st = new Station
                    {
                        NameAr = stop.Station!.Trim(),
                        Latitude = stop.Latitude.Value,
                        Longitude = stop.Longitude.Value
                    };
                    stationsByKey[key] = st;
                    newStations.Add(st);
                    resolved.Add((stop, st));
                }
                else
                {
                    missingHere.Add(stop.Station!.Trim());
                }
            }

            if (missingHere.Count > 0)
            {
                foreach (var m in missingHere) unmatched.Add(m);
                problems.Add($"قطر {number}: اتخطى لأن فيه محطات مش موجودة ({missingHere.Count})");
                // نشيل المحطات الجديدة اللي اتضافت في الـ dictionary للقطر ده بس
                foreach (var ns in newStations) stationsByKey.Remove(Normalize(ns.NameAr));
                continue;
            }

            db.Stations.AddRange(newStations);
            stationsCreated += newStations.Count;

            // 2) القطر: نجيبه أو ننشئه
            var train = await db.Trains.Include(x => x.Stops).FirstOrDefaultAsync(x => x.Number == number, ct);
            if (train is null)
            {
                train = new Train { Number = number };
                db.Trains.Add(train);
                added++;
            }
            else
            {
                // نمسح المحطات القديمة ونحفظ الأول عشان الـ unique index على (TrainId, Order)
                db.TrainStops.RemoveRange(train.Stops);
                await db.SaveChangesAsync(ct);
                train.Stops.Clear();
                updated++;
            }

            train.Type = t.Type?.Trim();
            train.NameAr = t.NameAr?.Trim();

            // 3) المحطات بترتيبها
            var order = 1;
            foreach (var (stop, station) in resolved)
            {
                train.Stops.Add(new TrainStop
                {
                    Station = station,
                    Order = order++,
                    DistanceFromStartKm = stop.DistanceKm ?? 0,
                    ScheduledArrival = ParseTime(stop.Arrival),
                    ScheduledDeparture = ParseTime(stop.Departure)
                });
            }

            await db.SaveChangesAsync(ct);
        }

        return new ScheduleImportResult(added, updated, stationsCreated, unmatched.ToList(), problems);
    }

    private static TimeOnly? ParseTime(string? s) =>
        !string.IsNullOrWhiteSpace(s) && TimeOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>توحيد الهمزات والياء والتاء المربوطة والمسافات عشان أسماء المحطات تتطابق</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            switch (ch)
            {
                case 'أ': case 'إ': case 'آ': sb.Append('ا'); break;
                case 'ى': sb.Append('ي'); break;
                case 'ة': sb.Append('ه'); break;
                case 'ـ': break; // تطويل
                default:
                    if (char.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) break; // تشكيل
                    sb.Append(char.ToLowerInvariant(ch));
                    break;
            }
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
