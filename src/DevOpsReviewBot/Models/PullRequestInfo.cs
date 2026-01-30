namespace DevOpsReviewBot.Models;

public class PullRequestInfo
{
    public int PullRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string LastMergeSourceCommit { get; set; } = string.Empty;
    public List<PullRequestFile> ChangedFiles { get; set; } = [];
}

public class PullRequestFile
{
    public string Path { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public class PullRequestIteration
{
    public int Id { get; set; }
    public string SourceRefCommit { get; set; } = string.Empty;
}
