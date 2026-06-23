namespace DotnetRag.Shared.Configuration;

public sealed class RerankerServiceConfiguration
{
    public string PrimaryProvider { get; init; } = GetEnv("APP_RANKING_PROVIDER", "auto");
    public string PrimaryModel { get; init; } = GetEnv("APP_RANKING_MODELNAME", "");
    public string PrimaryEndpoint { get; init; } = GetEnv("APP_RANKING_SERVERURL", "");
    public string? PrimaryApiKey { get; init; } = GetEnvNullable("APP_RANKING_APIKEY")
        ?? GetEnvNullable("NVIDIA_API_KEY")
        ?? GetEnvNullable("OPENAI_API_KEY");

    public string FallbackProvider { get; init; } = GetEnv("APP_RANKING_FALLBACK_PROVIDER", "lexical");
    public string FallbackModel { get; init; } = GetEnv("APP_RANKING_FALLBACK_MODELNAME", "");
    public string FallbackEndpoint { get; init; } = GetEnv("APP_RANKING_FALLBACK_SERVERURL", "");
    public string? FallbackApiKey { get; init; } = GetEnvNullable("APP_RANKING_FALLBACK_APIKEY");

    public int TimeoutSeconds { get; init; } = GetEnvInt("APP_RANKING_TIMEOUT_SECONDS", 20);
    public bool EnableLexicalEmergencyFallback { get; init; } = GetEnvBool("APP_RANKING_LEXICAL_EMERGENCY_FALLBACK", true);
    public string LogLevel { get; init; } = GetEnv("LOGLEVEL", "INFO");

    public static RerankerServiceConfiguration FromEnvironment() => new();

    private static string GetEnv(string name, string defaultValue) =>
        Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'') ?? defaultValue;

    private static string? GetEnvNullable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim('\'');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int GetEnvInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static bool GetEnvBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
