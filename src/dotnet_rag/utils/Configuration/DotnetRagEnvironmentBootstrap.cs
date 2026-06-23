namespace DotnetRag.Shared.Configuration;

public static class DotnetRagEnvironmentBootstrap
{
    private const string EnvFileOverrideVar = "DOTNET_RAG_ENV_FILE";
    private const string SkipBootstrapVar = "DOTNET_RAG_SKIP_ENV_BOOTSTRAP";

    public static void LoadSharedLocalEnvironment()
    {
        if (IsTruthy(Environment.GetEnvironmentVariable(SkipBootstrapVar)))
        {
            return;
        }

        var envFilePath = ResolveEnvFilePath();
        if (string.IsNullOrWhiteSpace(envFilePath) || !File.Exists(envFilePath))
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(envFilePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line["export ".Length..].Trim();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim().Trim('"').Trim('\'');
            var existing = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(existing))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        Environment.SetEnvironmentVariable("DOTNET_RAG_ENV_BOOTSTRAPPED", "true");
        Environment.SetEnvironmentVariable("DOTNET_RAG_ENV_BOOTSTRAP_FILE", envFilePath);
    }

    private static string? ResolveEnvFilePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(EnvFileOverrideVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var candidateRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in candidateRoots)
        {
            var path = FindByWalkingUp(root);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindByWalkingUp(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "deploy", "compose", "dotnet-local.env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase)
         || value.Equals("true", StringComparison.OrdinalIgnoreCase)
         || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
