using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Data;
using TrainTracker.Api.Models;

namespace TrainTracker.Api.Services;

// ---------- شكل الـ JSON المتوقع ----------
public record ScheduleImportDto(List<TrainImportDto> Trains);

public record TrainImportDto(string Number, string? Type, string? NameAr, List<StopImportDto> Stops);

/// <param name="Station">اسم المحطة بالعربي (بيتطابق مع المحطات الموجودة أو الأسماء البديلة)</param>
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
/// بيحسب DayOffset لكل محطة (القطارات اللي بتعدّي منتصف الليل).
/// </summary>
public class ScheduleImporter(AppDbContext db)
{
    public async Task<ScheduleImportResult> ImportAsync(ScheduleImportDto dto, CancellationToken ct = default)
    {
        var stationsByKey = new Dictionary<string, Station>();
        foreach (var s in await db.Stations.ToListAsync(ct))
            stationsByKey.TryAdd(ArabicText.Normalize(s.NameAr), s);

        // الأسماء البديلة ليها أولوية (بتتضاف بإيد المستخدم)
        foreach (var a in await db.StationAliases.Include(x => x.Station).ToListAsync(ct))
            stationsByKey[a.AliasKey] = a.Station;

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
                var key = ArabicText.Normalize(stop.Station ?? "");
                if (key.Length > 0 && key.All(char.IsDigit)) continue; // اسم محطة بالغلط (زي "١")
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
                    // مفيش محطة بالاسم ده: ننشئها (الإحداثيات 0,0 لو مش متوفرة، وتتظبط بعدين)
                    st = new Station
                    {
                        NameAr = stop.Station!.Trim(),
                        Latitude = stop.Latitude ?? 0,
                        Longitude = stop.Longitude ?? 0
                    };
                    stationsByKey[key] = st;
                    newStations.Add(st);
                    resolved.Add((stop, st));
                }
            }

            if (missingHere.Count > 0)
            {
                foreach (var m in missingHere) unmatched.Add(m);
                problems.Add($"قطر {number}: اتخطى لأن فيه محطات مش موجودة ({missingHere.Count})");
                // نشيل المحطات الجديدة اللي اتضافت في الـ dictionary للقطر ده بس
                foreach (var ns in newStations) stationsByKey.Remove(ArabicText.Normalize(ns.NameAr));
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

            // 3) المحطات بترتيبها + حساب اليوم (DayOffset)
            var order = 1;
            var dayOffset = 0;
            int? lastMinutes = null;

            void Advance(TimeOnly time)
            {
                var m = time.Hour * 60 + time.Minute;
                if (lastMinutes is int last && m < last) dayOffset++; // الوقت رجع لورا = عدّينا منتصف الليل
                lastMinutes = m;
            }

            foreach (var (stop, station) in resolved)
            {
                var arrival = ParseTime(stop.Arrival);
                var departure = ParseTime(stop.Departure);

                int stopDay;
                if (arrival is TimeOnly arr)
                {
                    Advance(arr);
                    stopDay = dayOffset;
                    if (departure is TimeOnly depAfterArr) Advance(depAfterArr);
                }
                else
                {
                    if (departure is TimeOnly depOnly) Advance(depOnly);
                    stopDay = dayOffset;
                }

                train.Stops.Add(new TrainStop
                {
                    Station = station,
                    Order = order++,
                    DistanceFromStartKm = stop.DistanceKm ?? 0,
                    ScheduledArrival = arrival,
                    ScheduledDeparture = departure,
                    DayOffset = stopDay
                });
            }

            await db.SaveChangesAsync(ct);
        }

        return new ScheduleImportResult(added, updated, stationsCreated, unmatched.ToList(), problems);
    }

    private static TimeOnly? ParseTime(string? s) =>
        !string.IsNullOrWhiteSpace(s) && TimeOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var t) ? t : null;
}
