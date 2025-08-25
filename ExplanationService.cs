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

    public async Task<(string llmExplanation, string algorithmicExplanation, string prompt, bool usedLLM)> GenerateExplanationAsync(CancellationToken ct = default)
    {
        var prompt = _engine.BuildExplanationPrompt();
        var netsForAlgo = _engine.GetNetBalances();
        var transfersForAlgo = _engine.SettleUp();
        var algorithmic = BuildAlgorithmicExplanation(netsForAlgo, transfersForAlgo);
        try
        {
            Console.WriteLine($"[Explain] Start GenerateExplanationAsync | Configured={IsConfigured} | People={_engine.GetPeople().Count} | Expenses={_engine.GetExpenses().Count}");
            Console.WriteLine($"[Explain] Prompt length={prompt.Length} chars | First 120: {TruncateForLog(prompt,120)}");
        }
        catch { /* logging best-effort */ }
        var client = EnsureClient();
        if (client == null)
        {
            // No LLM configured; return empty LLM explanation (or brief notice) plus algorithmic explanation
            var llmNotice = ""; // keep empty so UI can explicitly show lack of LLM output
            return (llmNotice, algorithmic, prompt, false);
        }

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 1000
        };

#pragma warning disable AOAI001
        // Opt-in to new property naming (max_completion_tokens) to match newer API versions
        options.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001

        var sb = new StringBuilder();
        sb.Append("You are a concise financial explainer that is explaining how a total cost is getting split amongst several participants. Explain it like you are explaining it to middle schoolers.");
        sb.Append(" Do not explain how some participants fronted the cost and now that must be divided amongst the rest, that is simple. Focus your explanation on the algorithmic approach to settling debts and credits.");
        var systemMessage = new SystemChatMessage(sb.ToString());

        var messages = new List<ChatMessage>
        {
            systemMessage,
            new UserChatMessage(prompt)
        };

        sb.Clear();
        try
        {
            Console.WriteLine($"[Explain] Calling Azure OpenAI deployment '{_deployment}' with {messages.Count} messages");
            var response = await client.CompleteChatAsync(messages, options, ct);
            Console.WriteLine("[Explain] Response received.");
            var content = response.Value.Content;
            try
            {
                // Attempt to log token usage if available (SDK dependent)
                var usageProp = response.Value.GetType().GetProperty("Usage");
                var usageVal = usageProp?.GetValue(response.Value);
                if (usageVal != null)
                {
                    var pt = usageVal.GetType().GetProperty("InputTokenCount")?.GetValue(usageVal);
                    var ctoks = usageVal.GetType().GetProperty("OutputTokenCount")?.GetValue(usageVal) ?? usageVal.GetType().GetProperty("CompletionTokenCount")?.GetValue(usageVal);
                    var tt = usageVal.GetType().GetProperty("TotalTokenCount")?.GetValue(usageVal);
                    Console.WriteLine($"[Explain] Token usage | input={pt} output={ctoks} total={tt}");
                }
            }
            catch { }
            int idx = 0;
            foreach (var item in content)
            {
                var snippet = item.Text ?? string.Empty;
                Console.WriteLine($"[Explain] Content[{idx}] length={snippet.Length} | First 100: {TruncateForLog(snippet,100)}");
                idx++;
                if (!string.IsNullOrWhiteSpace(snippet)) sb.Append(snippet);
            }
            var text = sb.ToString().Trim();
            // Always return algorithmic alongside; if empty LLM response, usedLLM=false
            if (string.IsNullOrEmpty(text))
            {
                return ("", algorithmic, prompt, false);
            }
            Console.WriteLine("[Explain] Final LLM explanation length=" + text.Length + " | First 150: " + TruncateForLog(text,150));
            return (text, algorithmic, prompt, true);
        }
        catch (Exception ex)
        {
            // Failure -> empty LLM explanation, still show algorithmic
            Console.WriteLine($"LLM explanation error: {ex.Message}");
            Console.WriteLine("[Explain] Exception: " + ex.GetType().Name + " | " + ex.Message);
            Console.WriteLine("[Explain] Stack: " + ex.StackTrace);
            return ("", algorithmic, prompt, false);
        }
    }

    private static string TruncateForLog(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= max) return value.Replace('\n',' ').Replace('\r',' ');
        return value.Substring(0, max).Replace('\n',' ').Replace('\r',' ') + "…";
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
