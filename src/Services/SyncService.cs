using GitSync.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitSync.Services;

/// <summary>
/// Orchestrates bidirectional repository synchronization between two git providers.
/// - Repositories only on Provider A are created on Provider B and mirrored
/// - Repositories only on Provider B are created on Provider A and mirrored
/// - Repositories on both are synced bidirectionally (all branches/tags)
/// </summary>
public class SyncService
{
    private readonly IGitProvider _providerA;
    private readonly IGitProvider _providerB;
    private readonly GitMirrorService _mirrorService;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IGitProvider providerA,
        IGitProvider providerB,
        GitMirrorService mirrorService,
        ILogger<SyncService> logger)
    {
        _providerA = providerA;
        _providerB = providerB;
        _mirrorService = mirrorService;
        _logger = logger;
    }

    /// <summary>
    /// Performs a full bidirectional sync cycle.
    /// </summary>
    public async Task SyncAsync()
    {
        _logger.LogInformation("=== Starting bidirectional sync: {A} <-> {B} ===",
            _providerA.ProviderName, _providerB.ProviderName);

        List<RepositoryInfo> reposA;
        List<RepositoryInfo> reposB;

        try
        {
            reposA = await _providerA.GetRepositoriesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch repositories from {Provider}", _providerA.ProviderName);
            return;
        }

        try
        {
            reposB = await _providerB.GetRepositoriesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch repositories from {Provider}", _providerB.ProviderName);
            return;
        }

        var repoNamesA = new HashSet<string>(reposA.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var repoNamesB = new HashSet<string>(reposB.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        var repoMapA = reposA.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var repoMapB = reposB.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        // All unique repo names from both sides
        var allRepoNames = new HashSet<string>(repoNamesA, StringComparer.OrdinalIgnoreCase);
        allRepoNames.UnionWith(repoNamesB);

        _logger.LogInformation("Provider A ({Name}): {Count} repos | Provider B ({NameB}): {CountB} repos | Total unique: {Total}",
            _providerA.ProviderName, reposA.Count,
            _providerB.ProviderName, reposB.Count,
            allRepoNames.Count);

        int synced = 0, created = 0, errors = 0;

        foreach (var repoName in allRepoNames)
        {
            try
            {
                var existsOnA = repoMapA.TryGetValue(repoName, out var repoA);
                var existsOnB = repoMapB.TryGetValue(repoName, out var repoB);

                if (existsOnA && !existsOnB)
                {
                    // Create on Provider B
                    _logger.LogInformation("[{Repo}] Exists only on {Provider}, creating on {Other}...",
                        repoName, _providerA.ProviderName, _providerB.ProviderName);

                    await _providerB.CreateRepositoryAsync(repoA!.Name, repoA.Description, repoA.IsPrivate);
                    created++;
                }
                else if (!existsOnA && existsOnB)
                {
                    // Create on Provider A
                    _logger.LogInformation("[{Repo}] Exists only on {Provider}, creating on {Other}...",
                        repoName, _providerB.ProviderName, _providerA.ProviderName);

                    await _providerA.CreateRepositoryAsync(repoB!.Name, repoB.Description, repoB.IsPrivate);
                    created++;
                }

                // Mirror bidirectionally
                var urlA = _providerA.GetAuthenticatedCloneUrl(repoName);
                var urlB = _providerB.GetAuthenticatedCloneUrl(repoName);

                await _mirrorService.MirrorAsync(repoName, urlA, urlB);
                synced++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Repo}] Failed to sync repository", repoName);
                errors++;
            }
        }

        _logger.LogInformation("=== Sync complete: {Synced} synced, {Created} created, {Errors} errors ===",
            synced, created, errors);
    }
}
