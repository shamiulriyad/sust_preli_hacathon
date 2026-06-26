using System.Text.RegularExpressions;
using QueueStormInvestigator.Models;

namespace QueueStormInvestigator.Services;

/// <summary>
/// Deterministic, dependency-free reasoning engine. Used as the fallback when
/// the LLM is slow, unavailable, or returns invalid output. Also usable standalone
/// (no LLM at all) — this is a complete, self-sufficient implementation of the
/// investigator logic, just less flexible on novel/unseen phrasing than an LLM.
///
/// Design principle: when genuinely unsure, prefer "other" / "insufficient_data" /
/// human_review_required=true over a confident wrong guess.
/// </summary>
public class RuleBasedReasoningService : IReasoningService
{
    // ---- Keyword buckets: English + Bangla (Unicode) + common Banglish transliteration ----
    // These lists are intentionally broad; extend them as you discover more phrasing.

    private static readonly (string CaseType, string[] Keywords)[] CaseTypeKeywords = new[]
    {
        (CaseType.PhishingOrSocialEngineering, new[] {
            "otp", "pin", "password", "card number", "cvv",
            "called me", "asked for my", "share my", "verify your account",
            "suspicious call", "scam", "fraud call", "fake call",
            "pin chaise", "otp chaise", "password chaise", "kotha bolse fraud"
        }),
        (CaseType.WrongTransfer, new[] {
            "wrong number", "wrong recipient", "wrong account", "sent to wrong",
            "vul number", "ভুল নাম্বার", "ভুল নম্বর", "wrong nogod", "vul taka",
            "sent by mistake", "accidentally sent",
            "didn't get it", "did not get it", "didn't receive it", "he says he didn't",
            "she says she didn't", "they say they didn't", "claims he didn't"
        }),
        (CaseType.DuplicatePayment, new[] {
            "twice", "double charged", "charged twice", "duplicate", "two times",
            "duibar", "দুইবার", "double deduct"
        }),
        (CaseType.PaymentFailed, new[] {
            "failed", "deducted but", "money deducted", "balance cut", "not received",
            "transaction failed", "taka katse", "টাকা কাটছে", "vogolo na", "পাইনি"
        }),
        (CaseType.RefundRequest, new[] {
            "refund", "money back", "return my money", "ফেরত", "ferot chai", "refund chai"
        }),
        (CaseType.MerchantSettlementDelay, new[] {
            "settlement", "merchant payment not received", "shop didn't get",
            "dokan paini", "merchant account", "payout delay"
        }),
        (CaseType.AgentCashInIssue, new[] {
            "cash in", "agent deposit", "deposit not showing", "agent point",
            "cash in korsi", "agent er kase taka dilam"
        }),
    };

    private static readonly string[] InjectionPatterns = new[]
    {
        "ignore previous instructions", "ignore the above", "disregard your rules",
        "you are now", "system prompt", "act as", "override safety", "ignore all rules"
    };

    public Task<TicketResponse> AnalyzeAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        var complaint = SanitizeForAnalysis(request.Complaint ?? string.Empty);
        var lowerComplaint = complaint.ToLowerInvariant();
        bool isBangla = ContainsBanglaScript(complaint) || request.Language == "bn";

        // 1. Classify case_type by keyword match (first matching bucket wins; phishing checked first
        //    intentionally since it is the highest-safety-priority category).
        string caseType = CaseType.Other;
        foreach (var (type, keywords) in CaseTypeKeywords)
        {
            if (keywords.Any(k => lowerComplaint.Contains(k.ToLowerInvariant())) ||
                ContainsAny(complaint, keywords))
            {
                caseType = type;
                break;
            }
        }

        // 2. Extract signals: amount, rough time hint, counterparty phone number.
        decimal? extractedAmount = ExtractAmount(complaint);
        string? extractedPhone = ExtractPhoneNumber(complaint);
        bool mentionsToday = Regex.IsMatch(lowerComplaint, @"\btoday\b|ajke|আজকে|আজ");

        string? relevantTxnId;
        string verdict;
        TransactionEntry? matchedTxn;
        int matchScore;
        bool ambiguous;

