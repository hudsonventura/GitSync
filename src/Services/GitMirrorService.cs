using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GitSync.Services;

/// <summary>
/// Handles git clone/fetch/push mirror operations via the git CLI.
/// Uses bare repositories to perform efficient mirroring.
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
    /// Clones as bare if not already cloned, then fetches from both remotes and pushes to both.
    /// </summary>
    public async Task MirrorAsync(string repoName, string remoteAUrl, string remoteBUrl)
    {
        var repoPath = Path.Combine(_reposPath, $"{repoName}.git");

        if (!Directory.Exists(repoPath))
        {
            // Initial clone from remote A
            _logger.LogInformation("[{Repo}] Cloning bare repository from remote A...", repoName);
            await RunGitAsync(_reposPath, "clone", "--bare", remoteAUrl, $"{repoName}.git");

            // Add remote B
            _logger.LogInformation("[{Repo}] Adding remote B...", repoName);
            await RunGitAsync(repoPath, "remote", "add", "remoteB", remoteBUrl);
        }

        // Ensure remote URLs are up to date
        await RunGitAsync(repoPath, "remote", "set-url", "origin", remoteAUrl);
        await SetRemoteUrlSafe(repoPath, "remoteB", remoteBUrl);

        // Fetch from both remotes
        _logger.LogInformation("[{Repo}] Fetching from remote A (origin)...", repoName);
        await RunGitAsync(repoPath, "fetch", "--prune", "origin", "+refs/*:refs/*");

        _logger.LogInformation("[{Repo}] Fetching from remote B...", repoName);
        try
        {
            await RunGitAsync(repoPath, "fetch", "--prune", "remoteB", "+refs/*:refs/*");
        }
        catch (Exception ex)
        {
            // Remote B might be empty (newly created), that's OK
            _logger.LogWarning("[{Repo}] Fetch from remote B failed (may be empty): {Message}", repoName, ex.Message);
        }

        // Push to both remotes
        _logger.LogInformation("[{Repo}] Pushing to remote A (origin)...", repoName);
        await RunGitAsync(repoPath, "push", "--mirror", remoteAUrl);

        _logger.LogInformation("[{Repo}] Pushing to remote B...", repoName);
        await RunGitAsync(repoPath, "push", "--mirror", remoteBUrl);

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
