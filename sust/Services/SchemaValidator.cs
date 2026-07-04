using QueueStormInvestigator.Models;

namespace QueueStormInvestigator.Services;

/// <summary>
/// Validates and normalizes a TicketResponse against the exact enum taxonomy.
/// Never trusts the LLM's enum casing/spelling — normalizes common variants,
/// and if a value is unrecoverable, throws so the orchestrator falls back
/// to the rule-based engine instead of shipping a schema violation.
/// </summary>
public static class SchemaValidator
{
    public class SchemaViolationException : Exception
    {
        public SchemaViolationException(string message) : base(message) { }
    }

    public static TicketResponse ValidateAndNormalize(TicketResponse response, string expectedTicketId)
    {
        response.TicketId = expectedTicketId; // always echo the original request's id, never trust LLM here

        response.CaseType = NormalizeEnum(response.CaseType, CaseType.All, CaseType.Other);
        response.EvidenceVerdict = NormalizeEnum(response.EvidenceVerdict, EvidenceVerdict.All, EvidenceVerdict.InsufficientData);
        response.Severity = NormalizeEnum(response.Severity, Severity.All, Severity.Medium);

        if (!Department.All.Contains(response.Department))
        {
            response.Department = Department.DefaultFor(response.CaseType);
        }

        if (string.IsNullOrWhiteSpace(response.AgentSummary))
            throw new SchemaViolationException("agent_summary missing");
        if (string.IsNullOrWhiteSpace(response.RecommendedNextAction))
            throw new SchemaViolationException("recommended_next_action missing");
        if (string.IsNullOrWhiteSpace(response.CustomerReply))
            throw new SchemaViolationException("customer_reply missing");

        if (response.Confidence is < 0 or > 1)
            response.Confidence = null;

        return response;
    }

    private static string NormalizeEnum(string? value, HashSet<string> allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var lower = value.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        return allowed.Contains(lower) ? lower : fallback;
    }
}
