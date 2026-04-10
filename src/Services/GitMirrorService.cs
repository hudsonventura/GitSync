using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GitSync.Services;

/// <summary>
/// Handles git clone/fetch/push synchronization via the git CLI.
/// Uses a standard local clone so repositories can be fetched from one remote
/// and pushed explicitly to the other.
/// </summary>
public class GitMirrorService
{
    private readonly string _reposPath;
    private readonly ILogger<GitMirrorService> _logger;

    public GitMirrorService(string reposPath, ILogger<GitMirrorService> logger)
    {
        _reposPath = reposPath;
        _logger = logger;

        if (!Directory.Exists(_reposPath))
        {
            Directory.CreateDirectory(_reposPath);
            _logger.LogInformation("Created repos directory: {Path}", _reposPath);
        }
    }

    /// <summary>
    /// Mirrors a repository between two remotes bidirectionally.
    /// Clones normally if not already cloned, then fetches from both remotes,
    /// reconciles branch tips from the fetched refs, and pushes branches/tags to both sides.
    /// </summary>
    public async Task MirrorAsync(string repoName, string remoteAUrl, string remoteBUrl)
    {
        var repoPath = Path.Combine(_reposPath, repoName);

        if (!Directory.Exists(repoPath))
        {
            // Initial clone from remote A
            _logger.LogInformation("[{Repo}] Cloning repository from remote A...", repoName);
            await RunGitAsync(_reposPath, "clone", remoteAUrl, repoName);

            // Add remote B
            _logger.LogInformation("[{Repo}] Adding remote B...", repoName);
            await RunGitAsync(repoPath, "remote", "add", "remoteB", remoteBUrl);
        }

        // Ensure remote URLs are up to date
        await RunGitAsync(repoPath, "remote", "set-url", "origin", remoteAUrl);
        await SetRemoteUrlSafe(repoPath, "remoteB", remoteBUrl);

        // Fetch from both remotes
        _logger.LogInformation("[{Repo}] Fetching from remote A (origin)...", repoName);
        await RunGitAsync(repoPath, "fetch", "--prune", "origin", "+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*");

        _logger.LogInformation("[{Repo}] Fetching from remote B...", repoName);
        try
        {
            await RunGitAsync(repoPath, "fetch", "--prune", "remoteB", "+refs/heads/*:refs/remotes/remoteB/*", "+refs/tags/*:refs/tags/*");
        }
        catch (Exception ex)
        {
            // Remote B might be empty (newly created), that's OK
            _logger.LogWarning("[{Repo}] Fetch from remote B failed (may be empty): {Message}", repoName, ex.Message);
        }

        await SynchronizeBranchesAsync(repoPath);

        // Push to both remotes
        _logger.LogInformation("[{Repo}] Pushing to remote A (origin)...", repoName);
        await PushAllRefsAsync(repoPath, "origin");

        _logger.LogInformation("[{Repo}] Pushing to remote B...", repoName);
        await PushAllRefsAsync(repoPath, "remoteB");

        _logger.LogInformation("[{Repo}] Mirror sync complete.", repoName);
    }

    private async Task SetRemoteUrlSafe(string repoPath, string remoteName, string url)
    {
        try
        {
            await RunGitAsync(repoPath, "remote", "set-url", remoteName, url);
        }
        catch
        {
            // Remote might not exist yet, try adding it
            await RunGitAsync(repoPath, "remote", "add", remoteName, url);
        }
    }

    private async Task SynchronizeBranchesAsync(string repoPath)
    {
        var hasHeadCommit = await HasHeadCommitAsync(repoPath);
        if (hasHeadCommit)
        {
            await RunGitAsync(repoPath, "checkout", "--detach");
        }

        var currentBranch = await GetCurrentBranchNameAsync(repoPath);
        var branchesByRemote = await GetRemoteBranchesAsync(repoPath);
        var desiredBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var branchName in branchesByRemote.Keys)
        {
            var sourceRef = await ResolveBranchSourceRefAsync(repoPath, branchName, branchesByRemote[branchName]);

            if (sourceRef is null)
            {
                continue;
            }

            await RunGitAsync(repoPath, "branch", "--force", branchName, sourceRef);
            desiredBranches[branchName] = sourceRef;
        }

        if (!hasHeadCommit && desiredBranches.Count > 0)
        {
            var branchToCheckout = desiredBranches.ContainsKey(currentBranch ?? string.Empty)
                ? currentBranch!
                : desiredBranches.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();

            _logger.LogInformation("Checking out branch {Branch} to initialize local repository state", branchToCheckout);
            await RunGitAsync(repoPath, "checkout", branchToCheckout);
            currentBranch = branchToCheckout;
        }

