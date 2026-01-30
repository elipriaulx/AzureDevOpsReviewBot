namespace DevOpsReviewBot.Models;

public class AppConfiguration
{
    public AzureDevOpsConfiguration AzureDevOps { get; set; } = new();
    public ReviewConfiguration Review { get; set; } = new();
    public CursorCliConfiguration CursorCli { get; set; } = new();
}

public class AzureDevOpsConfiguration
{
    public string OrganizationUrl { get; set; } = string.Empty;
    public string PersonalAccessToken { get; set; } = string.Empty;
    public List<RepositoryConfiguration> Repositories { get; set; } = [];
}

public class RepositoryConfiguration
{
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
}

public class ReviewConfiguration
{
    public int PollingIntervalMinutes { get; set; } = 5;
    public string CommentPrefix { get; set; } = "[AutoBot]";
    public int MaxCommentsPerFile { get; set; } = 5;
    public int MaxFileSizeKb { get; set; } = 500;
    public List<string> ExcludePatterns { get; set; } = [
        "*.generated.cs",
        "*.Designer.cs",
        "*.g.cs",
        "package-lock.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "*.min.js",
        "*.min.css"
    ];
}

public class CursorCliConfiguration
{
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 3;
    public string? ModelName { get; set; }
    public string AgentCommand { get; set; } = "agent";
}
