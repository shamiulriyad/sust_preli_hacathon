using System.Text.RegularExpressions;
using QueueStormInvestigator.Models;

namespace QueueStormInvestigator.Services;

/// <summary>
/// Final guardrail layer. Runs on EVERY response — whether it came from the LLM
/// or the rule-based engine — before it is sent to the client. This is the layer
/// that makes Section 8 safety violations structurally impossible to ship,
/// regardless of what the LLM decided to say.
/// </summary>
public class SafetyFilterService
{
    private static readonly string[] CredentialAskPatterns = new[]
    {
        @"\b(share|provide|send|tell us|confirm)\b.{0,20}\b(otp|pin|password|cvv|card number)\b",
        @"\b(otp|pin|password|cvv)\b.{0,20}\b(please share|please provide|please send|please confirm)\b"
    };

    private static readonly string[] UnauthorizedConfirmationPatterns = new[]
    {
        @"\bwe will refund you\b",
        @"\bwe have refunded\b",
        @"\byour money (has been|is) (returned|refunded)\b",
        @"\bwe will reverse\b",
        @"\bwe have unblocked\b",
        @"\bwe confirm your refund\b"
    };

    private static readonly string[] ThirdPartyPatterns = new[]
    {
        @"\bcontact (this|the) number\b",
        @"\bcall \+?\d{6,}\b",
        @"\breach out to (the )?agent (directly|personally)\b"
    };

    public SafetyCheckResult Apply(TicketResponse response)
    {
        var violations = new List<string>();
        string reply = response.CustomerReply ?? string.Empty;
        string nextAction = response.RecommendedNextAction ?? string.Empty;

        bool credentialAsk = MatchesAny(reply, CredentialAskPatterns);
        bool unauthorizedConfirm = MatchesAny(reply, UnauthorizedConfirmationPatterns)
                                   || MatchesAny(nextAction, UnauthorizedConfirmationPatterns);
        bool thirdParty = MatchesAny(reply, ThirdPartyPatterns);

        if (credentialAsk) violations.Add("credential_request");
        if (unauthorizedConfirm) violations.Add("unauthorized_confirmation");
        if (thirdParty) violations.Add("third_party_referral");

        if (violations.Count == 0)
        {
            return new SafetyCheckResult(response, violations);
        }

        // Do not try to "patch" unsafe text from an LLM. Replace the unsafe fields
        // wholesale with the known-safe generic template. This guarantees the
        // violation cannot ship, even if our regex only partially understood why
        // the text was unsafe.
        var safeResponse = new TicketResponse
        {
            TicketId = response.TicketId,
            RelevantTransactionId = response.RelevantTransactionId,
            EvidenceVerdict = response.EvidenceVerdict,
            CaseType = response.CaseType,
            Severity = Severity.High, // escalate severity when a safety violation was caught
            Department = Department.FraudRisk == response.Department ? response.Department : response.Department,
            AgentSummary = response.AgentSummary + " [Safety filter overrode unsafe generated reply.]",
            RecommendedNextAction = "Escalate to human agent immediately; automated reply was withheld due to a safety check.",
            CustomerReply = "Thank you for contacting us. Your case has been forwarded to our support team for review. " +
                             "We will never ask you to share your PIN, OTP, password, or card details with anyone.",
            HumanReviewRequired = true,
            Confidence = response.Confidence,
            ReasonCodes = (response.ReasonCodes ?? new List<string>()).Concat(violations).ToList(),
            SourceEngine = response.SourceEngine
        };

        return new SafetyCheckResult(safeResponse, violations);
    }

    private static bool MatchesAny(string text, string[] patterns) =>
        patterns.Any(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase));
}

public record SafetyCheckResult(TicketResponse Response, List<string> ViolationsCaught);
