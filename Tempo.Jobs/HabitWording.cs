using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tempo.Core.Domain;
using Tempo.Core.Models;

namespace Tempo.Jobs;

public sealed record HabitProposalText(string Sentence, string Rationale);

/// <summary>
/// Muse Spark words the proposal from structured facts (habit-learning spec). The numbers
/// stay deterministic: every digit in the model's text must equal the computed average or
/// the visit count, otherwise the deterministic template is used instead. Model failures
/// also fall back to the template — wording can never block inference.
/// </summary>
public sealed partial class HabitWording(IMetaModels models, ILogger<HabitWording> logger)
{
    private const string Schema = """
        {"type":"object","properties":{"sentence":{"type":"string"},"rationale":{"type":"string"}},"required":["sentence","rationale"]}
        """;

    public async Task<(string Sentence, string Rationale)> WordAsync(
        string placeName, ContextKey context, double avgMin, int visitCount, CancellationToken ct = default)
    {
        var fallback = Template(placeName, context, avgMin, visitCount);
        try
        {
            var facts = System.Text.Json.JsonSerializer.Serialize(new
            {
                place = placeName,
                weekday = context.Weekday?.ToString() ?? "weekdays",
                timeOfDay = context.TimeBucket.ToString().ToLowerInvariant(),
                direction = context.Direction.ToString(),
                beforeAppointmentKind = context.AdjacentAppointmentKind,
                averageMinutes = avgMin,
                visitCount,
            });
            var result = await models.ChatJsonAsync<HabitProposalText>(
                [
                    ChatMessage.System(
                        "You word a habit proposal for a time-keeper app. Given facts about a recurring stop, " +
                        "write {\"sentence\", \"rationale\"}: the sentence reads like a friend noticing something " +
                        "and asks whether to plan for the stop; the rationale is one short evidence line. " +
                        "Use only the numbers given, keep the place name exactly as written, no clock times."),
                    ChatMessage.User(facts),
                ],
                Schema, purpose: "habit-wording", ct);
            var text = result.Value;
            if (string.IsNullOrWhiteSpace(text.Sentence) || !NumbersAreFactual(text, avgMin, visitCount))
            {
                logger.LogWarning("Habit wording altered the numbers; using the template for {Place}", placeName);
                return fallback;
            }
            return (text.Sentence.Trim(), text.Rationale.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Habit wording unavailable ({Message}); using the template for {Place}", ex.Message, placeName);
            return fallback;
        }
    }

    private static bool NumbersAreFactual(HabitProposalText text, double avgMin, int visitCount)
    {
        var allowed = new HashSet<long> { (long)Math.Round(avgMin), visitCount };
        return NumberPattern().Matches(text.Sentence + " " + text.Rationale)
            .All(m => allowed.Contains(long.Parse(m.Value)));
    }

    private static (string, string) Template(string placeName, ContextKey context, double avgMin, int visitCount)
    {
        var day = context.Weekday?.ToString() ?? "weekday";
        var when = $"{day}{(context.TimeBucket == TimeBucket.Early ? " early morning" : " " + context.TimeBucket.ToString().ToLowerInvariant())}";
        return (
            $"Looks like you usually stop at {placeName} on {when}s, about {Math.Round(avgMin)} min — want me to plan for it?",
            $"{visitCount} visits, ~{Math.Round(avgMin)} min each");
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
