using Microsoft.EntityFrameworkCore;

namespace Tempo.Core.Data;

/// <summary>Entities arrive in section 2 (tasks.md); the context exists from section 0 so
/// migrate-on-startup is wired once and stays wired.</summary>
public sealed class TempoDbContext(DbContextOptions<TempoDbContext> options) : DbContext(options);
