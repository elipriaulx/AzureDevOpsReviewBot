using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevOpsReviewBot.Models;
using Microsoft.Extensions.Options;

namespace DevOpsReviewBot.Services;

public interface ICursorCliService
{
    Task<CursorReviewResponse> ReviewFilesAsync(List<PullRequestFile> files, CancellationToken ct = default);
}

public class CursorCliService : ICursorCliService
{
    private readonly ILogger<CursorCliService> _logger;
    private readonly AppConfiguration _config;
    private readonly string _agentPath;

    private const string ReviewPromptTemplate = """
        Review all code files in this workspace for a pull request. Focus on:
        - Logic errors and bugs
        - Security vulnerabilities  
        - Performance issues
        - SOLID principle violations
        - Resource leaks (undisposed objects, unclosed connections)
        - Code clarity issues

        Guidelines:
        - Only report significant issues that improve code quality
        - Respect developer creativity - avoid nitpicking style preferences
        - If a file has many issues, provide a summary instead of listing all
        - Be concise and actionable

        Respond with ONLY valid JSON in this exact format (no markdown, no extra text):
        {
            "files": [
                {
                    "filePath": "/path/to/file.cs",
                    "comments": [
                        {
                            "lineNumber": 10,
                            "comment": "Consider using...",
                            "severity": "suggestion"
                        }
                    ],
                    "summary": null
                }
            ],
            "overallSummary": "Brief summary of the review"
        }

        Severity levels: "suggestion", "warning", "issue"
        Use "summary" field (instead of comments array) if there are more than 5 issues in a file.
        If no issues found, return {"files": [], "overallSummary": "No significant issues found."}
        """;

    public CursorCliService(ILogger<CursorCliService> logger, IOptions<AppConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
        _agentPath = ResolveAgentPath();
        _logger.LogInformation("Cursor CLI agent path resolved to: {Path}", _agentPath);
    }

    private string ResolveAgentPath()
    {
        // If a full path is configured, use it directly
        var configuredCommand = _config.CursorCli.AgentCommand;
        if (Path.IsPathFullyQualified(configuredCommand) && File.Exists(configuredCommand))
        {
            return configuredCommand;
        }

        // Common installation locations for Cursor CLI on Windows
        var possiblePaths = new List<string>
        {
            // Official Cursor Agent CLI installation location
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cursor-agent", "agent.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cursor-agent", "agent.exe"),
            // User's local app data (typical npm global install location)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor-cli", "agent.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "agent.exe"),
            // npm global bin
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "agent.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "agent"),
            // User profile bin
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "bin", "agent.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "bin", "agent"),
            // Cursor installation directory
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "resources", "app", "bin", "agent.exe"),
        };

