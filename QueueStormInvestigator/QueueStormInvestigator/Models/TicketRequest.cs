using System.Text.Json.Serialization;

namespace QueueStormInvestigator.Models;

public class TransactionEntry
{
    [JsonPropertyName("transaction_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty; // ISO 8601 string, parsed on demand

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // transfer, payment, cash_in, cash_out, settlement, refund

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("counterparty")]
    public string? Counterparty { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // completed, failed, pending, reversed

    public DateTimeOffset? ParsedTimestamp =>
        DateTimeOffset.TryParse(Timestamp, out var dt) ? dt : null;
}

public class TicketRequest
{
    [JsonPropertyName("ticket_id")]
    public string TicketId { get; set; } = string.Empty;

    [JsonPropertyName("complaint")]
    public string Complaint { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string? Language { get; set; } // en, bn, mixed

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("user_type")]
    public string? UserType { get; set; }

    [JsonPropertyName("campaign_context")]
    public string? CampaignContext { get; set; }

    [JsonPropertyName("transaction_history")]
    public List<TransactionEntry> TransactionHistory { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Minimal structural validation. Returns null if valid, otherwise an error message.
    /// This is for the 400 (malformed) vs 422 (semantically invalid) distinction.
    /// </summary>
    public string? ValidateRequiredFields()
    {
        if (string.IsNullOrWhiteSpace(TicketId))
            return "ticket_id is required";
        return null;
    }

    public string? ValidateSemantic()
    {
        if (string.IsNullOrWhiteSpace(Complaint))
            return "complaint must not be empty";
        return null;
    }
}
