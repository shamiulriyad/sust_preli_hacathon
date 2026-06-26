using QueueStormInvestigator.Models;

namespace QueueStormInvestigator.Services;

public class ReasoningOrchestrator
{
    private readonly IReasoningService? _llmService;
    private readonly RuleBasedReasoningService _ruleBasedService;
    private readonly SafetyFilterService _safetyFilter;
    private readonly ILogger<ReasoningOrchestrator> _logger;
    private readonly TimeSpan _llmTimeout;

    public ReasoningOrchestrator(
        RuleBasedReasoningService ruleBasedService,
        SafetyFilterService safetyFilter,
        ILogger<ReasoningOrchestrator> logger,
        IConfiguration config,
        AnthropicReasoningService anthropicService)
    {
        _ruleBasedService = ruleBasedService;
        _safetyFilter = safetyFilter;
        _logger = logger;

        var timeoutSeconds = config.GetValue<double?>("LLM_TIMEOUT_SECONDS") ?? 8.0;
        _llmTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        // LLM is optional: if no API key is configured, run rule-based only.
        // This lets the same codebase work with or without an LLM, per the task requirements.
        var hasKey = !string.IsNullOrWhiteSpace(config["ANTHROPIC_API_KEY"])
                     || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        if (hasKey)
        {
            _llmService = anthropicService;
        }
    }

    public async Task<TicketResponse> AnalyzeAsync(TicketRequest request)
    {
        TicketResponse result;

        if (_llmService is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(_llmTimeout);
                var llmResult = await _llmService.AnalyzeAsync(request, cts.Token);
                result = SchemaValidator.ValidateAndNormalize(llmResult, request.TicketId);
                _logger.LogInformation("Ticket {TicketId} resolved via LLM", request.TicketId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM path failed for ticket {TicketId}, falling back to rule-based engine", request.TicketId);
                result = await RunRuleBasedAsync(request);
            }
        }
        else
        {
            result = await RunRuleBasedAsync(request);
        }

        // Safety filter ALWAYS runs, regardless of which engine produced the result.
        var safetyResult = _safetyFilter.Apply(result);
        if (safetyResult.ViolationsCaught.Count > 0)
        {
            _logger.LogWarning("Safety filter overrode response for ticket {TicketId}: {Violations}",
                request.TicketId, string.Join(",", safetyResult.ViolationsCaught));
        }

        return safetyResult.Response;
    }

    private async Task<TicketResponse> RunRuleBasedAsync(TicketRequest request)
    {
        var result = await _ruleBasedService.AnalyzeAsync(request, CancellationToken.None);
        return SchemaValidator.ValidateAndNormalize(result, request.TicketId);
    }
}