        // Try to find agent in known locations
        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                _logger.LogDebug("Found agent CLI at: {Path}", path);
                return path;
            }
        }

        // Try to resolve via PATH using where.exe (Windows)
        try
        {
            var whereResult = RunWhereCommand(configuredCommand);
            if (!string.IsNullOrEmpty(whereResult))
            {
                _logger.LogDebug("Found agent CLI via PATH: {Path}", whereResult);
                return whereResult;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve agent path via where.exe");
        }

        // Fall back to configured command (may fail at runtime if not in PATH)
        _logger.LogWarning("Could not resolve full path to agent CLI, using configured command: {Command}. " +
                          "If this fails, set CursorCli:AgentCommand to the full path in appsettings.json", 
                          configuredCommand);
        return configuredCommand;
    }

    private static string? RunWhereCommand(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                // Return the first line (first match)
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    public async Task<CursorReviewResponse> ReviewFilesAsync(List<PullRequestFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return new CursorReviewResponse { Success = true, Files = [] };
        }

        var tempDirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDirectoryPath);

        try
        {
            // Write files to temp directory preserving structure
            var writtenFiles = new List<string>();
            foreach (var file in files.Where(f => !string.IsNullOrEmpty(f.Content)))
            {
                var relativePath = file.Path.TrimStart('/');
                var filePath = Path.Combine(tempDirectoryPath, relativePath);
                var directory = Path.GetDirectoryName(filePath);
                
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                await File.WriteAllTextAsync(filePath, file.Content, ct);
                writtenFiles.Add(relativePath);
            }

            if (writtenFiles.Count == 0)
            {
                return new CursorReviewResponse { Success = true, Files = [] };
            }

            _logger.LogInformation("Reviewing {Count} files in workspace: {Path}", writtenFiles.Count, tempDirectoryPath);

            // Write the detailed prompt to a file in the workspace to avoid command line length limits
            var promptFilePath = Path.Combine(tempDirectoryPath, ".autobot-instructions.md");
            await File.WriteAllTextAsync(promptFilePath, ReviewPromptTemplate, ct);

            // Use a short prompt that tells the agent to read the instructions file
            var shortPrompt = "Read .autobot-instructions.md for detailed instructions, then review all code files in this workspace and respond according to those instructions.";

            // Invoke CLI with retry logic
            var result = await InvokeWithRetryAsync(tempDirectoryPath, shortPrompt, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Cursor CLI review");
            return new CursorReviewResponse
            {
                Success = false,
                Error = ex.Message,
                Files = []
            };
        }
        finally
        {
            // Cleanup temp directory
            CleanupTempDirectory(tempDirectoryPath);
        }
    }

    private async Task<CursorReviewResponse> InvokeWithRetryAsync(string workspacePath, string prompt, CancellationToken ct)
    {
        var maxRetries = _config.CursorCli.MaxRetries;
        var lastException = default(Exception);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var result = await InvokeCursorCliAsync(workspacePath, prompt, ct);
                
                if (result.Success)
                {
                    return result;
                }

                // Check if the error is retryable
                if (!IsRetryableError(result.Error))
                {
                    return result;
                }

                _logger.LogWarning("CLI attempt {Attempt}/{MaxRetries} failed: {Error}", 
                    attempt, maxRetries, result.Error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "CLI attempt {Attempt}/{MaxRetries} threw exception", 
                    attempt, maxRetries);
            }

            if (attempt < maxRetries)
            {
                // Exponential backoff: 2s, 4s, 8s...
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogInformation("Waiting {Seconds}s before retry...", delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        return new CursorReviewResponse
        {
            Success = false,
            Error = lastException?.Message ?? "All retry attempts failed",
            Files = []
        };
    }

    private static bool IsRetryableError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return true;

        // Network/transient errors are retryable
        var retryablePatterns = new[]
        {
            "timeout",
            "network",
            "connection",
            "ECONNRESET",
            "ETIMEDOUT",
            "rate limit",
            "503",
            "502",
            "504"
        };

        return retryablePatterns.Any(p => error.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CursorReviewResponse> InvokeCursorCliAsync(string workspacePath, string prompt, CancellationToken ct)
    {
        // Build arguments for agent CLI
        var args = new StringBuilder();
        args.Append("-p ");
        args.Append(EscapeArgument(prompt));
        args.Append(" --output-format json");
        args.Append($" --workspace \"{workspacePath}\"");
        
        // Add model if configured
        if (!string.IsNullOrEmpty(_config.CursorCli.ModelName))
        {
            args.Append($" --model {_config.CursorCli.ModelName}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _agentPath,
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspacePath,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _logger.LogDebug("Executing: {Command} {Args}", startInfo.FileName, startInfo.Arguments);

        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeout = TimeSpan.FromSeconds(_config.CursorCli.TimeoutSeconds);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("CLI process timed out after {Seconds} seconds, killing process", 
                    _config.CursorCli.TimeoutSeconds);
                
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill timed out process");
                }

                return new CursorReviewResponse
                {
                    Success = false,
                    Error = $"CLI timed out after {_config.CursorCli.TimeoutSeconds} seconds",
                    Files = []
                };
            }

            var stdout = outputBuilder.ToString();
            var stderr = errorBuilder.ToString();

            _logger.LogDebug("CLI exit code: {ExitCode}, stdout length: {StdoutLen}, stderr length: {StderrLen}",
                process.ExitCode, stdout.Length, stderr.Length);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Cursor CLI exited with code {ExitCode}. Stderr: {Stderr}",
                    process.ExitCode, stderr);

                // Try to parse stdout anyway - sometimes valid JSON is returned with non-zero exit
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    var parsed = TryParseResponse(stdout);
                    if (parsed != null && parsed.Files.Count > 0)
                    {
                        return parsed;
                    }
                }

                return new CursorReviewResponse
                {
                    Success = false,
                    Error = $"CLI exited with code {process.ExitCode}: {stderr}",
                    Files = []
                };
            }

            // Parse the JSON response from stdout
            var response = TryParseResponse(stdout);
            if (response != null)
            {
                response.Success = true;
                var totalComments = response.Files.Sum(f => f.Comments.Count);
                var filesWithSummary = response.Files.Count(f => !string.IsNullOrEmpty(f.Summary));
                _logger.LogInformation("CLI review complete: {FileCount} files with issues, {CommentCount} comments, {SummaryCount} summaries. Overall: {Summary}", 
                    response.Files.Count, totalComments, filesWithSummary, response.OverallSummary ?? "none");
                
                if (response.Files.Count == 0)
                {
                    _logger.LogDebug("Raw CLI output (no issues found): {Output}", stdout);
                }
                
                return response;
            }

            _logger.LogWarning("Failed to parse CLI response. Raw output: {Output}", 
                stdout.Length > 2000 ? stdout[..2000] + "..." : stdout);

            return new CursorReviewResponse
            {
                Success = true,
                Files = [],
                OverallSummary = "Review completed but response could not be parsed"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to invoke Cursor CLI");
            return new CursorReviewResponse
            {
                Success = false,
                Error = $"Failed to invoke CLI: {ex.Message}",
                Files = []
            };
        }
    }

    private CursorReviewResponse? TryParseResponse(string output)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // First, try to parse as the CLI wrapper format: {"type":"result","result":"..."}
            var wrapperResponse = TryParseCliWrapper(output, options);
            if (wrapperResponse != null)
            {
                return wrapperResponse;
            }

            // Fall back to direct parsing
            var jsonStart = output.IndexOf('{');
            var jsonEnd = output.LastIndexOf('}');

            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                _logger.LogDebug("No JSON object found in output");
                return null;
            }

            var json = output.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var response = JsonSerializer.Deserialize<CursorReviewResponse>(json, options);

            if (response != null)
            {
                NormalizeFilePaths(response);
                return response;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse JSON response");
        }

        return null;
    }

    private CursorReviewResponse? TryParseCliWrapper(string output, JsonSerializerOptions options)
    {
        try
        {
            // The CLI returns output in this format:
            // {"type":"result","subtype":"success","is_error":false,"duration_ms":123,"result":"...text with embedded JSON..."}
            
            var jsonStart = output.IndexOf('{');
            var jsonEnd = output.LastIndexOf('}');
            
            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                return null;
            }

            var json = output.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var wrapper = JsonSerializer.Deserialize<CliOutputWrapper>(json, options);
            
            if (wrapper?.Result == null)
            {
                return null;
            }

            _logger.LogDebug("Parsed CLI wrapper. Type: {Type}, Duration: {Duration}ms", 
                wrapper.Type, wrapper.DurationMs);

            // The result field contains text that may include our JSON response
            // The AI might wrap JSON in markdown code blocks, so we need to handle that
            var resultText = wrapper.Result;
            
            // Strip markdown code blocks if present
            if (resultText.Contains("```json"))
            {
                var codeBlockStart = resultText.IndexOf("```json");
                var codeBlockEnd = resultText.IndexOf("```", codeBlockStart + 7);
                if (codeBlockEnd > codeBlockStart)
                {
                    resultText = resultText.Substring(codeBlockStart + 7, codeBlockEnd - codeBlockStart - 7).Trim();
                    _logger.LogDebug("Extracted JSON from markdown code block");
                }
            }
            else if (resultText.Contains("```"))
            {
                // Handle generic code block without language specifier
                var codeBlockStart = resultText.IndexOf("```");
                var codeBlockEnd = resultText.IndexOf("```", codeBlockStart + 3);
                if (codeBlockEnd > codeBlockStart)
                {
                    var extracted = resultText.Substring(codeBlockStart + 3, codeBlockEnd - codeBlockStart - 3).Trim();
                    if (extracted.StartsWith("{"))
                    {
                        resultText = extracted;
                        _logger.LogDebug("Extracted JSON from generic code block");
                    }
                }
            }

            // Find the JSON object within the result string
            // Handle various whitespace formats: {"files", { "files", {\n"files", etc.
            var resultJsonStart = -1;
            
            // Try to find the start of the files array with various whitespace patterns
            var patterns = new[] { "{\"files\"", "{ \"files\"", "{\n\"files\"", "{\n    \"files\"", "{\r\n\"files\"" };
            foreach (var pattern in patterns)
            {
                resultJsonStart = resultText.IndexOf(pattern);
                if (resultJsonStart >= 0) break;
            }
            
            // If still not found, try regex-like approach: find { followed by whitespace and "files"
            if (resultJsonStart < 0)
            {
                for (var i = 0; i < resultText.Length - 10; i++)
                {
                    if (resultText[i] == '{')
                    {
                        // Check if this is followed by whitespace and "files"
                        var j = i + 1;
                        while (j < resultText.Length && char.IsWhiteSpace(resultText[j])) j++;
                        if (j < resultText.Length - 7 && resultText.Substring(j, 7) == "\"files\"")
                        {
                            resultJsonStart = i;
                            break;
                        }
                    }
                }
            }
            
            if (resultJsonStart < 0)
            {
                _logger.LogDebug("No review JSON found in CLI result field. Result text: {Text}", 
                    resultText.Length > 500 ? resultText[..500] + "..." : resultText);
                return null;
            }

            // Find matching closing brace
            var braceCount = 0;
            var resultJsonEnd = -1;
            for (var i = resultJsonStart; i < resultText.Length; i++)
            {
                if (resultText[i] == '{') braceCount++;
                else if (resultText[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        resultJsonEnd = i;
                        break;
                    }
                }
            }

            if (resultJsonEnd <= resultJsonStart)
            {
                _logger.LogDebug("Could not find closing brace for review JSON");
                return null;
            }

            var reviewJson = resultText.Substring(resultJsonStart, resultJsonEnd - resultJsonStart + 1);
            _logger.LogDebug("Extracted review JSON: {Json}", reviewJson.Length > 500 ? reviewJson[..500] + "..." : reviewJson);
            
            var response = JsonSerializer.Deserialize<CursorReviewResponse>(reviewJson, options);
            
            if (response != null)
            {
                NormalizeFilePaths(response);
                return response;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse CLI wrapper format");
        }
        
        return null;
    }

    private static void NormalizeFilePaths(CursorReviewResponse response)
    {
        foreach (var file in response.Files)
        {
            foreach (var comment in file.Comments)
            {
                if (string.IsNullOrEmpty(comment.FilePath))
                {
                    comment.FilePath = file.FilePath;
                }
            }
        }
    }

    // CLI output wrapper class
    private class CliOutputWrapper
    {
        public string? Type { get; set; }
        public string? Subtype { get; set; }
        public bool IsError { get; set; }
        public int DurationMs { get; set; }
        public string? Result { get; set; }
        public string? SessionId { get; set; }
        public string? RequestId { get; set; }
    }

    private static string EscapeArgument(string arg)
    {
        // Escape the prompt for command line
        // Replace quotes and escape special characters
        var escaped = arg
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", " ");

        return $"\"{escaped}\"";
    }

    private void CleanupTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                _logger.LogDebug("Cleaned up temp directory: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup temp directory: {Path}", path);
        }
    }
}
