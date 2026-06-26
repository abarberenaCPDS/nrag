using System.Text.Json;
using DotnetRag.Blazor.Models;
using Microsoft.Extensions.Configuration;

namespace DotnetRag.Blazor.State;

public sealed class SettingsState
{
    public SettingsState(IConfiguration config)
    {
        var s = config.GetSection("Settings");
        Temperature = s.GetValue("Temperature", 0.2);
        TopP = s.GetValue("TopP", 0.7);
        MaxTokens = s.GetValue("MaxTokens", 1024);
        VdbTopK = s.GetValue("VdbTopK", 100);
        RerankerTopK = s.GetValue("RerankerTopK", 10);
        ConfidenceThreshold = s.GetValue("ConfidenceThreshold", 0.25);
        EnableReranker = s.GetValue("EnableReranker", true);
        EnableCitations = s.GetValue("EnableCitations", true);
        EnableGuardrails = s.GetValue("EnableGuardrails", false);
        EnableQueryRewriting = s.GetValue("EnableQueryRewriting", false);
        EnableVlmInference = s.GetValue("EnableVlmInference", false);
        EnableFilterGenerator = s.GetValue("EnableFilterGenerator", false);
        LlmProvider = s["LlmProvider"] ?? "";
        LlmModelName = s["LlmModelName"] ?? "";
        LlmServerUrl = s["LlmServerUrl"] ?? "";
        EmbeddingProvider = s["EmbeddingProvider"] ?? "";
        EmbeddingModelName = s["EmbeddingModelName"] ?? "";
        EmbeddingServerUrl = s["EmbeddingServerUrl"] ?? "";
        RankingModelName = s["RankingModelName"] ?? "";
        RankingServerUrl = s["RankingServerUrl"] ?? "";
        VlmProvider = s["VlmProvider"] ?? "";
        VlmModelName = s["VlmModelName"] ?? "";
        VlmServerUrl = s["VlmServerUrl"] ?? "";
        IsDarkMode = s.GetValue("IsDarkMode", true);
        UseLocalStorage = s.GetValue("UseLocalStorage", false);
    }

    // RAG config
    public double Temperature { get; set; }
    public double TopP { get; set; }
    public int MaxTokens { get; set; }
    public int VdbTopK { get; set; }
    public int RerankerTopK { get; set; }
    public double ConfidenceThreshold { get; set; }

    // Feature toggles
    public bool EnableReranker { get; set; }
    public bool EnableCitations { get; set; }
    public bool EnableQueryRewriting { get; set; }
    public bool EnableGuardrails { get; set; }
    public bool EnableVlmInference { get; set; }
    public bool EnableFilterGenerator { get; set; }
    public bool AgenticMode { get; set; }

    // Models
    public string LlmModelName { get; set; }
    public string EmbeddingModelName { get; set; }
    public string RankingModelName { get; set; }
    public string VlmModelName { get; set; } = "";

    // Providers
    public string LlmProvider { get; set; } = "";
    public string EmbeddingProvider { get; set; } = "";
    public string VlmProvider { get; set; } = "";

    // Endpoints
    public string LlmServerUrl { get; set; }
    public string EmbeddingServerUrl { get; set; }
    public string RankingServerUrl { get; set; }
    public string VlmServerUrl { get; set; } = "";
    public string VdbEndpoint { get; set; } = "";

    // Advanced
    public List<string> StopTokenList { get; set; } = [];

    // UI
    public bool IsDarkMode { get; set; } = true;
    public bool UseLocalStorage { get; set; } = false;

    public event Action? OnChange;

    public void ApplyServerDefaults(ConfigurationResponse config)
    {
        if (config.RagConfiguration is { } rc)
        {
            Temperature = rc.Temperature;
            TopP = rc.TopP;
            MaxTokens = rc.MaxTokens;
            VdbTopK = rc.VdbTopK;
            RerankerTopK = rc.RerankerTopK;
            ConfidenceThreshold = rc.ConfidenceScoreThreshold;
        }
        if (config.FeatureToggles is { } ft)
        {
            EnableReranker = ft.EnableReranker;
            EnableCitations = ft.EnableCitations;
            EnableQueryRewriting = ft.EnableQueryRewriting;
            EnableGuardrails = ft.EnableGuardrails;
            EnableVlmInference = ft.EnableVlmInference;
            EnableFilterGenerator = ft.EnableFilterGenerator;
        }
        if (config.Models is { } m)
        {
            LlmModelName = m.LlmModelName ?? "";
            EmbeddingModelName = m.EmbeddingModelName ?? "";
            RankingModelName = m.RankingModelName ?? "";
            VlmModelName = m.VlmModelName ?? "";
        }
        if (config.Endpoints is { } e)
        {
            LlmServerUrl = e.LlmServerUrl ?? "";
            EmbeddingServerUrl = e.EmbeddingServerUrl ?? "";
            RankingServerUrl = e.RankingServerUrl ?? "";
            VlmServerUrl = e.VlmServerUrl ?? "";
            VdbEndpoint = e.VdbEndpoint ?? "";
        }
        if (config.Providers is { } p)
        {
            LlmProvider = p.LlmProvider ?? "";
            EmbeddingProvider = p.EmbeddingProvider ?? "";
            VlmProvider = p.VlmProvider ?? "";
        }
        OnChange?.Invoke();
    }

