using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevOpsReviewBot.Models;
using Microsoft.Extensions.Options;

namespace DevOpsReviewBot.Services;

public interface IAzureDevOpsService
{
    Task<List<PullRequestInfo>> GetOpenPullRequestsAsync(string project, string repository, CancellationToken ct = default);
    Task<List<PullRequestIteration>> GetPullRequestIterationsAsync(string project, string repositoryId, int pullRequestId, CancellationToken ct = default);
    Task<List<PullRequestFile>> GetIterationChangesAsync(string project, string repositoryId, int pullRequestId, int iterationId, CancellationToken ct = default);
    Task<string> GetFileContentAsync(string project, string repositoryId, string path, string commitId, CancellationToken ct = default);
    Task PostCommentThreadAsync(string project, string repositoryId, int pullRequestId, string filePath, int? lineNumber, string comment, CancellationToken ct = default);
}

public class AzureDevOpsService : IAzureDevOpsService
{
    private readonly HttpClient _httpClient;
    private readonly AppConfiguration _config;
    private readonly ILogger<AzureDevOpsService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AzureDevOpsService(HttpClient httpClient, IOptions<AppConfiguration> config, ILogger<AzureDevOpsService> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_config.AzureDevOps.PersonalAccessToken))
        {
            _logger.LogWarning("Azure DevOps PAT is not configured. Please set AzureDevOps:PersonalAccessToken in appsettings.json");
        }

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_config.AzureDevOps.PersonalAccessToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.BaseAddress = new Uri(_config.AzureDevOps.OrganizationUrl.TrimEnd('/') + "/");
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        var response = await _httpClient.GetAsync(url, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => 
                    "Authentication failed. Check your PAT token has the required permissions (Code: Read, Pull Request Threads: Read & Write)",
                System.Net.HttpStatusCode.NotFound => 
                    "Resource not found. Verify the organization URL, project name, and repository name are correct",
                System.Net.HttpStatusCode.Forbidden =>
                    "Access denied. Your PAT may not have sufficient permissions for this operation",
                _ => $"API request failed with status {response.StatusCode}"
            };
            
            _logger.LogError("Azure DevOps API error: {Message}. URL: {Url}, Response: {Response}", 
                errorMessage, url, content.Length > 500 ? content[..500] + "..." : content);
            
            throw new HttpRequestException(errorMessage);
        }

        // Check if response looks like HTML (error page) instead of JSON
        if (content.TrimStart().StartsWith('<'))
        {
            _logger.LogError("Azure DevOps returned HTML instead of JSON. This usually means authentication failed or the URL is incorrect. URL: {Url}", url);
            throw new HttpRequestException("Azure DevOps returned an HTML error page. Check your PAT and organization URL configuration.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions) 
                ?? throw new JsonException("Deserialized to null");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Azure DevOps response. URL: {Url}, Content: {Content}", 
                url, content.Length > 500 ? content[..500] + "..." : content);
            throw;
        }
    }

    public async Task<List<PullRequestInfo>> GetOpenPullRequestsAsync(string project, string repository, CancellationToken ct = default)
    {
        var url = $"{project}/_apis/git/repositories/{repository}/pullrequests?searchCriteria.status=active&api-version=7.1";
        
        var result = await GetJsonAsync<AzureDevOpsListResponse<AzureDevOpsPullRequest>>(url, ct);
        
        return result.Value?.Select(pr => new PullRequestInfo
        {
            PullRequestId = pr.PullRequestId,
            Title = pr.Title ?? string.Empty,
            SourceBranch = pr.SourceRefName ?? string.Empty,
            TargetBranch = pr.TargetRefName ?? string.Empty,
            Project = project,
            Repository = repository,
            RepositoryId = pr.Repository?.Id ?? string.Empty,
            LastMergeSourceCommit = pr.LastMergeSourceCommit?.CommitId ?? string.Empty
        }).ToList() ?? [];
    }

    public async Task<List<PullRequestIteration>> GetPullRequestIterationsAsync(string project, string repositoryId, int pullRequestId, CancellationToken ct = default)
    {
        var url = $"{project}/_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/iterations?api-version=7.1";
        
        var result = await GetJsonAsync<AzureDevOpsListResponse<AzureDevOpsIteration>>(url, ct);
        
        return result.Value?.Select(i => new PullRequestIteration
        {
            Id = i.Id,
            SourceRefCommit = i.SourceRefCommit?.CommitId ?? string.Empty
        }).ToList() ?? [];
    }

    public async Task<List<PullRequestFile>> GetIterationChangesAsync(string project, string repositoryId, int pullRequestId, int iterationId, CancellationToken ct = default)
    {
        var url = $"{project}/_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/iterations/{iterationId}/changes?api-version=7.1";
        
        var result = await GetJsonAsync<AzureDevOpsChangesResponse>(url, ct);
        
        return result.ChangeEntries?.Select(c => new PullRequestFile
        {
            Path = c.Item?.Path ?? string.Empty,
            ChangeType = c.ChangeType ?? string.Empty
        }).ToList() ?? [];
    }

    public async Task<string> GetFileContentAsync(string project, string repositoryId, string path, string commitId, CancellationToken ct = default)
    {
        // Use $format=text to get raw file content instead of metadata
        var url = $"{project}/_apis/git/repositories/{repositoryId}/items?path={Uri.EscapeDataString(path)}&versionType=commit&version={commitId}&$format=text&api-version=7.1";
        
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to get file content for {Path} at commit {CommitId}", path, commitId);
            return string.Empty;
        }
    }

    public async Task PostCommentThreadAsync(string project, string repositoryId, int pullRequestId, string filePath, int? lineNumber, string comment, CancellationToken ct = default)
    {
        var url = $"{project}/_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/threads?api-version=7.1";

        var prefixedComment = $"{_config.Review.CommentPrefix} {comment}";
        
        var thread = new AzureDevOpsCommentThread
        {
            Comments =
            [
                new AzureDevOpsComment { Content = prefixedComment, CommentType = 1 }
            ],
            Status = 1 // Active but not blocking
        };

        if (!string.IsNullOrEmpty(filePath))
        {
            // Normalize file path - Azure DevOps expects paths to start with /
            var normalizedPath = filePath.StartsWith('/') ? filePath : "/" + filePath;
            
            thread.ThreadContext = new ThreadContext
            {
                FilePath = normalizedPath,
                RightFileStart = lineNumber.HasValue ? new FilePosition { Line = lineNumber.Value, Offset = 1 } : null,
                RightFileEnd = lineNumber.HasValue ? new FilePosition { Line = lineNumber.Value, Offset = 1 } : null
            };
        }

        var json = JsonSerializer.Serialize(thread, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var response = await _httpClient.PostAsync(url, content, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Failed to post comment: {StatusCode} - {Error}", response.StatusCode, error);
        }
    }

    // Azure DevOps API response types
    private class AzureDevOpsListResponse<T>
    {
        public List<T>? Value { get; set; }
        public int Count { get; set; }
    }

    private class AzureDevOpsPullRequest
    {
        public int PullRequestId { get; set; }
        public string? Title { get; set; }
        public string? SourceRefName { get; set; }
        public string? TargetRefName { get; set; }
        public AzureDevOpsRepository? Repository { get; set; }
        public AzureDevOpsCommitRef? LastMergeSourceCommit { get; set; }
    }

    private class AzureDevOpsRepository
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private class AzureDevOpsCommitRef
    {
        public string? CommitId { get; set; }
    }

    private class AzureDevOpsIteration
    {
        public int Id { get; set; }
        public AzureDevOpsCommitRef? SourceRefCommit { get; set; }
    }

    private class AzureDevOpsChangesResponse
    {
        public List<AzureDevOpsChangeEntry>? ChangeEntries { get; set; }
    }

    private class AzureDevOpsChangeEntry
    {
        public string? ChangeType { get; set; }
        public AzureDevOpsItem? Item { get; set; }
    }

    private class AzureDevOpsItem
    {
        public string? Path { get; set; }
    }

    private class AzureDevOpsCommentThread
    {
        public List<AzureDevOpsComment>? Comments { get; set; }
        public int Status { get; set; }
        public ThreadContext? ThreadContext { get; set; }
    }

    private class AzureDevOpsComment
    {
        public string? Content { get; set; }
        public int CommentType { get; set; }
    }

    private class ThreadContext
    {
        public string? FilePath { get; set; }
        public FilePosition? RightFileStart { get; set; }
        public FilePosition? RightFileEnd { get; set; }
    }

    private class FilePosition
    {
        public int Line { get; set; }
        public int Offset { get; set; }
    }
}
