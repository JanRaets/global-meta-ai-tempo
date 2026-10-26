using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;

namespace Tempo.Integrations.Google;

/// <summary>Stores the Google refresh token encrypted at rest via Data Protection.</summary>
public sealed class GoogleTokenStore(TempoDbContext db, IDataProtectionProvider dataProtection)
{
    private const string Provider = "google";
    private readonly IDataProtector protector = dataProtection.CreateProtector("Tempo.GoogleTokens");

    public async Task SaveRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var row = await db.OAuthTokens.SingleOrDefaultAsync(t => t.Provider == Provider, ct);
        if (row is null)
        {
            row = new OAuthToken { Id = Guid.NewGuid(), Provider = Provider, ProtectedRefreshToken = "" };
            db.OAuthTokens.Add(row);
        }
        row.ProtectedRefreshToken = protector.Protect(refreshToken);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetRefreshTokenAsync(CancellationToken ct = default)
    {
        var row = await db.OAuthTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Provider == Provider, ct);
        return row is null ? null : protector.Unprotect(row.ProtectedRefreshToken);
    }
}
