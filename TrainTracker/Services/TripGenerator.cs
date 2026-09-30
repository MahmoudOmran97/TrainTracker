using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Data;
using TrainTracker.Api.Models;

namespace TrainTracker.Api.Services;

public static class TripGenerator
{
    /// <summary>توقيت مصر تقريبًا (UTC+3 زي ما كان في الكود الأصلي)</summary>
    public static DateOnly EgyptToday() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>بيولّد رحلات اليوم لأي قطر لسه معندوش رحلة في التاريخ ده</summary>
    public static async Task EnsureTripsAsync(AppDbContext db, DateOnly date, CancellationToken ct = default)
    {
        var existingTrainIds = await db.Trips
            .Where(t => t.ServiceDate == date)
            .Select(t => t.TrainId)
            .ToListAsync(ct);

        var missing = await db.Trains
            .Where(t => !existingTrainIds.Contains(t.Id))
            .ToListAsync(ct);

        if (missing.Count == 0) return;

        db.Trips.AddRange(missing.Select(t => new Trip { TrainId = t.Id, ServiceDate = date }));
        await db.SaveChangesAsync(ct);
    }
}