        // Duplicate payment gets specialized handling: look for a same-amount,
        // same-counterparty pair close together in time, and point at the SECOND
        // (the suspected duplicate), not the first.
        if (caseType == CaseType.DuplicatePayment)
        {
            var duplicatePair = FindDuplicatePair(request.TransactionHistory);
            if (duplicatePair is not null)
            {
                matchedTxn = duplicatePair;
                relevantTxnId = duplicatePair.TransactionId;
                verdict = EvidenceVerdict.Consistent;
                matchScore = 2;
                ambiguous = false;
            }
            else
            {
                (matchedTxn, matchScore, ambiguous) = MatchTransaction(
                    request.TransactionHistory, extractedAmount, extractedPhone, mentionsToday);
                relevantTxnId = ambiguous ? null : matchedTxn?.TransactionId;
                verdict = ambiguous
                    ? EvidenceVerdict.InsufficientData
                    : DecideVerdict(caseType, matchedTxn, matchScore, request.TransactionHistory.Count, request.TransactionHistory);
            }
        }
        else
        {
            (matchedTxn, matchScore, ambiguous) = MatchTransaction(
                request.TransactionHistory, extractedAmount, extractedPhone, mentionsToday);
            relevantTxnId = ambiguous ? null : matchedTxn?.TransactionId;
            verdict = ambiguous
                ? EvidenceVerdict.InsufficientData
                : DecideVerdict(caseType, matchedTxn, matchScore, request.TransactionHistory.Count, request.TransactionHistory);
        }

        // 3. Derive severity, department, human_review_required.
        string department = Department.DefaultFor(caseType, verdict);
        decimal? effectiveAmount = extractedAmount ?? matchedTxn?.Amount;
        string severity = DeriveSeverity(caseType, verdict, effectiveAmount, ambiguous);
        bool humanReview = DeriveHumanReview(caseType, verdict, effectiveAmount, ambiguous);

        // 4. Build text fields from templates (never free-generated -> safety guaranteed).
        //    Replies are generated in Bangla when the complaint/language signal indicates Bangla.
        var (agentSummary, nextAction, customerReply) = BuildTextFields(
            request.TicketId, caseType, verdict, relevantTxnId, extractedAmount ?? matchedTxn?.Amount,
            ambiguous, request.UserType, isBangla);

        var response = new TicketResponse
        {
            TicketId = request.TicketId,
            RelevantTransactionId = relevantTxnId,
            EvidenceVerdict = verdict,
            CaseType = caseType,
            Severity = severity,
            Department = department,
            AgentSummary = agentSummary,
            RecommendedNextAction = nextAction,
            CustomerReply = customerReply,
            HumanReviewRequired = humanReview,
            Confidence = ambiguous ? 0.6 : (matchScore > 0 ? Math.Min(0.5 + matchScore * 0.15, 0.95) : 0.4),
            ReasonCodes = BuildReasonCodes(caseType, verdict, relevantTxnId, ambiguous),
            SourceEngine = "rule_based"
        };

