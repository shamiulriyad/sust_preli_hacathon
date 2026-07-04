using QueueStormInvestigator.Models;

namespace QueueStormInvestigator.Services;

public interface IReasoningService
{
    /// <summary>
    /// Analyze a ticket and produce a structured response.
    /// Implementations must NOT throw for "I don't know" cases — they should
    /// return a safe insufficient_data / human_review_required result instead.
    /// Implementations MAY throw for genuine failures (timeout, network error,
    /// malformed LLM output) — the orchestrator catches these and falls back.
    /// </summary>
    Task<TicketResponse> AnalyzeAsync(TicketRequest request, CancellationToken cancellationToken);
}
