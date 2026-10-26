using Microsoft.Extensions.Logging;
using Tempo.Core.Domain;

namespace Tempo.Core.Nudging;

/// <summary>Delivers a nudge to the user. Telegram implements this in section 9;
/// until then the logging transport makes nudges visible in the console.</summary>
public interface INudgeTransport
{
    Task<bool> SendAsync(Nudge nudge, CancellationToken ct = default);
}

public sealed class LoggingNudgeTransport(ILogger<LoggingNudgeTransport> logger) : INudgeTransport
{
    public Task<bool> SendAsync(Nudge nudge, CancellationToken ct = default)
    {
        logger.LogInformation("NUDGE [{Kind}] {Text}", nudge.Kind, nudge.Text);
        return Task.FromResult(true);
    }
}
