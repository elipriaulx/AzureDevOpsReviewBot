namespace DevOpsReviewBot.Models;

public class ReviewState
{
    public Dictionary<string, List<string>> ReviewedCommits { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public string GetPullRequestKey(string project, string repository, int pullRequestId)
        => $"{project}/{repository}/{pullRequestId}";

    public bool HasReviewedCommit(string project, string repository, int pullRequestId, string commitId)
    {
        var key = GetPullRequestKey(project, repository, pullRequestId);
        return ReviewedCommits.TryGetValue(key, out var commits) && commits.Contains(commitId);
    }

    public void MarkCommitReviewed(string project, string repository, int pullRequestId, string commitId)
    {
        var key = GetPullRequestKey(project, repository, pullRequestId);
        if (!ReviewedCommits.ContainsKey(key))
        {
            ReviewedCommits[key] = [];
        }
        if (!ReviewedCommits[key].Contains(commitId))
        {
            ReviewedCommits[key].Add(commitId);
        }
        LastUpdated = DateTime.UtcNow;
    }

    public void CleanupClosedPullRequests(IEnumerable<string> activePrKeys)
    {
        var keysToRemove = ReviewedCommits.Keys
            .Where(k => !activePrKeys.Contains(k))
            .ToList();
        
        foreach (var key in keysToRemove)
        {
            ReviewedCommits.Remove(key);
        }
    }
}