        return Task.FromResult(response);
    }

    private static bool ContainsBanglaScript(string text) =>
        text.Any(c => c >= '\u0980' && c <= '\u09FF');

    private static bool ContainsAny(string text, string[] keywords) =>
        keywords.Any(k => k.Any(c => c >= '\u0980' && c <= '\u09FF') && text.Contains(k));

    /// <summary>
    /// Finds two transactions with identical amount and counterparty occurring close
    /// together in time (within ~10 minutes) — the signature of a duplicate charge.
    /// Returns the SECOND transaction (the suspected duplicate), per investigation
    /// convention: the first is presumed the legitimate charge.
    /// </summary>
    private static TransactionEntry? FindDuplicatePair(List<TransactionEntry> history)
    {
        var sorted = history
            .Where(t => t.ParsedTimestamp.HasValue)
            .OrderBy(t => t.ParsedTimestamp)
            .ToList();

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            for (int j = i + 1; j < sorted.Count; j++)
            {
                var a = sorted[i];
                var b = sorted[j];
                if (a.Amount == b.Amount &&
                    a.Counterparty == b.Counterparty &&
                    Math.Abs((b.ParsedTimestamp!.Value - a.ParsedTimestamp!.Value).TotalMinutes) <= 10)
                {
                    return b; // the later one is the suspected duplicate
                }
            }
        }
        return null;
    }

    // Strips anything that looks like an injection attempt before it's used in
    // any downstream string building. We classify on the raw lowercase text but
    // never echo attacker-controlled instructions back into output fields.
    private static string SanitizeForAnalysis(string complaint)
    {
        var result = complaint;
        foreach (var pattern in InjectionPatterns)
        {
            result = Regex.Replace(result, Regex.Escape(pattern), "[redacted]", RegexOptions.IgnoreCase);
        }
        return result;
    }

    private static decimal? ExtractAmount(string text)
    {
        // Matches "5000 taka", "5,000 BDT", "tk 5000", bare numbers near currency words
        var match = Regex.Match(text, @"(\d[\d,]*)\s*(taka|tk|bdt|৫০০০)?", RegexOptions.IgnoreCase);
        if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var amount))
        {
            return amount;
        }
        return null;
    }

    private static string? ExtractPhoneNumber(string text)
    {
        var match = Regex.Match(text, @"(\+?880)?1[3-9]\d{8}");
        return match.Success ? match.Value : null;
    }

    private static (TransactionEntry? Txn, int Score, bool Ambiguous) MatchTransaction(
        List<TransactionEntry> history, decimal? amount, string? phone, bool mentionsToday)
    {
        TransactionEntry? best = null;
        int bestScore = 0;
        var topScorers = new List<TransactionEntry>();

        foreach (var txn in history)
        {
            int score = 0;
            if (amount.HasValue && txn.Amount == amount.Value) score++;
            if (!string.IsNullOrEmpty(phone) && txn.Counterparty?.Contains(phone) == true) score++;
            if (mentionsToday && txn.ParsedTimestamp.HasValue &&
                txn.ParsedTimestamp.Value.Date == DateTimeOffset.UtcNow.Date) score++;

            if (score > bestScore)
            {
                bestScore = score;
                best = txn;
                topScorers.Clear();
                topScorers.Add(txn);
            }
            else if (score == bestScore && score > 0)
            {
                topScorers.Add(txn);
            }
        }

        // Ambiguous when more than one transaction ties for the best score on a WEAK
        // signal (amount-only match, no phone/time corroboration) AND they point to
        // different counterparties. Picking one at random here would be a guess, not
        // a finding — the spec explicitly wants insufficient_data + clarification in
        // this situation rather than a confident wrong pick.
        bool ambiguous = bestScore > 0 && bestScore <= 1 &&
            topScorers.Select(t => t.Counterparty).Distinct().Count() > 1;

        if (ambiguous)
        {
            return (null, bestScore, true);
        }

        return (bestScore > 0 ? best : null, bestScore, false);
    }

    /// <summary>
    /// Detects whether the matched transaction's counterparty appears repeatedly
    /// in the history (an "established recipient"). A wrong_transfer claim against
    /// a recipient the customer has paid multiple times before is suspicious —
    /// this is exactly the kind of evidence-vs-claim contradiction the investigator
    /// is supposed to catch rather than rubber-stamp.
    /// </summary>
    private static bool IsEstablishedRecipient(List<TransactionEntry> history, TransactionEntry matched)
    {
        if (string.IsNullOrEmpty(matched.Counterparty)) return false;
        int priorCount = history.Count(t =>
            t.TransactionId != matched.TransactionId &&
            t.Counterparty == matched.Counterparty &&
            t.Status == TransactionStatus.Completed);
        return priorCount >= 2; // two or more OTHER completed transfers to the same party
    }

    private static string DecideVerdict(string caseType, TransactionEntry? matched, int matchScore, int historyCount, List<TransactionEntry> fullHistory)
    {
        if (historyCount == 0) return EvidenceVerdict.InsufficientData;
        if (matched is null || matchScore == 0) return EvidenceVerdict.InsufficientData;

        // Case-specific consistency checks against the matched transaction's actual status.
        return caseType switch
        {
            CaseType.PaymentFailed => matched.Status is TransactionStatus.Failed or TransactionStatus.Pending
                ? EvidenceVerdict.Consistent
                : EvidenceVerdict.Inconsistent,

            // "Money not received" complaints for cash-in/settlement are consistent
            // with the data whenever the matched transaction is still pending or
            // failed — that pending/failed state IS the evidence supporting the
            // claim, even with only an amount-level match (status, not counterparty
            // corroboration, is what proves these cases).
            CaseType.AgentCashInIssue or CaseType.MerchantSettlementDelay =>
                matched.Status is TransactionStatus.Pending or TransactionStatus.Failed
                    ? EvidenceVerdict.Consistent
                    : EvidenceVerdict.Inconsistent,

            CaseType.WrongTransfer => matched.Status == TransactionStatus.Completed
                ? (IsEstablishedRecipient(fullHistory, matched)
                    ? EvidenceVerdict.Inconsistent   // repeat recipient contradicts "wrong number" claim
                    : EvidenceVerdict.Consistent)
                : EvidenceVerdict.InsufficientData,

            CaseType.RefundRequest => matched.Status == TransactionStatus.Reversed
                ? EvidenceVerdict.Inconsistent // already refunded, complaint may be outdated
                : EvidenceVerdict.Consistent,

            _ => matchScore >= 2 ? EvidenceVerdict.Consistent : EvidenceVerdict.InsufficientData
        };
    }

    /// <summary>
    /// Severity is driven primarily by case_type and evidence_verdict, not raw
    /// amount alone — a clear-cut payment_failed is operationally urgent (money
    /// stuck) even at modest amounts, while a low-amount refund_request the
    /// customer simply changed their mind about is genuinely low priority.
    /// </summary>
    private static string DeriveSeverity(string caseType, string verdict, decimal? amount, bool ambiguous)
    {
        if (caseType == CaseType.PhishingOrSocialEngineering) return Severity.Critical;

        if (caseType == CaseType.Other) return Severity.Low;

        if (caseType == CaseType.WrongTransfer)
        {
            if (verdict == EvidenceVerdict.Consistent) return Severity.High;
            return Severity.Medium; // inconsistent or insufficient_data (ambiguous)
        }

        if (caseType == CaseType.PaymentFailed) return Severity.High;

        if (caseType == CaseType.DuplicatePayment) return Severity.High;

        if (caseType == CaseType.AgentCashInIssue) return Severity.High;

        if (caseType == CaseType.MerchantSettlementDelay) return Severity.Medium;

        if (caseType == CaseType.RefundRequest)
        {
            // A routine "changed my mind" refund on a modest amount is low priority;
            // larger amounts or unverified evidence warrant more attention.
            if (verdict != EvidenceVerdict.Consistent) return Severity.Medium;
            if (amount.HasValue && amount.Value >= 5000) return Severity.Medium;
            return Severity.Low;
        }

        return Severity.Medium;
    }

    /// <summary>
    /// human_review_required follows the same case_type-driven logic: clear-cut,
    /// low-stakes, evidence-backed cases that an agent can action directly do NOT
    /// need to sit in a human review queue — only genuinely disputed, ambiguous-
    /// but-unresolved, high-value, or safety-sensitive cases do.
    /// </summary>
    private static bool DeriveHumanReview(string caseType, string verdict, decimal? amount, bool ambiguous)
    {
        if (caseType == CaseType.PhishingOrSocialEngineering) return true;

        if (caseType == CaseType.Other) return false; // nothing concrete to escalate yet

        if (ambiguous) return false; // route to a clarifying question first, not a human queue

        if (caseType == CaseType.WrongTransfer) return true; // disputes always need human sign-off

        if (caseType == CaseType.PaymentFailed) return false; // payments_ops can verify and act directly

        if (caseType == CaseType.DuplicatePayment) return true; // money taken twice always needs review

        if (caseType == CaseType.AgentCashInIssue) return true; // money-in-limbo always needs follow-up

        if (caseType == CaseType.MerchantSettlementDelay) return false; // routine ops check, not a dispute

        if (caseType == CaseType.RefundRequest)
        {
            if (verdict != EvidenceVerdict.Consistent) return true;
            if (amount.HasValue && amount.Value >= 5000) return true;
            return false; // small, evidence-backed, voluntary refund request
        }

        if (amount.HasValue && amount.Value >= 10000) return true;
        return false;
    }

    private static List<string> BuildReasonCodes(string caseType, string verdict, string? txnId, bool ambiguous)
    {
        var codes = new List<string> { caseType, verdict };
        if (ambiguous) codes.Add("ambiguous_match");
        else if (txnId is not null) codes.Add("transaction_match");
        else codes.Add("no_transaction_match");
        return codes;
    }

    // ---- Template library: every customer-facing string is pre-written and policy-safe.
    // No free text generation here, so safety rules can never be violated by this path. ----

    private static (string Summary, string NextAction, string Reply) BuildTextFields(
        string ticketId, string caseType, string verdict, string? txnId, decimal? amount,
        bool ambiguous, string? userType, bool isBangla)
    {
        string amountText = amount.HasValue ? $"{amount.Value} BDT" : "the reported amount";
        string txnRef = txnId ?? "no matching transaction";
        bool isMerchant = userType == "merchant";

        // Ambiguous match: multiple equally-plausible transactions, do not guess —
        // ask a clarifying question instead of routing a dispute prematurely.
        if (ambiguous)
        {
            string ambSummary = $"Multiple transactions of {amountText} found on the relevant date; " +
                                 "cannot determine which one the complaint refers to without further input.";
            string ambNextAction = "Reply to customer requesting a disambiguating detail (recipient identity or " +
                                    "exact time) before identifying the correct transaction. Do not initiate a dispute yet.";
            string ambReply = isBangla
                ? "আপনার অভিযোগের জন্য ধন্যবাদ। ওই তারিখে একাধিক লেনদেন পাওয়া গেছে। সঠিক লেনদেনটি চিহ্নিত করতে আমাদের আরও তথ্য (যেমন প্রাপকের নাম্বার) প্রয়োজন। অনুগ্রহ করে আপনার পিন বা ওটিপি কারো সাথে শেয়ার করবেন না।"
                : "Thank you for reaching out. We found multiple matching transactions around that time. Could you " +
                  "share a bit more detail (such as the recipient's number) so we can identify the correct one? " +
                  "Please do not share your PIN or OTP with anyone.";
            return (ambSummary, ambNextAction, ambReply);
        }

        string summary = caseType switch
        {
            CaseType.PhishingOrSocialEngineering =>
                "Customer reports a suspicious call/message attempting to obtain credentials. No transaction match required.",
            CaseType.WrongTransfer =>
                $"Customer reports sending {amountText} to the wrong recipient. Matched transaction: {txnRef}." +
                (verdict == EvidenceVerdict.Inconsistent ? " History shows repeated prior transfers to this same recipient, contradicting the wrong-transfer claim." : ""),
            CaseType.PaymentFailed =>
                $"Customer reports a failed transaction with possible balance deduction of {amountText}. Matched transaction: {txnRef}.",
            CaseType.RefundRequest =>
                $"Customer is requesting a refund of {amountText}. Matched transaction: {txnRef}.",
            CaseType.DuplicatePayment =>
                $"Customer reports being charged twice for {amountText}. Suspected duplicate transaction: {txnRef}.",
            CaseType.MerchantSettlementDelay =>
                $"{(isMerchant ? "Merchant" : "Customer")} reports a settlement delay. Matched transaction: {txnRef}.",
            CaseType.AgentCashInIssue =>
                $"Customer reports a cash-in via agent not reflected in balance. Matched transaction: {txnRef}.",
            _ => $"Customer complaint could not be confidently classified. Evidence verdict: {verdict}."
        };

        string nextAction = caseType switch
        {
            CaseType.PhishingOrSocialEngineering =>
                "Escalate to fraud & risk team immediately. Do not contact customer requesting any credentials.",
            CaseType.WrongTransfer when verdict == EvidenceVerdict.Inconsistent =>
                $"Flag for human review. Verify with the customer whether this was genuinely a wrong transfer given the established transaction pattern with {txnRef}'s recipient.",
            CaseType.WrongTransfer or CaseType.RefundRequest or CaseType.DuplicatePayment =>
                $"Route to dispute resolution for manual verification of {txnRef} before any action is taken.",
            CaseType.PaymentFailed =>
                $"Verify transaction status for {txnRef} in core banking system before responding further.",
            CaseType.MerchantSettlementDelay =>
                $"Forward to merchant operations to check settlement batch status for {txnRef}.",
            CaseType.AgentCashInIssue =>
                $"Forward to agent operations to reconcile cash-in record for {txnRef}.",
            _ => "Route to customer support for manual triage; insufficient evidence for automated routing."
        };

        string reply = (caseType, isBangla) switch
        {
            (CaseType.PhishingOrSocialEngineering, true) =>
                "এই বিষয়ে জানানোর জন্য ধন্যবাদ। অনুগ্রহ করে কারো সাথে আপনার পিন, ওটিপি, পাসওয়ার্ড বা কার্ডের তথ্য শেয়ার করবেন না, " +
                "এমনকি যদি কেউ আমাদের প্রতিনিধি হিসেবে দাবি করে। আমরা কখনো এসব তথ্য চাইব না। সন্দেহজনক যোগাযোগের বিষয়ে শুধুমাত্র " +
                "আমাদের অফিসিয়াল সাপোর্ট চ্যানেলে রিপোর্ট করুন।",

            (CaseType.PhishingOrSocialEngineering, false) =>
                "Thank you for reporting this. Please do not share your PIN, OTP, password, or card details with anyone, " +
                "including anyone claiming to represent us. We will never ask for these. Our official support channels " +
                "are the only verified contact points; please report any suspicious contact to them.",

            (CaseType.WrongTransfer, true) =>
                $"আমরা {txnRef} লেনদেন সম্পর্কে আপনার অভিযোগ নথিভুক্ত করেছি। আমাদের টিম বিস্তারিত যাচাই করবে এবং যোগ্য পরিমাণ " +
                "অফিসিয়াল চ্যানেলের মাধ্যমে ফেরত দেওয়া হবে যদি যাচাই-বাছাইয়ে নিশ্চিত হয়।",

            (CaseType.WrongTransfer, false) =>
                $"We have noted your concern regarding transaction {txnRef}. Our team will verify the " +
                "details, and any eligible amount will be processed through official channels following review.",

            (CaseType.RefundRequest, true) =>
                $"আমরা {txnRef} সম্পর্কিত আপনার রিফান্ড অনুরোধ পেয়েছি। আমাদের টিম এই কেসটি পর্যালোচনা করবে, এবং যাচাইয়ের পর " +
                "যোগ্য পরিমাণ অফিসিয়াল চ্যানেলের মাধ্যমে ফেরত দেওয়া হবে।",

            (CaseType.RefundRequest, false) =>
                $"We have received your refund request related to {txnRef}. Our team will review this case, and " +
                "any eligible amount will be returned through official channels following verification.",

            (CaseType.PaymentFailed, true) =>
                $"আমরা {txnRef} লেনদেন সম্পর্কে আপনার সমস্যা বুঝতে পেরেছি। আমাদের টিম লেনদেনের অবস্থা পর্যালোচনা করছে, এবং " +
                "যাচাইয়ের পর যোগ্য পরিমাণ অফিসিয়াল চ্যানেলের মাধ্যমে সমাধান করা হবে।",

            (CaseType.PaymentFailed, false) =>
                $"We understand your concern about transaction {txnRef}. Our team is reviewing the transaction status, " +
                "and any eligible amount will be addressed through official channels once verified.",

            (CaseType.DuplicatePayment, true) =>
                $"আমরা {txnRef} সম্পর্কিত সম্ভাব্য দ্বিগুণ চার্জের রিপোর্ট নথিভুক্ত করেছি। আমাদের টিম এটি যাচাই করবে, এবং নিশ্চিত হলে " +
                "যোগ্য পরিমাণ অফিসিয়াল চ্যানেলের মাধ্যমে ফেরত দেওয়া হবে।",

            (CaseType.DuplicatePayment, false) =>
                $"We have noted your report of a possible duplicate charge related to {txnRef}. Our team will verify this, " +
                "and any eligible amount will be returned through official channels if confirmed.",

            (CaseType.MerchantSettlementDelay, true) =>
                "আমরা আপনার সেটেলমেন্ট সংক্রান্ত উদ্বেগ নথিভুক্ত করেছি। আমাদের মার্চেন্ট অপারেশনস টিম আপনার অ্যাকাউন্ট পর্যালোচনা করে " +
                "অফিসিয়াল চ্যানেলের মাধ্যমে যোগাযোগ করবে।",

            (CaseType.MerchantSettlementDelay, false) =>
                (isMerchant
                    ? "We have noted your settlement concern. Our merchant operations team will review your account and respond "
                    : "We have noted this settlement concern. Our merchant operations team will review the account and respond ") +
                "through official channels.",

            (CaseType.AgentCashInIssue, true) =>
                "আমরা আপনার ক্যাশ-ইন সংক্রান্ত উদ্বেগ নথিভুক্ত করেছি। আমাদের টিম এজেন্টের রেকর্ডের সাথে এটি পুনর্মিলন করবে এবং " +
                "অফিসিয়াল চ্যানেলের মাধ্যমে যোগাযোগ করবে।",

            (CaseType.AgentCashInIssue, false) =>
                "We have noted your cash-in concern. Our team will reconcile this with the agent record and follow up " +
                "through official channels.",

            (_, true) =>
                "যোগাযোগ করার জন্য ধন্যবাদ। আমাদের সাপোর্ট টিম আপনার কেসটি পর্যালোচনা করবে এবং অফিসিয়াল চ্যানেলের মাধ্যমে সাড়া দেবে। " +
                "আমরা কখনো আপনার পিন, ওটিপি বা পাসওয়ার্ড চাইব না।",

            (_, false) =>
                "Thank you for reaching out. Our support team will review your case and respond through official channels. " +
                "We will never ask for your PIN, OTP, or password."
        };

        return (summary, nextAction, reply);
    }
}