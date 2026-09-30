using Microsoft.EntityFrameworkCore;
using TrainTracker.Api.Models;

namespace TrainTracker.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<Train> Trains => Set<Train>();
    public DbSet<TrainStop> TrainStops => Set<TrainStop>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<PositionReport> PositionReports => Set<PositionReport>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<UserTrip> UserTrips => Set<UserTrip>();
    public DbSet<StationAlias> StationAliases => Set<StationAlias>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Station>(e =>
        {
            e.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameEn).HasMaxLength(200);
            e.Property(x => x.Governorate).HasMaxLength(100);
            e.Property(x => x.ExternalId).HasMaxLength(50);
            e.HasIndex(x => x.ExternalId).IsUnique().HasFilter("[ExternalId] IS NOT NULL");
            e.HasIndex(x => x.NameAr);
        });

        b.Entity<StationAlias>(e =>
        {
            e.Property(x => x.Alias).HasMaxLength(200).IsRequired();
            e.Property(x => x.AliasKey).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.AliasKey).IsUnique();
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Train>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
        });

        b.Entity<TrainStop>(e =>
        {
            e.HasIndex(x => new { x.TrainId, x.Order }).IsUnique();
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Trip>(e =>
        {
            e.HasIndex(x => new { x.TrainId, x.ServiceDate }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<PositionReport>(e =>
        {
            e.HasIndex(x => new { x.TripId, x.ReportedAtUtc });
        });

        b.Entity<UserTrip>(e =>
        {
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.UserId, x.TripId }).IsUnique();
            e.HasOne(x => x.Trip).WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
