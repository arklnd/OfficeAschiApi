using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Models;

namespace OfficeAschiApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Reportee> Reportees => Set<Reportee>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(e =>
        {
            e.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<Seat>(e =>
        {
            e.HasOne(s => s.Team).WithMany(t => t.Seats).HasForeignKey(s => s.TeamId);
        });

        modelBuilder.Entity<Reportee>(e =>
        {
            e.HasOne(r => r.Team).WithMany(t => t.Reportees).HasForeignKey(r => r.TeamId);
            e.HasIndex(r => new { r.TeamId, r.FriendlyName }).IsUnique();
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.HasOne(b => b.Seat).WithMany(s => s.Bookings).HasForeignKey(b => b.SeatId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Reportee).WithMany(r => r.Bookings).HasForeignKey(b => b.ReporteeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Team).WithMany().HasForeignKey(b => b.TeamId).OnDelete(DeleteBehavior.Restrict);

            // Only one confirmed booking per seat per date
            e.HasIndex(b => new { b.Date, b.SeatId, b.Status })
                .HasFilter($"\"Status\" = {(int)BookingStatus.Confirmed}")
                .IsUnique();

            // A reportee can only have one booking (confirmed or waitlisted) per date per team
            e.HasIndex(b => new { b.Date, b.ReporteeId }).IsUnique();
        });

        modelBuilder.Entity<PushSubscription>(e =>
        {
            e.HasOne(p => p.Team).WithMany().HasForeignKey(p => p.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.Endpoint, p.EntityType, p.EntityId }).IsUnique();
        });
    }
}
