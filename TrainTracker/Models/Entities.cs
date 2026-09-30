namespace TrainTracker.Api.Models;

public enum TripStatus { Scheduled = 0, Running = 1, Completed = 2, Cancelled = 3 }
public enum FollowMode { HasTicket = 0, Following = 1 }

public class Station
{
    public int Id { get; set; }
    /// <summary>مثال: node/123456 (المعرّف في OpenStreetMap)</summary>
    public string? ExternalId { get; set; }
    public string NameAr { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Governorate { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class Train
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string? NameAr { get; set; }
    /// <summary>VIP / مكيف / تحسين / روسي ...</summary>
    public string? Type { get; set; }
    public List<TrainStop> Stops { get; set; } = new();
}

/// <summary>جدول مواعيد القطر: محطة + ترتيبها + الوصول والمغادرة</summary>
public class TrainStop
{
    public int Id { get; set; }
    public int TrainId { get; set; }
    public Train Train { get; set; } = null!;
    public int StationId { get; set; }
    public Station Station { get; set; } = null!;
    public int Order { get; set; }
    public double DistanceFromStartKm { get; set; }
    public TimeOnly? ScheduledArrival { get; set; }
    public TimeOnly? ScheduledDeparture { get; set; }
}

/// <summary>رحلة قطر في يوم معين (ده الكارد اللي في الشاشة الرئيسية)</summary>
public class Trip
{
    public int Id { get; set; }
    public int TrainId { get; set; }
    public Train Train { get; set; } = null!;
    public DateOnly ServiceDate { get; set; }
    public TripStatus Status { get; set; } = TripStatus.Scheduled;
    public int DelayMinutes { get; set; }
    public double? LastLatitude { get; set; }
    public double? LastLongitude { get; set; }
    public DateTime? LastReportAtUtc { get; set; }
}

public class PositionReport
{
    public long Id { get; set; }
    public int TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid? UserId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? SpeedKmh { get; set; }
    public DateTime ReportedAtUtc { get; set; }
}

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? DisplayName { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<Device> Devices { get; set; } = new();
}

public class Device
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string FcmToken { get; set; } = "";
    public string? Platform { get; set; }
}

/// <summary>اشتراك المستخدم في رحلة: معاه تذكرة ولا بيتابع بس، وهينزل فين</summary>
public class UserTrip
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public int TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public int? BoardingStationId { get; set; }
    public int AlightStationId { get; set; }
    public FollowMode Mode { get; set; }
    public int NotifyMinutesBefore { get; set; } = 15;
    public bool Notified { get; set; }
}
