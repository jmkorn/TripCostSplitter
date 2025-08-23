using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TripSplit;

public sealed class ExplanationService
{
    private readonly HttpClient _http;
    private readonly SettlementEngine _engine;
    private readonly string? _endpoint; // e.g. https://your-resource.openai.azure.com
    private readonly string? _apiKey;
    private readonly string? _deployment; // deployment (model) name
    private const string DefaultApiVersion = "2024-02-15-preview"; // adjust if needed

    public ExplanationService(HttpClient http, SettlementEngine engine, IConfiguration config)
    {
        _http = http;
        _engine = engine;
        _endpoint = config["AZURE_OPENAI_ENDPOINT"]
            ?? config["AzureOpenAI:Endpoint"]
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        _apiKey = config["AZURE_OPENAI_KEY"]
            ?? config["AzureOpenAI:Key"]
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY");
        _deployment = config["AZURE_OPENAI_DEPLOYMENT"]
            ?? config["AzureOpenAI:Deployment"]
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_endpoint) && !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_deployment);

    public async Task<(string explanation, string prompt, bool usedLLM)> GenerateExplanationAsync(CancellationToken ct = default)
    {
        var prompt = _engine.BuildExplanationPrompt();
        if (!IsConfigured)
        {
            var transfers = _engine.SettleUp();
            var fallback = transfers.Count == 0 ? "No transfers required because everyone is already settled." : string.Join("\n", new[] { "(LLM not configured – fallback explanation)", "Settlement summary:" }.Concat(transfers.Select(t => $"- {t.From} pays {t.To} {t.Amount:C}")));
            return (fallback, prompt, false);
        }

        // Build request body for Azure OpenAI Chat Completions
        var messages = new object[]
        {
            new { role = "system", content = "You are a concise financial explainer. Keep under 250 words." },
            new { role = "user", content = prompt }
        };
        var body = new
        {
            messages,
            temperature = 0.2,
            max_tokens = 600
        };
        var json = JsonSerializer.Serialize(body);
        var url = $"{_endpoint!.TrimEnd('/')}/openai/deployments/{_deployment}/chat/completions?api-version={DefaultApiVersion}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("api-key", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            var fallback = $"Failed to get LLM explanation (status {(int)resp.StatusCode}). Showing fallback. Error: {err}";
            return (fallback, prompt, false);
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var explanation = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "(empty response)";
        return (explanation.Trim(), prompt, true);
    }
}
