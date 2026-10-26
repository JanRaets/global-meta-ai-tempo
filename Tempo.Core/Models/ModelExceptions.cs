namespace Tempo.Core.Models;

/// <summary>An HTTP-level failure from a provider; carries the status so the fallback wrapper can decide.</summary>
public sealed class ModelHttpException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public bool IsFallbackEligible => StatusCode is 429 or >= 500;
}

/// <summary>Both providers failed; callers use their deterministic fallback path (template nudge, no-op run).</summary>
public sealed class ModelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The model's output could not be parsed into the requested type after one retry. No partial result is used.</summary>
public sealed class ModelOutputException(string message, Exception? inner = null) : Exception(message, inner);
