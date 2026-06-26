using System.Text.Json;
using QueueStormInvestigator.Models;
using QueueStormInvestigator.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Render port binding: Render injects PORT, must bind 0.0.0.0:$PORT ----
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ---- DI registration ----
builder.Services.AddHttpClient<AnthropicReasoningService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15); // outer safety net above the per-call CancellationToken
});
builder.Services.AddSingleton<RuleBasedReasoningService>();
builder.Services.AddSingleton<SafetyFilterService>();
builder.Services.AddSingleton<ReasoningOrchestrator>();
builder.Services.AddLogging();

var app = builder.Build();

// ---- Lightweight Swagger UI / OpenAPI spec ----
// Kept dependency-free so the project still runs without external NuGet packages.
app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapGet("/swagger/v1/swagger.json", () => Results.Content(GetOpenApiDocument(), "application/json"));

app.MapGet("/swagger", () => Results.Content(GetSwaggerUiHtml(), "text/html"));
app.MapGet("/swagger/", () => Results.Content(GetSwaggerUiHtml(), "text/html"));
app.MapGet("/swagger/index.html", () => Results.Content(GetSwaggerUiHtml(), "text/html"));

// ---- Global exception handler: never leak stack traces / secrets, never crash the process ----
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "internal_error",
            message = "An unexpected error occurred while processing the request."
        }));
    });
});

// ---- GET /health ----
// Must be instant and dependency-free so it always responds within 60s of start,
// regardless of LLM/network state.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- POST /analyze-ticket ----
app.MapPost("/analyze-ticket", async (HttpContext context, ReasoningOrchestrator orchestrator) =>
{
    TicketRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<TicketRequest>();
    }
    catch (JsonException)
    {
        return Results.Json(new { error = "malformed_json", message = "Request body is not valid JSON." },
            statusCode: 400);
    }

    if (request is null)
    {
        return Results.Json(new { error = "malformed_json", message = "Request body could not be parsed." },
            statusCode: 400);
    }

    var requiredFieldError = request.ValidateRequiredFields();
    if (requiredFieldError is not null)
    {
        return Results.Json(new { error = "missing_required_field", message = requiredFieldError },
            statusCode: 400);
    }

    var semanticError = request.ValidateSemantic();
    if (semanticError is not null)
    {
        return Results.Json(new { error = "semantically_invalid", message = semanticError },
            statusCode: 422);
    }

    try
    {
        var response = await orchestrator.AnalyzeAsync(request);
        return Results.Ok(response);
    }
    catch (Exception)
    {
        // Should be rare — orchestrator already falls back internally — but guarantees
        // we never crash or leak details even on a truly unexpected failure.
        return Results.Json(new { error = "internal_error", message = "Could not analyze ticket." },
            statusCode: 500);
    }
});

app.Run();

static string GetSwaggerUiHtml() => """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>QueueStorm Investigator API</title>
  <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css" />
  <style>
    body { margin: 0; background: #f6f8fa; }
    .swagger-ui .topbar { display: none; }
  </style>
</head>
<body>
  <div id="swagger-ui"></div>
  <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
  <script>
    window.onload = () => {
      window.ui = SwaggerUIBundle({
        url: '/swagger/v1/swagger.json',
        dom_id: '#swagger-ui',
        deepLinking: true,
        presets: [SwaggerUIBundle.presets.apis],
        layout: 'BaseLayout'
      });
    };
  </script>
</body>
</html>
""";

