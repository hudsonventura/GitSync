namespace GitSync.Interfaces;

/// <summary>
/// Interface for interacting with a Git hosting provider (GitHub, Gitea, GitLab, etc.).
/// Used for both source and destination providers.
/// </summary>
public interface IGitProvider
{
    /// <summary>
    /// Display name of the provider (e.g., "GitHub", "Gitea", "GitLab").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Retrieves all repositories accessible to the authenticated user.
    /// Includes both public and private repositories.
    /// </summary>
    Task<List<RepositoryInfo>> GetRepositoriesAsync();

    /// <summary>
    /// Creates a new repository on the provider.
    /// </summary>
    /// <param name="name">Repository name</param>
    /// <param name="description">Repository description</param>
    /// <param name="isPrivate">Whether the repository should be private</param>
    /// <returns>The created repository info</returns>
    Task<RepositoryInfo> CreateRepositoryAsync(string name, string description, bool isPrivate);

    /// <summary>
    /// Builds the authenticated clone URL for a given repository name.
    /// The URL includes credentials so git operations can authenticate.
    /// </summary>
    string GetAuthenticatedCloneUrl(string repoName);
}
