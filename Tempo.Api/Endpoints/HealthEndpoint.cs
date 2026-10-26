using FastEndpoints;

namespace Tempo.Api.Endpoints;

public sealed class HealthEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/health");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(new { status = "ok" }, ct);
}
