namespace DotnetRag.Shared.Configuration;

public sealed class RerankerServiceConfiguration
{
    public string PrimaryProvider { get; init; } = GetRequiredEnv("APP_RANKING_PROVIDER");
    public string PrimaryModel { get; init; } = GetEnv("APP_RANKING_MODELNAME", "");
    public string PrimaryEndpoint { get; init; } = GetEnv("APP_RANKING_SERVERURL", "");
    public string? PrimaryApiKey { get; init; } = GetEnvNullable("APP_RANKING_APIKEY")
        ?? GetEnvNullable("NVIDIA_API_KEY")
        ?? GetEnvNullable("OPENAI_API_KEY");

    public string FallbackProvider { get; init; } = GetRequiredEnv("APP_RANKING_FALLBACK_PROVIDER");
    public string FallbackModel { get; init; } = GetEnv("APP_RANKING_FALLBACK_MODELNAME", "");
    public string FallbackEndpoint { get; init; } = GetEnv("APP_RANKING_FALLBACK_SERVERURL", "");
    public string? FallbackApiKey { get; init; } = GetEnvNullable("APP_RANKING_FALLBACK_APIKEY");

    public int TimeoutSeconds { get; init; } = GetRequiredEnvInt("APP_RANKING_TIMEOUT_SECONDS");
    public bool EnableLexicalEmergencyFallback { get; init; } = GetEnvBool("APP_RANKING_LEXICAL_EMERGENCY_FALLBACK", true);
    public string LogLevel { get; init; } = GetEnv("LOGLEVEL", "INFO");

    public static RerankerServiceConfiguration FromEnvironment()
    {
        var config = new RerankerServiceConfiguration();
        config.Validate();
        return config;
    }

    public void Validate()
    {
        ValidateProvider(
            NormalizeProvider(PrimaryProvider),
            PrimaryModel,
            PrimaryEndpoint,
            "APP_RANKING_MODELNAME",
            "APP_RANKING_SERVERURL");

        ValidateProvider(
            NormalizeProvider(FallbackProvider),
            FallbackModel,
            FallbackEndpoint,
            "APP_RANKING_FALLBACK_MODELNAME",
            "APP_RANKING_FALLBACK_SERVERURL");
    }

    private static string GetEnv(string name, string defaultValue) =>
        Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'') ?? defaultValue;

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'');
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"{name} must be set in the environment.");
    }

    private static string? GetEnvNullable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int GetRequiredEnvInt(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"{name} must be set to a valid integer in the environment.");
    }

    private static bool GetEnvBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static void ValidateProvider(
        string provider,
        string model,
        string endpoint,
        string modelEnvName,
        string endpointEnvName)
    {
        if (provider is "" or "auto" or "lexical" or "disabled")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"{modelEnvName} must be set when {provider} reranking is configured.");
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"{endpointEnvName} must be set when {provider} reranking is configured.");
        }
    }

    private static string NormalizeProvider(string provider)
    {
        var value = provider.Trim().ToLowerInvariant();
        return value switch
        {
            "openai-compatible" => "openai",
            "openai_compatible" => "openai",
            _ => value
        };
    }
}
