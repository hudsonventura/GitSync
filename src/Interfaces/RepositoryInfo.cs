namespace GitSync.Interfaces;

/// <summary>
/// Represents a git repository with its metadata.
/// </summary>
public class RepositoryInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public string CloneUrl { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;

    public override string ToString() => $"{Name} ({(IsPrivate ? "private" : "public")})";
}
