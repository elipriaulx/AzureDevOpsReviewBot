DevOpsReviewBot - Automated Code Review
=======================================

A .NET 10 application that automatically reviews Azure DevOps pull requests using Cursor Agent CLI.

> [!WARNING]
> This has been vibed, with very little oversight - use at your own risk.

Setup
----

### 1. Install Cursor Agent CLI

Install the Cursor Agent CLI following the official instructions:

```powershell
# Windows PowerShell
irm 'https://cursor.com/install?win32=true' | iex
```

Verify the installation and authenticate:
```powershell
agent --version
agent login
```

Note that you can also authenticate cursor with an environment variable.

### 2. Configure Azure DevOps PAT

Create a Personal Access Token in Azure DevOps with these permissions:
- **Code**: Read
- **Pull Request Threads**: Read & Write

### 3. Create Local Configuration

Create `src/DevOpsReviewBot/appsettings.Development.json` with your personal settings:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "DevOpsReviewBot": "Debug"
    }
  },
  "AzureDevOps": {
    "OrganizationUrl": "https://dev.azure.com/your-organization",
    "PersonalAccessToken": "your-pat-here",
    "Repositories": [
      { "Project": "YourProject", "Repository": "YourRepository" }
    ]
  },
  "CursorCli": {
    "AgentCommand": "C:\\Users\\YourName\\AppData\\Local\\cursor-agent\\agent.cmd"
  }
}
```

> **Note**: This file is gitignored to keep your PAT and personal settings out of source control.

### 4. Prerequisites

- .NET 10 SDK
- Cursor Agent CLI installed and authenticated
- Azure DevOps PAT with appropriate permissions

### 5. Run

```powershell
cd src/DevOpsReviewBot
dotnet run
```

Or from the solution root:
```powershell
dotnet run --project src/DevOpsReviewBot
```

The service polls for open PRs every 5 minutes (configurable) and posts review comments for new commits.

Configuration Options
---------------------

Settings in `appsettings.Development.json` override `appsettings.json`.

### AzureDevOps Section

| Setting | Description |
|---------|-------------|
| `OrganizationUrl` | Your Azure DevOps organization URL |
| `PersonalAccessToken` | PAT with Code Read and PR Thread permissions |
| `Repositories` | Array of `{ Project, Repository }` to monitor |

### CursorCli Section

| Setting | Description | Default |
|---------|-------------|---------|
| `AgentCommand` | Path to agent CLI (use full path if not in PATH) | `agent` |
| `TimeoutSeconds` | Maximum time to wait for CLI response | `300` |
| `MaxRetries` | Number of retry attempts for transient failures | `3` |
| `ModelName` | AI model to use (e.g., `gpt-5`, `sonnet-4`) | `null` (auto) |

### Review Section

| Setting | Description | Default |
|---------|-------------|---------|
| `PollingIntervalMinutes` | How often to check for new PRs | `5` |
| `CommentPrefix` | Prefix for all posted comments | `[DevOpsReviewBot]` |
| `MaxCommentsPerFile` | Maximum comments per file | `5` |
| `MaxFileSizeKb` | Skip files larger than this | `500` |
| `ExcludePatterns` | Glob patterns for files to skip | See config |

Troubleshooting
---------------

### "Cannot find the file specified" error

The agent CLI path needs to be specified. Find it with:
```powershell
where.exe agent
```

Then add to your `appsettings.Development.json`:
```json
{
  "CursorCli": {
    "AgentCommand": "C:\\Users\\YourName\\AppData\\Local\\cursor-agent\\agent.cmd"
  }
}
```

### Proxy Configuration

If behind a corporate proxy, set environment variables before running:

```powershell
$env:HTTP_PROXY = "http://proxy-server:port"
$env:HTTPS_PROXY = "http://proxy-server:port"
dotnet run --project src/DevOpsReviewBot
```

## How It Works

1. Polls Azure DevOps for open pull requests in configured repositories
2. Checks state file to identify commits not yet reviewed
3. Retrieves changed files (filtered by extension and size)
4. Creates a temporary workspace with the files
5. Invokes Cursor Agent CLI with `agent -p` for AI review
6. Parses JSON response and posts comments to the PR
7. Updates state to track reviewed commits
8. Cleans up temporary files

Comments are prefixed with the configured message (default: `[DevOpsReviewBot]`). If a file has many issues, a summary is posted instead of individual comments.
