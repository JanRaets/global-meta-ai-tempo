using Tempo.Core.Location;

namespace Tempo.Tests.Fakes;

public sealed class FakeLocationBuffer : ILocationBuffer
{
    public List<LocationPoint> Points { get; } = [];

    public Task AppendAsync(LocationPoint point, CancellationToken ct = default)
    {
        Points.Add(point);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LocationPoint>> GetSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LocationPoint>>(
            Points.Where(p => p.Timestamp >= since).OrderBy(p => p.Timestamp).ToList());

    public Task<LocationPoint?> GetLatestAsync(CancellationToken ct = default) =>
        Task.FromResult(Points.OrderBy(p => p.Timestamp).LastOrDefault());
}
