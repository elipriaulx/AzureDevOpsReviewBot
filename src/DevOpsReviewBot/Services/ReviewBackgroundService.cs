using System.Text.RegularExpressions;
using DevOpsReviewBot.Models;
using Microsoft.Extensions.Options;

namespace DevOpsReviewBot.Services;

public class ReviewBackgroundService : BackgroundService
{
    private readonly IAzureDevOpsService _azureDevOpsService;
    private readonly ICursorCliService _cursorCliService;
    private readonly IReviewStateService _reviewStateService;
    private readonly AppConfiguration _config;
    private readonly ILogger<ReviewBackgroundService> _logger;
    private readonly List<Regex> _excludePatterns;

    public ReviewBackgroundService(
        IAzureDevOpsService azureDevOpsService,
        ICursorCliService cursorCliService,
        IReviewStateService reviewStateService,
        IOptions<AppConfiguration> config,
        ILogger<ReviewBackgroundService> logger)
    {
        _azureDevOpsService = azureDevOpsService;
        _cursorCliService = cursorCliService;
        _reviewStateService = reviewStateService;
        _config = config.Value;
        _logger = logger;
        
        // Compile exclude patterns to regex for efficient matching
        _excludePatterns = _config.Review.ExcludePatterns
            .Select(ConvertGlobToRegex)
            .ToList();
    }
    
    private static Regex ConvertGlobToRegex(string glob)
    {
        // Convert glob pattern to regex
        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoBot review service starting");
        
        var interval = TimeSpan.FromMinutes(_config.Review.PollingIntervalMinutes);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunReviewCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during review cycle");
            }

