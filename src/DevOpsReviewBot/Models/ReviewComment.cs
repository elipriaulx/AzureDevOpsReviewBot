using System.Text.Json.Serialization;

namespace DevOpsReviewBot.Models;

public class ReviewComment
{
    public string FilePath { get; set; } = string.Empty;
    public int? LineNumber { get; set; }
    public string Comment { get; set; } = string.Empty;
    
    [JsonConverter(typeof(JsonStringEnumConverter<ReviewCommentSeverity>))]
    public ReviewCommentSeverity Severity { get; set; } = ReviewCommentSeverity.Suggestion;
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewCommentSeverity>))]
public enum ReviewCommentSeverity
{
    Suggestion,
    Warning,
    Issue
}

public class FileReviewResult
{
    public string FilePath { get; set; } = string.Empty;
    public List<ReviewComment> Comments { get; set; } = [];
    public string? Summary { get; set; }
}

public class CursorReviewResponse
{
    public List<FileReviewResult> Files { get; set; } = [];
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OverallSummary { get; set; }
}
