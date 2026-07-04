using System.Text.Json.Serialization;

namespace QueueStormInvestigator.Models;

public class TicketResponse
{
    [JsonPropertyName("ticket_id")]
    public string TicketId { get; set; } = string.Empty;

    [JsonPropertyName("relevant_transaction_id")]
    public string? RelevantTransactionId { get; set; } // null if no match

    [JsonPropertyName("evidence_verdict")]
    public string EvidenceVerdict { get; set; } = Models.EvidenceVerdict.InsufficientData;

    [JsonPropertyName("case_type")]
    public string CaseType { get; set; } = Models.CaseType.Other;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = Models.Severity.Low;

    [JsonPropertyName("department")]
    public string Department { get; set; } = Models.Department.CustomerSupport;

    [JsonPropertyName("agent_summary")]
    public string AgentSummary { get; set; } = string.Empty;

    [JsonPropertyName("recommended_next_action")]
    public string RecommendedNextAction { get; set; } = string.Empty;

    [JsonPropertyName("customer_reply")]
    public string CustomerReply { get; set; } = string.Empty;

    [JsonPropertyName("human_review_required")]
    public bool HumanReviewRequired { get; set; } = true; // default safe: require review

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("reason_codes")]
    public List<string>? ReasonCodes { get; set; }

    /// <summary>
    /// Internal-only flag (not serialized) so the orchestrator can tell downstream
    /// logging/metrics whether this came from the LLM or the rule-based fallback.
    /// </summary>
    [JsonIgnore]
    public string SourceEngine { get; set; } = "rule_based";
}