static string GetOpenApiDocument() => """
{
  "openapi": "3.0.1",
  "info": {
    "title": "QueueStorm Investigator API",
    "version": "v1",
    "description": "Investigates support complaints against transaction history and returns a structured routing decision."
  },
  "servers": [
    {
      "url": "/"
    }
  ],
  "paths": {
    "/health": {
      "get": {
        "summary": "Health check",
        "responses": {
          "200": {
            "description": "API is running",
            "content": {
              "application/json": {
                "schema": {
                  "type": "object",
                  "properties": {
                    "status": {
                      "type": "string",
                      "example": "ok"
                    }
                  }
                }
              }
            }
          }
        }
      }
    },
    "/analyze-ticket": {
      "post": {
        "summary": "Analyze a support ticket",
        "description": "Classifies a customer complaint, matches it to transaction evidence, routes it to a department, and returns safe agent/customer text.",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "$ref": "#/components/schemas/TicketRequest"
              },
              "example": {
                "ticket_id": "TKT-001",
                "complaint": "I sent 500 taka to the wrong number yesterday. Please help.",
                "language": "en",
                "channel": "app",
                "user_type": "customer",
                "transaction_history": [
                  {
                    "transaction_id": "TXN-1001",
                    "timestamp": "2026-06-25T10:15:00+06:00",
                    "type": "transfer",
                    "amount": 500,
                    "counterparty": "01700000000",
                    "status": "completed"
                  }
                ],
                "metadata": {
                  "priority": "normal"
                }
              }
            }
          }
        },
        "responses": {
          "200": {
            "description": "Ticket analysis result",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/TicketResponse"
                }
              }
            }
          },
          "400": {
            "description": "Malformed JSON or missing required field"
          },
          "422": {
            "description": "Semantically invalid input"
          },
          "500": {
            "description": "Unexpected internal error"
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "TicketRequest": {
        "type": "object",
        "required": ["ticket_id", "complaint"],
        "properties": {
          "ticket_id": {
            "type": "string",
            "example": "TKT-001"
          },
          "complaint": {
            "type": "string",
            "example": "I sent 500 taka to the wrong number yesterday. Please help."
          },
          "language": {
            "type": "string",
            "nullable": true,
            "example": "en"
          },
          "channel": {
            "type": "string",
            "nullable": true,
            "example": "app"
          },
          "user_type": {
            "type": "string",
            "nullable": true,
            "example": "customer"
          },
          "campaign_context": {
            "type": "string",
            "nullable": true
          },
          "transaction_history": {
            "type": "array",
            "items": {
              "$ref": "#/components/schemas/TransactionEntry"
            }
          },
          "metadata": {
            "type": "object",
            "nullable": true,
            "additionalProperties": true
          }
        }
      },
      "TransactionEntry": {
        "type": "object",
        "properties": {
          "transaction_id": {
            "type": "string",
            "example": "TXN-1001"
          },
          "timestamp": {
            "type": "string",
            "example": "2026-06-25T10:15:00+06:00"
          },
          "type": {
            "type": "string",
            "example": "transfer"
          },
          "amount": {
            "type": "number",
            "format": "decimal",
            "example": 500
          },
          "counterparty": {
            "type": "string",
            "nullable": true,
            "example": "01700000000"
          },
          "status": {
            "type": "string",
            "example": "completed"
          }
        }
      },
      "TicketResponse": {
        "type": "object",
        "properties": {
          "ticket_id": {
            "type": "string"
          },
          "relevant_transaction_id": {
            "type": "string",
            "nullable": true
          },
          "evidence_verdict": {
            "type": "string",
            "enum": ["consistent", "inconsistent", "insufficient_data"]
          },
          "case_type": {
            "type": "string",
            "enum": ["wrong_transfer", "payment_failed", "refund_request", "duplicate_payment", "merchant_settlement_delay", "agent_cash_in_issue", "phishing_or_social_engineering", "other"]
          },
          "severity": {
            "type": "string",
            "enum": ["low", "medium", "high", "critical"]
          },
          "department": {
            "type": "string",
            "enum": ["customer_support", "dispute_resolution", "payments_ops", "merchant_operations", "agent_operations", "fraud_risk"]
          },
          "agent_summary": {
            "type": "string"
          },
          "recommended_next_action": {
            "type": "string"
          },
          "customer_reply": {
            "type": "string"
          },
          "human_review_required": {
            "type": "boolean"
          },
          "confidence": {
            "type": "number",
            "format": "double",
            "nullable": true
          },
          "reason_codes": {
            "type": "array",
            "nullable": true,
            "items": {
              "type": "string"
            }
          }
        }
      }
    }
  }
}
""";