            _logger.LogInformation("Next review cycle in {Minutes} minutes", _config.Review.PollingIntervalMinutes);
            
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("AutoBot review service stopping");
    }

    private async Task RunReviewCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting review cycle");
        
        var state = await _reviewStateService.LoadStateAsync(ct);
        var activePrKeys = new List<string>();

        foreach (var repo in _config.AzureDevOps.Repositories)
        {
            try
            {
                await ProcessRepositoryAsync(repo, state, activePrKeys, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing repository {Project}/{Repository}", repo.Project, repo.Repository);
            }
        }

        // Cleanup state for closed PRs
        state.CleanupClosedPullRequests(activePrKeys);
        await _reviewStateService.SaveStateAsync(state, ct);
        
        _logger.LogInformation("Review cycle completed");
    }

    private async Task ProcessRepositoryAsync(RepositoryConfiguration repo, ReviewState state, List<string> activePrKeys, CancellationToken ct)
    {
        _logger.LogInformation("Checking {Project}/{Repository} for open PRs", repo.Project, repo.Repository);
        
        var pullRequests = await _azureDevOpsService.GetOpenPullRequestsAsync(repo.Project, repo.Repository, ct);
        _logger.LogInformation("Found {Count} open PRs in {Project}/{Repository}", pullRequests.Count, repo.Project, repo.Repository);

        foreach (var pr in pullRequests)
        {
            var prKey = state.GetPullRequestKey(pr.Project, pr.Repository, pr.PullRequestId);
            activePrKeys.Add(prKey);

            try
            {
                await ProcessPullRequestAsync(pr, state, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing PR #{PullRequestId} in {Project}/{Repository}", 
                    pr.PullRequestId, pr.Project, pr.Repository);
            }
        }
    }

    private async Task ProcessPullRequestAsync(PullRequestInfo pr, ReviewState state, CancellationToken ct)
    {
        // Get iterations to find new commits
        var iterations = await _azureDevOpsService.GetPullRequestIterationsAsync(
            pr.Project, pr.RepositoryId, pr.PullRequestId, ct);

        if (iterations.Count == 0)
        {
            _logger.LogDebug("PR #{PullRequestId} has no iterations", pr.PullRequestId);
            return;
        }

        // Get the latest iteration
        var latestIteration = iterations.OrderByDescending(i => i.Id).First();
        
        // Check if we've already reviewed this commit
        if (state.HasReviewedCommit(pr.Project, pr.Repository, pr.PullRequestId, latestIteration.SourceRefCommit))
        {
            _logger.LogDebug("PR #{PullRequestId} commit {CommitId} already reviewed", 
                pr.PullRequestId, latestIteration.SourceRefCommit);
            return;
        }

        _logger.LogInformation("Reviewing PR #{PullRequestId}: {Title} (commit {CommitId})", 
            pr.PullRequestId, pr.Title, latestIteration.SourceRefCommit);

        // Get changed files
        var changes = await _azureDevOpsService.GetIterationChangesAsync(
            pr.Project, pr.RepositoryId, pr.PullRequestId, latestIteration.Id, ct);

        // Filter to only code files, exclude deletions, and apply exclude patterns
        var filesToReview = changes
            .Where(c => !c.ChangeType.Equals("delete", StringComparison.OrdinalIgnoreCase))
            .Where(c => IsReviewableFile(c.Path))
            .Where(c => !IsExcludedByPattern(c.Path))
            .ToList();

        _logger.LogInformation("Found {Count} files to review in PR #{PullRequestId} (after filtering)", 
            filesToReview.Count, pr.PullRequestId);

        if (filesToReview.Count == 0)
        {
            state.MarkCommitReviewed(pr.Project, pr.Repository, pr.PullRequestId, latestIteration.SourceRefCommit);
            return;
        }

        // Fetch file contents
        var maxFileSizeBytes = _config.Review.MaxFileSizeKb * 1024;
        foreach (var file in filesToReview)
        {
            file.Content = await _azureDevOpsService.GetFileContentAsync(
                pr.Project, pr.RepositoryId, file.Path, latestIteration.SourceRefCommit, ct);
        }

        // Remove files without content and files that exceed size limit
        var originalCount = filesToReview.Count;
        filesToReview = filesToReview
            .Where(f => !string.IsNullOrEmpty(f.Content))
            .Where(f => f.Content.Length <= maxFileSizeBytes)
            .ToList();
        
        var skippedDueToSize = originalCount - filesToReview.Count;
        if (skippedDueToSize > 0)
        {
            _logger.LogInformation("Skipped {Count} files exceeding {MaxSize}KB size limit", 
                skippedDueToSize, _config.Review.MaxFileSizeKb);
        }

        if (filesToReview.Count == 0)
        {
            state.MarkCommitReviewed(pr.Project, pr.Repository, pr.PullRequestId, latestIteration.SourceRefCommit);
            return;
        }

        // Run AI review
        var reviewResult = await _cursorCliService.ReviewFilesAsync(filesToReview, ct);

        if (!reviewResult.Success)
        {
            _logger.LogWarning("Cursor CLI review failed for PR #{PullRequestId}: {Error}", 
                pr.PullRequestId, reviewResult.Error);
            // Don't mark as reviewed so we retry next cycle
            return;
        }

        // Log review results
        var totalComments = reviewResult.Files.Sum(f => f.Comments.Count);
        var filesWithIssues = reviewResult.Files.Count;
        _logger.LogInformation("PR #{PullRequestId} review results: {FilesWithIssues} files with issues, {TotalComments} total comments to post",
            pr.PullRequestId, filesWithIssues, totalComments);

        if (filesWithIssues == 0)
        {
            _logger.LogInformation("PR #{PullRequestId}: No issues found by reviewer. Overall summary: {Summary}",
                pr.PullRequestId, reviewResult.OverallSummary ?? "none");
        }

        // Post comments
        await PostReviewCommentsAsync(pr, reviewResult, ct);

        // Mark as reviewed
        state.MarkCommitReviewed(pr.Project, pr.Repository, pr.PullRequestId, latestIteration.SourceRefCommit);
        
        _logger.LogInformation("Completed review of PR #{PullRequestId}", pr.PullRequestId);
    }

    private async Task PostReviewCommentsAsync(PullRequestInfo pr, CursorReviewResponse reviewResult, CancellationToken ct)
    {
        foreach (var fileResult in reviewResult.Files)
        {
            // If there's a summary (too many issues), post that instead of individual comments
            if (!string.IsNullOrEmpty(fileResult.Summary))
            {
                await _azureDevOpsService.PostCommentThreadAsync(
                    pr.Project, pr.RepositoryId, pr.PullRequestId,
                    fileResult.FilePath, lineNumber: null,
                    fileResult.Summary, ct);
                continue;
            }

            // Post individual comments, limited to max per file
            var commentsToPost = fileResult.Comments
                .Take(_config.Review.MaxCommentsPerFile)
                .ToList();

            // If we're truncating, add a summary comment
            if (fileResult.Comments.Count > _config.Review.MaxCommentsPerFile)
            {
                var summary = $"This file has {fileResult.Comments.Count} suggestions. Showing top {_config.Review.MaxCommentsPerFile}. " +
                              "Consider reviewing the full file for additional improvements.";
                
                await _azureDevOpsService.PostCommentThreadAsync(
                    pr.Project, pr.RepositoryId, pr.PullRequestId,
                    fileResult.FilePath, lineNumber: null,
                    summary, ct);
            }

            foreach (var comment in commentsToPost)
            {
                await _azureDevOpsService.PostCommentThreadAsync(
                    pr.Project, pr.RepositoryId, pr.PullRequestId,
                    comment.FilePath, comment.LineNumber,
                    comment.Comment, ct);
            }
        }
    }

    private static bool IsReviewableFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var reviewableExtensions = new HashSet<string>
        {
            ".cs", ".fs", ".vb",           // .NET
            ".js", ".ts", ".jsx", ".tsx",  // JavaScript/TypeScript
            ".py",                          // Python
            ".java", ".kt",                // JVM
            ".go",                          // Go
            ".rs",                          // Rust
            ".cpp", ".c", ".h", ".hpp",    // C/C++
            ".rb",                          // Ruby
            ".php",                         // PHP
            ".swift",                       // Swift
            ".sql",                         // SQL
            ".yaml", ".yml",               // YAML
            ".json",                        // JSON
            ".xml",                         // XML
        };

        return reviewableExtensions.Contains(extension);
    }
    
    private bool IsExcludedByPattern(string path)
    {
        var fileName = Path.GetFileName(path);
        
        // Check against each exclude pattern
        foreach (var pattern in _excludePatterns)
        {
            if (pattern.IsMatch(fileName) || pattern.IsMatch(path))
            {
                _logger.LogDebug("File {Path} excluded by pattern", path);
                return true;
            }
        }
        
        return false;
    }
}
