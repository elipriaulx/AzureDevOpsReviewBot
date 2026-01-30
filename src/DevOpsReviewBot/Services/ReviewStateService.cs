using System.Text.Json;
using DevOpsReviewBot.Models;

namespace DevOpsReviewBot.Services;

public interface IReviewStateService
{
    Task<ReviewState> LoadStateAsync(CancellationToken ct = default);
    Task SaveStateAsync(ReviewState state, CancellationToken ct = default);
}

public class ReviewStateService : IReviewStateService
{
    private readonly string _stateFilePath;
    private readonly ILogger<ReviewStateService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public ReviewStateService(ILogger<ReviewStateService> logger)
    {
        _logger = logger;
        _stateFilePath = Path.Combine(AppContext.BaseDirectory, "review-state.json");
    }

    public async Task<ReviewState> LoadStateAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                _logger.LogInformation("State file not found, creating new state");
                return new ReviewState();
            }

            var json = await File.ReadAllTextAsync(_stateFilePath, ct);
            var state = JsonSerializer.Deserialize<ReviewState>(json, ReadOptions);
            
            if (state != null)
            {
                _logger.LogInformation("Loaded state with {Count} tracked PRs", state.ReviewedCommits.Count);
                return state;
            }

            _logger.LogWarning("Failed to deserialize state, creating new state");
            return new ReviewState();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse state file, creating new state");
            return new ReviewState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading state file");
            return new ReviewState();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveStateAsync(ReviewState state, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            state.LastUpdated = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, WriteOptions);
            
            // Write to temp file first, then move (atomic operation)
            var tempPath = _stateFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _stateFilePath, overwrite: true);
            
            _logger.LogDebug("Saved state with {Count} tracked PRs", state.ReviewedCommits.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save state file");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}
