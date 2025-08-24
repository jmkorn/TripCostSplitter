using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;
using System.Text;

namespace TripSplit;

public sealed class ExplanationService
{
    private readonly SettlementEngine _engine;
    private readonly string? _endpoint; // e.g. https://your-resource.openai.azure.com
    private readonly string? _apiKey;
    private readonly string? _deployment; // deployment (model) name
    private ChatClient? _chatClient; // lazily created

    public ExplanationService(SettlementEngine engine, IConfiguration config)
    {
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

    private ChatClient? EnsureClient()
    {
        if (!IsConfigured) return null;
        if (_chatClient is not null) return _chatClient;
        try
        {
            var azureClient = new AzureOpenAIClient(new Uri(_endpoint!), new AzureKeyCredential(_apiKey!));
            _chatClient = azureClient.GetChatClient(_deployment!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create AzureOpenAIClient: {ex.Message}");
            return null;
        }
        return _chatClient;
    }

    public async Task<(string explanation, string prompt, bool usedLLM)> GenerateExplanationAsync(CancellationToken ct = default)
    {
        var prompt = _engine.BuildExplanationPrompt();
        var client = EnsureClient();
        if (client == null)
        {
            var transfers = _engine.SettleUp();
            var fallback = transfers.Count == 0 ? "No transfers required because everyone is already settled." : string.Join("\n", new[] { "(LLM not configured – fallback explanation)", "Settlement summary:" }.Concat(transfers.Select(t => $"- {t.From} pays {t.To} {t.Amount:C}")));
            return (fallback, prompt, false);
        }

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 1000
        };

#pragma warning disable AOAI001
        // Opt-in to new property naming (max_completion_tokens) to match newer API versions
        options.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a concise financial explainer that is explaining how a total cost is getting split amongst several participants. Keep explanations simple."),
            new UserChatMessage(prompt)
        };

        try
        {
            var response = await client.CompleteChatAsync(messages, options, ct);
            var content = response.Value.Content;
            var sb = new StringBuilder();
            foreach (var item in content)
            {
                if (!string.IsNullOrWhiteSpace(item.Text)) sb.Append(item.Text);
            }
            var text = sb.ToString().Trim();
            if (string.IsNullOrEmpty(text))
            {
                // Build deterministic algorithmic explanation fallback (empty model response)
                var transfers = _engine.SettleUp();
                var nets = _engine.GetNetBalances();
                var algo = BuildAlgorithmicExplanation(nets, transfers);
                return ("(LLM returned empty content)\n" + algo, prompt, false);
            }
            return (text, prompt, true);
        }
        catch (Exception ex)
        {
            var transfers = _engine.SettleUp();
            var fallback = string.Join("\n", new[] { $"Failed to get LLM explanation: {ex.Message}", "Settlement summary:" }.Concat(transfers.Select(t => $"- {t.From} pays {t.To} {t.Amount:C}")));
            return (fallback, prompt, false);
        }
    }

    private static string BuildAlgorithmicExplanation(IReadOnlyList<SettlementEngine.NetBalance> netBalances, IReadOnlyList<SettlementEngine.Transfer> transfers)
    {
        var creditorRemaining = netBalances.Where(n => n.Net > 0m).ToDictionary(n => n.Name, n => n.Net);
        var debtorRemaining = netBalances.Where(n => n.Net < 0m).ToDictionary(n => n.Name, n => -n.Net);
        var creditorOriginal = creditorRemaining.ToDictionary(k => k.Key, v => v.Value);
        var debtorOriginal = debtorRemaining.ToDictionary(k => k.Key, v => v.Value);
        var lines = new List<string>();
        if (transfers.Count == 0)
        {
            return "All participants are already settled; no transfers required.";
        }
        lines.Add("Step-by-step settlement rationale:");
        foreach (var t in transfers)
        {
            if (!debtorRemaining.TryGetValue(t.From, out var debtorBefore)) debtorBefore = 0m;
            if (!creditorRemaining.TryGetValue(t.To, out var creditorBefore)) creditorBefore = 0m;
            var debtorAfter = Math.Max(0m, debtorBefore - t.Amount);
            var creditorAfter = Math.Max(0m, creditorBefore - t.Amount);
            lines.Add($"- {t.From} pays {t.To} {t.Amount:C}. {t.From} debt: {debtorBefore:C} -> {debtorAfter:C}; {t.To} credit: {creditorBefore:C} -> {creditorAfter:C}.");
            debtorRemaining[t.From] = debtorAfter;
            creditorRemaining[t.To] = creditorAfter;
        }
        var residualCredit = creditorRemaining.Where(kv => kv.Value != 0m).ToList();
        var residualDebt = debtorRemaining.Where(kv => kv.Value != 0m).ToList();
        if (residualCredit.Count == 0 && residualDebt.Count == 0)
        {
            lines.Add("All net balances reach zero after these transfers.");
        }
        else
        {
            lines.Add("Warning: Not all balances settled fully.");
            if (residualCredit.Count > 0) lines.Add("Remaining credits: " + string.Join(", ", residualCredit.Select(kv => $"{kv.Key}:{kv.Value:C}")));
            if (residualDebt.Count > 0) lines.Add("Remaining debts: " + string.Join(", ", residualDebt.Select(kv => $"{kv.Key}:{kv.Value:C}")));
        }
        return string.Join("\n", lines);
    }
}