    public void NotifyChange() => OnChange?.Invoke();

    public string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            Temperature, TopP, MaxTokens, VdbTopK, RerankerTopK, ConfidenceThreshold,
            EnableReranker, EnableCitations, EnableQueryRewriting, EnableGuardrails,
            EnableVlmInference, EnableFilterGenerator, AgenticMode,
            LlmModelName, EmbeddingModelName, RankingModelName, VlmModelName,
            LlmProvider, EmbeddingProvider, VlmProvider,
            LlmServerUrl, EmbeddingServerUrl, RankingServerUrl, VlmServerUrl, VdbEndpoint,
            StopTokenList, IsDarkMode, UseLocalStorage
        });
    }

    public void ApplyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("Temperature", out var v)) Temperature = v.GetDouble();
            if (r.TryGetProperty("TopP", out v)) TopP = v.GetDouble();
            if (r.TryGetProperty("MaxTokens", out v)) MaxTokens = v.GetInt32();
            if (r.TryGetProperty("VdbTopK", out v)) VdbTopK = v.GetInt32();
            if (r.TryGetProperty("RerankerTopK", out v)) RerankerTopK = v.GetInt32();
            if (r.TryGetProperty("ConfidenceThreshold", out v)) ConfidenceThreshold = v.GetDouble();
            if (r.TryGetProperty("EnableReranker", out v)) EnableReranker = v.GetBoolean();
            if (r.TryGetProperty("EnableCitations", out v)) EnableCitations = v.GetBoolean();
            if (r.TryGetProperty("EnableQueryRewriting", out v)) EnableQueryRewriting = v.GetBoolean();
            if (r.TryGetProperty("EnableGuardrails", out v)) EnableGuardrails = v.GetBoolean();
            if (r.TryGetProperty("EnableVlmInference", out v)) EnableVlmInference = v.GetBoolean();
            if (r.TryGetProperty("EnableFilterGenerator", out v)) EnableFilterGenerator = v.GetBoolean();
            if (r.TryGetProperty("AgenticMode", out v)) AgenticMode = v.GetBoolean();
            if (r.TryGetProperty("LlmModelName", out v)) LlmModelName = v.GetString() ?? LlmModelName;
            if (r.TryGetProperty("EmbeddingModelName", out v)) EmbeddingModelName = v.GetString() ?? EmbeddingModelName;
            if (r.TryGetProperty("RankingModelName", out v)) RankingModelName = v.GetString() ?? RankingModelName;
            if (r.TryGetProperty("VlmModelName", out v)) VlmModelName = v.GetString() ?? VlmModelName;
            if (r.TryGetProperty("LlmProvider", out v)) LlmProvider = v.GetString() ?? LlmProvider;
            if (r.TryGetProperty("EmbeddingProvider", out v)) EmbeddingProvider = v.GetString() ?? EmbeddingProvider;
            if (r.TryGetProperty("VlmProvider", out v)) VlmProvider = v.GetString() ?? VlmProvider;
            if (r.TryGetProperty("LlmServerUrl", out v)) LlmServerUrl = v.GetString() ?? LlmServerUrl;
            if (r.TryGetProperty("EmbeddingServerUrl", out v)) EmbeddingServerUrl = v.GetString() ?? EmbeddingServerUrl;
            if (r.TryGetProperty("RankingServerUrl", out v)) RankingServerUrl = v.GetString() ?? RankingServerUrl;
            if (r.TryGetProperty("VlmServerUrl", out v)) VlmServerUrl = v.GetString() ?? VlmServerUrl;
            if (r.TryGetProperty("VdbEndpoint", out v)) VdbEndpoint = v.GetString() ?? VdbEndpoint;
            if (r.TryGetProperty("StopTokenList", out v) && v.ValueKind == JsonValueKind.Array)
                StopTokenList = v.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
            if (r.TryGetProperty("IsDarkMode", out v)) IsDarkMode = v.GetBoolean();
            if (r.TryGetProperty("UseLocalStorage", out v)) UseLocalStorage = v.GetBoolean();
        }
        catch { }
    }
}
