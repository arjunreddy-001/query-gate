using System.Net;
using System.Text.Json;
using DbAccessManager.Services;
using DbAccessManager.Services.Ai;
using Xunit;

namespace DbAccessManager.Tests;

public class AiProviderClientPayloadTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            if (request.Content != null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return responder(request);
        }
    }

    [Fact]
    public async Task Gemini_GenerateSqlAsync_SerializesCorrectPayloadAndParsesResponse()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var jsonResponse = JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new[]
                            {
                                new { text = "```sql\nSELECT * FROM customers WHERE is_active = true;\n```" }
                            }
                        }
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new GeminiAiClient(httpClient, "test-gemini-key", "gemini-1.5-pro", temperature: 0.2);

        var result = await client.GenerateSqlAsync("Get all active customers", "CREATE TABLE customers (id INT, is_active BOOL);", "PostgreSQL");

        // Verify CleanSqlOutput stripped markdown fences
        Assert.Equal("SELECT * FROM customers WHERE is_active = true;", result);

        // Verify Request URL and query parameter key
        var captured = mockHandler.CapturedRequest;
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent?key=test-gemini-key", captured.RequestUri?.ToString());

        // Verify serialized JSON payload
        var body = mockHandler.CapturedRequestBody;
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var systemText = root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString();
        Assert.Contains("PostgreSQL", systemText);
        Assert.Contains("CREATE TABLE customers", systemText);

        var userText = root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString();
        Assert.Equal("Get all active customers", userText);

        var temp = root.GetProperty("generationConfig").GetProperty("temperature").GetDouble();
        Assert.Equal(0.2, temp, 2);
    }

    [Fact]
    public async Task Gemini_HttpError_ThrowsInvalidOperationException()
    {
        var mockHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("API_KEY_INVALID", System.Text.Encoding.UTF8, "text/plain")
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new GeminiAiClient(httpClient, "invalid-key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateSqlAsync("query", "schema", "SQL Server"));

        Assert.Contains("Gemini request failed (HTTP 403)", ex.Message);
        Assert.Contains("API_KEY_INVALID", ex.Message);
    }

    [Fact]
    public async Task Gemini_ExplainPlanAsync_SerializesExecutionPlanDetails()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var jsonResponse = JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new[] { new { text = "Query is bottlenecked by table scan." } }
                        }
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new GeminiAiClient(httpClient, "key", "gemini-1.5-flash");

        var plan = new ExecutionPlanDto
        {
            TotalCost = 42.125,
            EstimatedRows = 10000
        };
        plan.MissingIndexes.Add(new PlanMissingIndexDto
        {
            Table = "orders",
            CreateIndexStatement = "CREATE INDEX idx_orders ON orders(customer_id)",
            Impact = 85.5
        });
        plan.Operators.Add(new PlanOperatorDto
        {
            NodeId = 1,
            PhysicalOp = "Seq Scan",
            TargetObject = "orders",
            CostPercentage = 95.0,
            SubtreeCost = 40.0,
            EstimatedRows = 10000,
            IsWarning = true,
            WarningMessage = "Full scan"
        });

        var explanation = await client.ExplainPlanAsync(plan, "PostgreSQL");
        Assert.Equal("Query is bottlenecked by table scan.", explanation);

        var body = mockHandler.CapturedRequestBody;
        Assert.NotNull(body);
        Assert.Contains("Total Cost: 42.1250", body);
        Assert.Contains("Missing Index Recommendations", body);
        Assert.Contains("CREATE INDEX idx_orders", body);
        Assert.Contains("Seq Scan", body);
    }

    [Fact]
    public async Task Anthropic_GenerateSqlAsync_SerializesHeadersAndMessagesCorrectly()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var jsonResponse = JsonSerializer.Serialize(new
            {
                content = new[]
                {
                    new { type = "text", text = "```sql\nSELECT COUNT(*) FROM users;\n```" }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new AnthropicAiClient(httpClient, "sk-ant-test-key", "claude-3-5-sonnet-20241022", temperature: 0.1);

        var sql = await client.GenerateSqlAsync("Count all users", "CREATE TABLE users (id INT);", "MySQL");

        Assert.Equal("SELECT COUNT(*) FROM users;", sql);

        var captured = mockHandler.CapturedRequest;
        Assert.NotNull(captured);
        Assert.Equal("https://api.anthropic.com/v1/messages", captured.RequestUri?.ToString());
        Assert.Equal("sk-ant-test-key", captured.Headers.GetValues("x-api-key").FirstOrDefault());
        Assert.Equal("2023-06-01", captured.Headers.GetValues("anthropic-version").FirstOrDefault());

        var body = mockHandler.CapturedRequestBody;
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("claude-3-5-sonnet-20241022", root.GetProperty("model").GetString());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.1, root.GetProperty("temperature").GetDouble(), 2);
        Assert.Contains("MySQL", root.GetProperty("system").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Count all users", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Anthropic_HttpError_ThrowsInvalidOperationException()
    {
        var mockHandler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate_limit_exceeded", System.Text.Encoding.UTF8, "text/plain")
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new AnthropicAiClient(httpClient, "sk-test");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateSqlAsync("prompt", "schema", "PostgreSQL"));

        Assert.Contains("Anthropic request failed (HTTP 429)", ex.Message);
        Assert.Contains("rate_limit_exceeded", ex.Message);
    }

    [Fact]
    public async Task OpenAiCompatible_EndpointNormalization_HandlesVariousBases()
    {
        string? capturedUri = null;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            capturedUri = req.RequestUri?.ToString();
            var jsonResponse = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content = "SELECT 1;" } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);

        // Case 1: Base host without path
        var client1 = new OpenAiCompatibleAiClient(httpClient, "http://localhost:11434", "key", "llama3");
        await client1.GenerateSqlAsync("test", "schema", "PostgreSQL");
        Assert.Equal("http://localhost:11434/v1/chat/completions", capturedUri);

        // Case 2: Base with /v1
        var client2 = new OpenAiCompatibleAiClient(httpClient, "https://api.deepseek.com/v1", "key", "deepseek-coder");
        await client2.GenerateSqlAsync("test", "schema", "PostgreSQL");
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", capturedUri);

        // Case 3: Complete endpoint path
        var client3 = new OpenAiCompatibleAiClient(httpClient, "https://api.openai.com/v1/chat/completions", "key", "gpt-4o");
        await client3.GenerateSqlAsync("test", "schema", "PostgreSQL");
        Assert.Equal("https://api.openai.com/v1/chat/completions", capturedUri);
    }

    [Fact]
    public async Task OpenAiCompatible_BearerHeaderAndPayloadStructure_AreValid()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var jsonResponse = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new { role = "assistant", content = "EXPLANATION: Missing index\nFIXED_SQL:\nSELECT * FROM t WHERE id = 1" }
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleAiClient(httpClient, "https://api.openai.com/v1", "my-openai-key", "gpt-4o", temperature: 0.3);

        var result = await client.ExplainOrFixAsync("SELECT * FROM t WHERE id = 1", "Timeout exceeded", "TABLE t(id INT)", "SQL Server");

        Assert.Contains("EXPLANATION: Missing index", result);
        Assert.Contains("FIXED_SQL:", result);

        var captured = mockHandler.CapturedRequest;
        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("my-openai-key", captured.Headers.Authorization?.Parameter);

        var body = mockHandler.CapturedRequestBody;
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble(), 2);

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Contains("Timeout exceeded", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAiCompatible_NoApiKey_DoesNotSendAuthorizationHeader()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var jsonResponse = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content = "OK" } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new OpenAiCompatibleAiClient(httpClient, "http://localhost:11434", "", "mistral");

        var (ok, msg) = await client.TestConnectionAsync();
        Assert.True(ok);
        Assert.Contains("Connected successfully", msg);

        var captured = mockHandler.CapturedRequest;
        Assert.NotNull(captured);
        Assert.Null(captured.Headers.Authorization);
    }

    [Theory]
    [InlineData("```sql\nSELECT 1;\n```", "SELECT 1;")]
    [InlineData("```\nSELECT 2;\n```", "SELECT 2;")]
    [InlineData("SELECT 3;", "SELECT 3;")]
    [InlineData("   \n\tSELECT 4;  \n ", "SELECT 4;")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OpenAiCompatible_CleanSqlOutput_StripsFencesAndTrims(string? raw, string expected)
    {
        var cleaned = OpenAiCompatibleAiClient.CleanSqlOutput(raw!);
        Assert.Equal(expected, cleaned);
    }
}
