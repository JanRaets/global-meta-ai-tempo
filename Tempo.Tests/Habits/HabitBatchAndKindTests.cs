using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Domain;
using Tempo.Jobs;
using Tempo.Tests.Nudging;

namespace Tempo.Tests.Habits;

public sealed class AppointmentKindTests
{
    [Theory]
    [InlineData("Team meeting", "meeting")]
    [InlineData("Weekly sync with Anna", "meeting")]
    [InlineData("Dentist check-up", "medical")]
    [InlineData("Tandarts", "medical")]
    [InlineData("Flight to Lisbon", "travel")]
    [InlineData("Dinner with Sam", "personal")]
    [InlineData("Badminton", "personal")]
    [InlineData("Q4 planning", "unknown")]
    public void Titles_classify_deterministically(string title, string expected) =>
        Assert.Equal(expected, AppointmentKind.Classify(title));
}

public sealed class HabitBatchTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T07:05:00Z")); // 08:05 local
    private readonly RecordingNudgeTransport transport = new();

    public HabitBatchTests()
    {
        db.Context.Add(new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
    }

    private HabitProposalBatchService Service() =>
        new(db.Context, transport, clock, NullLogger<HabitProposalBatchService>.Instance);

    private Habit AddProposed(string place = "Bar Bello", int sentCount = 0, DateTimeOffset? lastProposed = null)
    {
        var p = new Place { Id = Guid.NewGuid(), Name = place, Lat = 52.09, Lng = 5.2 };
        var habit = new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = p.Id,
            Place = p,
            Context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.Other, null),
            AvgDurationMin = 9,
            VisitCount = 5,
            ProposalText = $"Plan {place}?",
            Rationale = "5 visits, ~9 min each",
            ProposalSentCount = sentCount,
            LastProposedAt = lastProposed,
        };
        db.Context.AddRange(p, habit);
        db.Context.SaveChanges();
        return habit;
    }

    [Fact]
    public async Task Proposals_are_batched_into_one_message_per_day()
    {
        AddProposed("Bar Bello");
        AddProposed("Albert Heijn");

        await Service().RunAsync();
        await Service().RunAsync(); // same day → nothing new

        var nudge = Assert.Single(transport.Delivered);
        Assert.Equal(NudgeKind.HabitProposal, nudge.Kind);
        Assert.Contains("Bar Bello", nudge.Text);
        Assert.Contains("Albert Heijn", nudge.Text);
        Assert.All(db.Context.Habits, h => Assert.Equal(1, h.ProposalSentCount));
    }

    [Fact]
    public async Task An_unanswered_proposal_is_resent_once_after_seven_days_and_never_again()
    {
        AddProposed("Bar Bello", sentCount: 1, lastProposed: clock.GetUtcNow().AddDays(-8));
        AddProposed("Snackbar Smul", sentCount: 2, lastProposed: clock.GetUtcNow().AddDays(-30));

        await Service().RunAsync();

        var nudge = Assert.Single(transport.Delivered);
        Assert.Contains("Bar Bello", nudge.Text);        // second (and last) send
        Assert.DoesNotContain("Snackbar", nudge.Text);   // already re-sent once
    }

    [Fact]
    public async Task A_recent_unanswered_proposal_is_not_resent_yet()
    {
        AddProposed("Bar Bello", sentCount: 1, lastProposed: clock.GetUtcNow().AddDays(-2));

        await Service().RunAsync();

        Assert.Empty(transport.Delivered);
    }

    [Fact]
    public async Task A_batch_due_in_quiet_hours_is_deferred_to_the_next_run_after_them()
    {
        var habit = AddProposed("Bar Bello");
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T21:30:00Z")); // 22:30 local, quiet 22–07

        await Service().RunAsync();
        Assert.Empty(transport.Delivered);
        Assert.Equal(0, habit.ProposalSentCount);

        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-28T07:05:00Z")); // 08:05 local next day
        await Service().RunAsync();
        Assert.Single(transport.Delivered);
    }

    [Fact]
    public async Task A_single_proposal_nudge_carries_its_habit_id_for_notification_actions()
    {
        var habit = AddProposed("Bar Bello");

        await Service().RunAsync();

        Assert.Equal(habit.Id, Assert.Single(transport.Delivered).HabitId);
    }

    [Fact]
    public async Task A_multi_proposal_nudge_carries_no_habit_id()
    {
        AddProposed("Bar Bello");
        AddProposed("Albert Heijn");

        await Service().RunAsync();

        Assert.Null(Assert.Single(transport.Delivered).HabitId);
    }

    public void Dispose() => db.Dispose();
}
