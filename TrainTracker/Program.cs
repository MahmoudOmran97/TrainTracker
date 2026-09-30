using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Data;
using TrainTracker.Api.Models;
using TrainTracker.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<ScheduleImporter>();

builder.Services.AddHttpClient<StationImporter>(c =>
{
    c.Timeout = TimeSpan.FromMinutes(3);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TrainTrackerApp/1.0 (+https://github.com/MahmoudOmran97/TrainTracker)");
    c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
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
    db.Database.ExecuteSqlRaw("""
        IF COL_LENGTH('dbo.TrainStops', 'DayOffset') IS NULL
            ALTER TABLE dbo.TrainStops
                ADD DayOffset int NOT NULL CONSTRAINT DF_TrainStops_DayOffset DEFAULT 0;

        IF OBJECT_ID('dbo.StationAliases', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.StationAliases
            (
                Id        int IDENTITY(1,1) NOT NULL,
                Alias     nvarchar(200) NOT NULL,
                AliasKey  nvarchar(200) NOT NULL,
                StationId int NOT NULL,
                CONSTRAINT PK_StationAliases PRIMARY KEY (Id),
                CONSTRAINT FK_StationAliases_Stations_StationId
                    FOREIGN KEY (StationId) REFERENCES dbo.Stations (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IX_StationAliases_AliasKey ON dbo.StationAliases (AliasKey);
        END
        """);
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
app.MapGet("/api/trips/today", async (AppDbContext db, CancellationToken ct) =>
{
    var today = TripGenerator.EgyptToday();

    // نولّد رحلات النهارده لو لسه متولدتش
    await TripGenerator.EnsureTripsAsync(db, today, ct);

    var trips = await db.Trips.AsNoTracking()
        .Where(t => t.ServiceDate == today)
        .Include(t => t.Train).ThenInclude(tr => tr.Stops).ThenInclude(s => s.Station)
        .ToListAsync(ct);

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

// ---------------- بحث عن قطارات بين محطتين (رحلات النهارده) ----------------
app.MapGet("/api/trips/search", async (AppDbContext db, int fromStationId, int toStationId, CancellationToken ct) =>
{
    if (fromStationId == toStationId)
        return Results.BadRequest("محطة المغادرة والوصول لازم يكونوا مختلفين");

    var today = TripGenerator.EgyptToday();
    await TripGenerator.EnsureTripsAsync(db, today, ct);

    // القطر لازم يعدّي على المحطتين، ومحطة المغادرة قبل محطة الوصول في الترتيب
    var rows = await db.TrainStops.AsNoTracking()
        .Where(a => a.StationId == fromStationId)
        .Join(db.TrainStops.Where(b => b.StationId == toStationId),
              a => a.TrainId, b => b.TrainId, (a, b) => new { a, b })
        .Where(x => x.a.Order < x.b.Order)
        .Select(x => new
        {
            x.a.TrainId,
            TrainNumber = x.a.Train.Number,
            TrainType = x.a.Train.Type,
            Departure = x.a.ScheduledDeparture,
            Arrival = x.b.ScheduledArrival,
            StopsCount = x.b.Order - x.a.Order
        })
        .ToListAsync(ct);

    var trainIds = rows.Select(r => r.TrainId).ToList();
    var tripsByTrain = await db.Trips.AsNoTracking()
        .Where(t => t.ServiceDate == today && trainIds.Contains(t.TrainId))
        .ToDictionaryAsync(t => t.TrainId, ct);

    var result = rows
        .Where(r => tripsByTrain.ContainsKey(r.TrainId))
        .OrderBy(r => r.Departure)
        .Select(r =>
        {
            var trip = tripsByTrain[r.TrainId];
            return new TripSearchResultDto(trip.Id, r.TrainNumber, r.TrainType,
                r.Departure, r.Arrival, r.StopsCount, trip.Status.ToString(), trip.DelayMinutes);
        })
        .ToList();

    return Results.Ok(result);
});

// ---------------- تفاصيل قطر برقمه (جدول المحطات) ----------------
app.MapGet("/api/trains/{number}", async (string number, AppDbContext db, CancellationToken ct) =>
{
    var train = await db.Trains.AsNoTracking()
        .Include(t => t.Stops).ThenInclude(s => s.Station)
        .FirstOrDefaultAsync(t => t.Number == number, ct);
    if (train is null) return Results.NotFound();

    var stops = train.Stops.OrderBy(s => s.Order)
        .Select(s => new TripStopDto(s.Station.Id, s.Station.NameAr, s.Station.Latitude, s.Station.Longitude,
            s.Order, s.ScheduledArrival, s.ScheduledDeparture, s.DayOffset));

    return Results.Ok(new TrainDetailsDto(train.Id, train.Number, train.Type, stops));
});

app.MapGet("/api/trips/{id:int}", async (int id, AppDbContext db) =>
{
    var t = await db.Trips.AsNoTracking()
        .Include(x => x.Train).ThenInclude(tr => tr.Stops).ThenInclude(s => s.Station)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (t is null) return Results.NotFound();

    var stops = t.Train.Stops.OrderBy(s => s.Order)
        .Select(s => new TripStopDto(s.Station.Id, s.Station.NameAr, s.Station.Latitude, s.Station.Longitude,
            s.Order, s.ScheduledArrival, s.ScheduledDeparture, s.DayOffset));

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

    // استيراد جداول القطارات من JSON (شوف Samples/sample-schedule.json)
    app.MapPost("/api/admin/import-schedule", async (ScheduleImportDto dto, ScheduleImporter importer, CancellationToken ct) =>
        Results.Ok(await importer.ImportAsync(dto, ct)));

    // أسماء بديلة للمحطات: لو محطة طلعت في unmatchedStations اربطها بمحطة موجودة
    app.MapGet("/api/admin/aliases", async (AppDbContext db, CancellationToken ct) =>
        await db.StationAliases.AsNoTracking()
            .OrderBy(a => a.Alias)
            .Select(a => new StationAliasDto(a.Id, a.Alias, a.StationId, a.Station.NameAr))
            .ToListAsync(ct));

    app.MapPost("/api/admin/aliases", async (StationAliasCreateDto dto, AppDbContext db, CancellationToken ct) =>
    {
        var alias = dto.Alias?.Trim() ?? "";
        var key = ArabicText.Normalize(alias);
        if (key.Length == 0) return Results.BadRequest("الاسم فاضي");

        var station = await db.Stations.FindAsync(new object[] { dto.StationId }, ct);
        if (station is null) return Results.NotFound("المحطة مش موجودة");

        var existing = await db.StationAliases.FirstOrDefaultAsync(a => a.AliasKey == key, ct);
        if (existing is null)
            db.StationAliases.Add(new StationAlias { Alias = alias, AliasKey = key, StationId = station.Id });
        else
        {
            existing.Alias = alias;
            existing.StationId = station.Id;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new StationAliasDto(existing?.Id ?? 0, alias, station.Id, station.NameAr));
    });
}

app.Run();

// ---------------- DTOs ----------------
public record StationDto(int Id, string NameAr, string? NameEn, double Latitude, double Longitude);

public record TripCardDto(int TripId, string TrainNumber, string? TrainType,
    string? From, string? To, TimeOnly? Departure, TimeOnly? Arrival,
    string Status, int DelayMinutes, double? LastLatitude, double? LastLongitude);

public record TripStopDto(int StationId, string StationName, double Latitude, double Longitude,
    int Order, TimeOnly? ScheduledArrival, TimeOnly? ScheduledDeparture, int DayOffset = 0);

public record TripDetailsDto(int TripId, string TrainNumber, string? TrainType, string Status,
    int DelayMinutes, double? LastLatitude, double? LastLongitude, IEnumerable<TripStopDto> Stops);

public record PositionReportDto(double Latitude, double Longitude, double? SpeedKmh, Guid? UserId);

public record TripSearchResultDto(int TripId, string TrainNumber, string? TrainType,
    TimeOnly? Departure, TimeOnly? Arrival, int StopsCount, string Status, int DelayMinutes);

public record TrainDetailsDto(int TrainId, string TrainNumber, string? TrainType, IEnumerable<TripStopDto> Stops);

public record StationAliasDto(int Id, string Alias, int StationId, string StationName);

public record StationAliasCreateDto(string Alias, int StationId);
