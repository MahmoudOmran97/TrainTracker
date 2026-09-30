using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Data;
using TrainTracker.Api.Models;
using TrainTracker.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpClient<StationImporter>(c =>
{
    c.Timeout = TimeSpan.FromMinutes(3);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TrainTrackerApp/1.0 (contact: you@example.com)");
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ============================================================
// إنشاء الداتابيز والجداول أوتوماتيك أول ما الـ API يشتغل
// (لو الداتابيز موجودة بالفعل مبيعملش حاجة)
// ============================================================
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ---------------- Stations ----------------
app.MapGet("/api/stations", async (AppDbContext db, string? q) =>
{
    var query = db.Stations.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(q))
        query = query.Where(s => s.NameAr.Contains(q) || (s.NameEn != null && s.NameEn.Contains(q)));

    return await query
        .OrderBy(s => s.NameAr)
        .Select(s => new StationDto(s.Id, s.NameAr, s.NameEn, s.Latitude, s.Longitude))
        .ToListAsync();
});

// ---------------- Trips (كروت الشاشة الرئيسية) ----------------
app.MapGet("/api/trips/today", async (AppDbContext db) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)); // توقيت مصر تقريبًا

    // نولّد رحلات النهارده لو لسه متولدتش
    var existingTrainIds = await db.Trips.Where(t => t.ServiceDate == today)
        .Select(t => t.TrainId).ToListAsync();
    var missing = await db.Trains.Where(t => !existingTrainIds.Contains(t.Id)).ToListAsync();
    if (missing.Count > 0)
    {
        db.Trips.AddRange(missing.Select(t => new Trip { TrainId = t.Id, ServiceDate = today }));
        await db.SaveChangesAsync();
    }

    var trips = await db.Trips.AsNoTracking()
        .Where(t => t.ServiceDate == today)
        .Include(t => t.Train).ThenInclude(tr => tr.Stops).ThenInclude(s => s.Station)
        .ToListAsync();

    return trips.Select(t =>
    {
        var stops = t.Train.Stops.OrderBy(s => s.Order).ToList();
        var first = stops.FirstOrDefault();
        var last = stops.LastOrDefault();
        return new TripCardDto(
            t.Id, t.Train.Number, t.Train.Type,
            first?.Station.NameAr, last?.Station.NameAr,
            first?.ScheduledDeparture, last?.ScheduledArrival,
            t.Status.ToString(), t.DelayMinutes,
            t.LastLatitude, t.LastLongitude);
    });
});

app.MapGet("/api/trips/{id:int}", async (int id, AppDbContext db) =>
{
    var t = await db.Trips.AsNoTracking()
        .Include(x => x.Train).ThenInclude(tr => tr.Stops).ThenInclude(s => s.Station)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (t is null) return Results.NotFound();

    var stops = t.Train.Stops.OrderBy(s => s.Order)
        .Select(s => new TripStopDto(s.Station.Id, s.Station.NameAr, s.Station.Latitude, s.Station.Longitude,
            s.Order, s.ScheduledArrival, s.ScheduledDeparture));

    return Results.Ok(new TripDetailsDto(t.Id, t.Train.Number, t.Train.Type, t.Status.ToString(),
        t.DelayMinutes, t.LastLatitude, t.LastLongitude, stops));
});

// ---------------- بلاغ موقع من راكب ----------------
app.MapPost("/api/trips/{id:int}/position", async (int id, PositionReportDto dto, AppDbContext db) =>
{
    if (dto.Latitude is < -90 or > 90 || dto.Longitude is < -180 or > 180)
        return Results.BadRequest("إحداثيات غير صالحة");

    var trip = await db.Trips.FindAsync(id);
    if (trip is null) return Results.NotFound();

    var now = DateTime.UtcNow;
    db.PositionReports.Add(new PositionReport
    {
        TripId = id, UserId = dto.UserId,
        Latitude = dto.Latitude, Longitude = dto.Longitude,
        SpeedKmh = dto.SpeedKmh, ReportedAtUtc = now
    });

    // المرحلة الجاية: map matching + فلترة البلاغات الشاذة. دلوقتي بنحدّث آخر موقع بس
    trip.LastLatitude = dto.Latitude;
    trip.LastLongitude = dto.Longitude;
    trip.LastReportAtUtc = now;
    trip.Status = TripStatus.Running;

    await db.SaveChangesAsync();
    return Results.Accepted();
});

// ---------------- Admin (وقت التطوير بس) ----------------
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/admin/import-stations", async (StationImporter importer, CancellationToken ct) =>
    {
        var (added, updated, skipped) = await importer.ImportAsync(ct);
        return Results.Ok(new { added, updated, skipped });
    });
}

app.Run();

// ---------------- DTOs ----------------
public record StationDto(int Id, string NameAr, string? NameEn, double Latitude, double Longitude);

public record TripCardDto(int TripId, string TrainNumber, string? TrainType,
    string? From, string? To, TimeOnly? Departure, TimeOnly? Arrival,
    string Status, int DelayMinutes, double? LastLatitude, double? LastLongitude);

public record TripStopDto(int StationId, string StationName, double Latitude, double Longitude,
    int Order, TimeOnly? ScheduledArrival, TimeOnly? ScheduledDeparture);

public record TripDetailsDto(int TripId, string TrainNumber, string? TrainType, string Status,
    int DelayMinutes, double? LastLatitude, double? LastLongitude, IEnumerable<TripStopDto> Stops);

public record PositionReportDto(double Latitude, double Longitude, double? SpeedKmh, Guid? UserId);
