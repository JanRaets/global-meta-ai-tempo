using Microsoft.EntityFrameworkCore;
using Tempo.Core.Domain;

namespace Tempo.Core.Data;

public sealed class TempoDbContext(DbContextOptions<TempoDbContext> options) : DbContext(options)
{
    public DbSet<Place> Places => Set<Place>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<DeparturePlan> DeparturePlans => Set<DeparturePlan>();
    public DbSet<Nudge> Nudges => Set<Nudge>();
    public DbSet<Intent> Intents => Set<Intent>();
    public DbSet<PlanInputs> PlanInputs => Set<PlanInputs>();
    public DbSet<AgentTrace> AgentTraces => Set<AgentTrace>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Appointment>(e =>
        {
            e.HasIndex(a => new { a.Source, a.ExternalId });
            e.HasOne(a => a.Place).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Visit>()
            .HasOne(v => v.Place).WithMany().OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Habit>(e =>
        {
            e.OwnsOne(h => h.Context);
            e.HasOne(h => h.Place).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeparturePlan>(e =>
        {
            e.OwnsMany(p => p.Stops, b => b.ToJson());
            e.HasIndex(p => new { p.AppointmentId, p.Version }).IsUnique();
            e.HasOne<Appointment>().WithMany().HasForeignKey(p => p.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlanInputs>(e =>
        {
            e.OwnsMany(p => p.ExtraStops, b => b.ToJson());
            e.HasIndex(p => p.AppointmentId).IsUnique();
            e.HasOne<Appointment>().WithMany().HasForeignKey(p => p.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        });

        // Nudges keep plain ids on purpose: cancelled nudges survive plan/appointment deletion.
        modelBuilder.Entity<Nudge>()
            .HasIndex(n => new { n.PlanId, n.PlanVersion, n.Kind });

        modelBuilder.Entity<AgentTrace>()
            .OwnsMany(t => t.Steps, b => b.ToJson());

        modelBuilder.Entity<OAuthToken>()
            .HasIndex(t => t.Provider).IsUnique();

        modelBuilder.Entity<PushSubscription>()
            .HasIndex(s => s.Endpoint).IsUnique();
    }
}

/// <summary>Encrypted-at-rest OAuth refresh token (the value is Data Protection output, never plaintext).</summary>
public sealed class OAuthToken
{
    public Guid Id { get; set; }
    public required string Provider { get; set; }
    public required string ProtectedRefreshToken { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
