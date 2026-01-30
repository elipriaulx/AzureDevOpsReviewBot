using DevOpsReviewBot.Models;
using DevOpsReviewBot.Services;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration
builder.Services.Configure<AppConfiguration>(config =>
{
    builder.Configuration.GetSection("AzureDevOps").Bind(config.AzureDevOps);
    builder.Configuration.GetSection("Review").Bind(config.Review);
    builder.Configuration.GetSection("CursorCli").Bind(config.CursorCli);
});

// Register services
builder.Services.AddSingleton<IReviewStateService, ReviewStateService>();
builder.Services.AddSingleton<ICursorCliService, CursorCliService>();

// Configure HttpClient for Azure DevOps
builder.Services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>(client =>
{
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register background service
builder.Services.AddHostedService<ReviewBackgroundService>();

var host = builder.Build();

// Validate configuration at startup
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var config = builder.Configuration;

var orgUrl = config.GetValue<string>("AzureDevOps:OrganizationUrl");
var pat = config.GetValue<string>("AzureDevOps:PersonalAccessToken");
var repos = config.GetSection("AzureDevOps:Repositories").GetChildren().ToList();

var hasErrors = false;

if (string.IsNullOrWhiteSpace(orgUrl) || orgUrl == "https://dev.azure.com/your-organization")
{
    logger.LogError("Configuration error: AzureDevOps:OrganizationUrl is not set. Please update appsettings.json");
    hasErrors = true;
}

if (string.IsNullOrWhiteSpace(pat))
{
    logger.LogError("Configuration error: AzureDevOps:PersonalAccessToken is empty. Please set your PAT in appsettings.json");
    hasErrors = true;
}

if (repos.Count == 0)
{
    logger.LogError("Configuration error: No repositories configured. Add at least one repository to AzureDevOps:Repositories");
    hasErrors = true;
}
else
{
    foreach (var repo in repos)
    {
        var project = repo.GetValue<string>("Project");
        var repository = repo.GetValue<string>("Repository");
        if (project == "YourProject" || repository == "YourRepository")
        {
            logger.LogWarning("Configuration warning: Repository appears to use placeholder values ({Project}/{Repository}). Update appsettings.json with actual values", project, repository);
        }
    }
}

if (hasErrors)
{
    logger.LogError("Exiting due to configuration errors. Please fix the issues above and restart.");
    return;
}

logger.LogInformation("AutoBot starting - Organization: {Org}, Polling interval: {Interval} minutes", 
    orgUrl, config.GetValue<int>("Review:PollingIntervalMinutes"));

host.Run();