        var localBranchesOutput = await RunGitAsync(repoPath, "for-each-ref", "refs/heads", "--format=%(refname:strip=2)");
        foreach (var localBranch in localBranchesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!desiredBranches.ContainsKey(localBranch))
            {
                _logger.LogInformation("Deleting stale local branch {Branch}", localBranch);
                await RunGitAsync(repoPath, "branch", "--delete", "--force", localBranch);
            }
        }
    }

    private async Task<bool> HasHeadCommitAsync(string repoPath)
    {
        try
        {
            await RunGitAsync(repoPath, "rev-parse", "--verify", "HEAD");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> GetCurrentBranchNameAsync(string repoPath)
    {
        try
        {
            var branchName = await RunGitAsync(repoPath, "branch", "--show-current");
            var normalized = branchName.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> GetRemoteBranchesAsync(string repoPath)
    {
        var branchesByRemote = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var remoteName in new[] { "origin", "remoteB" })
        {
            var output = await RunGitAsync(
                repoPath,
                "for-each-ref",
                $"refs/remotes/{remoteName}",
                "--format=%(refname:strip=3) %(objectname)");

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = line.IndexOf(' ');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var branchName = line[..separatorIndex];
                if (string.Equals(branchName, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var commitSha = line[(separatorIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(commitSha))
                {
                    if (!branchesByRemote.TryGetValue(branchName, out var remotes))
                    {
                        remotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        branchesByRemote[branchName] = remotes;
                    }

                    remotes[remoteName] = commitSha;
                }
            }
        }

        return branchesByRemote;
    }

    private async Task<string?> ResolveBranchSourceRefAsync(
        string repoPath,
        string branchName,
        Dictionary<string, string> branchByRemote)
    {
        var hasOrigin = branchByRemote.TryGetValue("origin", out _);
        var hasRemoteB = branchByRemote.TryGetValue("remoteB", out _);

        if (hasOrigin && !hasRemoteB)
        {
            return $"origin/{branchName}";
        }

        if (!hasOrigin && hasRemoteB)
        {
            return $"remoteB/{branchName}";
        }

        if (!hasOrigin || !hasRemoteB)
        {
            return null;
        }

        var comparisonOutput = await RunGitAsync(
            repoPath,
            "rev-list",
            "--left-right",
            "--count",
            $"origin/{branchName}...remoteB/{branchName}");

        var counts = comparisonOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (counts.Length != 2
            || !int.TryParse(counts[0], out var originOnlyCommits)
            || !int.TryParse(counts[1], out var remoteBOnlyCommits))
        {
            throw new Exception($"Failed to compare branch tips for '{branchName}'.");
        }

        if (originOnlyCommits == 0 && remoteBOnlyCommits == 0)
        {
            return $"origin/{branchName}";
        }

        if (originOnlyCommits == 0)
        {
            _logger.LogInformation(
                "Branch {Branch} is ahead on remoteB by {Count} commit(s); using remoteB as source.",
                branchName,
                remoteBOnlyCommits);
            return $"remoteB/{branchName}";
        }

        if (remoteBOnlyCommits == 0)
        {
            _logger.LogInformation(
                "Branch {Branch} is ahead on origin by {Count} commit(s); using origin as source.",
                branchName,
                originOnlyCommits);
            return $"origin/{branchName}";
        }

        throw new Exception(
            $"Branch '{branchName}' has diverged between origin and remoteB. Manual reconciliation is required before sync can continue safely.");
    }

    private async Task PushAllRefsAsync(string repoPath, string remoteName)
    {
        await RunGitAsync(repoPath, "push", "--prune", remoteName, "refs/heads/*:refs/heads/*");
        await RunGitAsync(repoPath, "push", remoteName, "--tags");
    }

    private async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var sanitizedArgs = SanitizeForLog(string.Join(" ", args));
        _logger.LogDebug("Running: git {Args} in {Dir}", sanitizedArgs, workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Disable git credential prompts
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)
            ?? throw new Exception("Failed to start git process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var sanitizedStderr = SanitizeForLog(stderr);
            _logger.LogError("Git command failed (exit code {Code}): {Error}",
                process.ExitCode, sanitizedStderr);
            throw new Exception($"Git command failed (exit code {process.ExitCode}): {sanitizedStderr}");
        }

        return stdout;
    }

    /// <summary>
    /// Removes tokens/passwords from log output for security.
    /// </summary>
    private static string SanitizeForLog(string input)
    {
        // Remove credentials from URLs (e.g., https://user:token@host)
        return System.Text.RegularExpressions.Regex.Replace(
            input,
            @"(https?://[^:]+:)[^@]+(@)",
            "$1***$2"
        );
    }
}
