using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitSync.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitSync.Providers;

/// <summary>
/// Git provider implementation for Azure DevOps projects.
/// Expects a project URL such as https://dev.azure.com/{organization}/{project}.
/// Repository visibility follows the Azure DevOps project visibility.
/// </summary>
public class AzureDevOpsProvider : IGitProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _username;
    private readonly string _projectUrl;
    private readonly string _organizationSegment;
    private readonly string _projectName;
    private readonly string _organizationUrl;
    private readonly ILogger<AzureDevOpsProvider> _logger;
    private ProjectMetadata? _projectMetadata;

    public string ProviderName => "Azure DevOps";

    public AzureDevOpsProvider(
        HttpClient httpClient,
        string token,
        string username,
        string projectUrl,
        ILogger<AzureDevOpsProvider> logger)
    {
        _httpClient = httpClient;
        _token = token;
        _username = username;
        _projectUrl = projectUrl.TrimEnd('/');
        _logger = logger;

        (_organizationSegment, _projectName) = ParseProjectUrl(_projectUrl);
        _organizationUrl = BuildOrganizationUrl(_projectUrl, _organizationSegment);

        _httpClient.BaseAddress = new Uri($"{_projectUrl}/_apis/git/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_token}")));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<RepositoryInfo>> GetRepositoriesAsync()
    {
        var project = await GetProjectMetadataAsync();
        var repos = new List<RepositoryInfo>();
        string? continuationToken = null;

        while (true)
        {
            var requestUri = "repositories?api-version=7.1";

            if (!string.IsNullOrWhiteSpace(continuationToken))
            {
                requestUri += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
            }

            var response = await _httpClient.GetAsync(requestUri);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch repositories from Azure DevOps: {Status} - {Body}",
                    response.StatusCode, responseBody);
                response.EnsureSuccessStatusCode();
            }

            var payload = JsonSerializer.Deserialize<JsonElement>(responseBody);
            var items = payload.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToList()
                : [];

            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                repos.Add(new RepositoryInfo
                {
                    Name = item.GetProperty("name").GetString() ?? "",
                    Description = item.TryGetProperty("project", out var projectElement)
                        && projectElement.TryGetProperty("description", out var description)
                        && description.ValueKind != JsonValueKind.Null
                            ? description.GetString() ?? ""
                            : "",
                    IsPrivate = project.IsPrivate,
                    CloneUrl = item.GetProperty("remoteUrl").GetString() ?? "",
                    HtmlUrl = item.GetProperty("webUrl").GetString() ?? ""
                });
            }

            if (!response.Headers.TryGetValues("x-ms-continuationtoken", out var continuationValues))
            {
                break;
            }

            continuationToken = continuationValues.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                break;
            }
        }

        _logger.LogInformation("Found {Count} repositories on Azure DevOps project {Project}", repos.Count, _projectName);
        return repos;
    }

    public async Task<RepositoryInfo> CreateRepositoryAsync(string name, string description, bool isPrivate)
    {
        var project = await GetProjectMetadataAsync();

        if (!isPrivate)
        {
            _logger.LogWarning(
                "Azure DevOps repositories inherit project visibility. Repository '{Name}' will be created in project '{Project}' with the project's access settings.",
                name,
                project.Name);
        }

        var payload = new
        {
            name,
            project = new
            {
                id = project.Id
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync($"{_organizationUrl}/_apis/git/repositories?api-version=7.1", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create repository '{Name}' on Azure DevOps: {Status} - {Body}",
                name, response.StatusCode, responseBody);
            throw new Exception($"Failed to create repository '{name}' on Azure DevOps: {response.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var repo = new RepositoryInfo
        {
            Name = item.GetProperty("name").GetString() ?? "",
            Description = description,
            IsPrivate = project.IsPrivate,
            CloneUrl = item.GetProperty("remoteUrl").GetString() ?? "",
            HtmlUrl = item.GetProperty("webUrl").GetString() ?? ""
        };

        _logger.LogInformation("Created repository '{Name}' on Azure DevOps project {Project}",
            repo.Name, project.Name);

        return repo;
    }

    public string GetAuthenticatedCloneUrl(string repoName)
    {
        var uri = new Uri(_projectUrl);
        return $"{uri.Scheme}://{Uri.EscapeDataString(_username)}:{Uri.EscapeDataString(_token)}@{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{_organizationSegment}/{Uri.EscapeDataString(_projectName)}/_git/{Uri.EscapeDataString(repoName)}";
    }

    private async Task<ProjectMetadata> GetProjectMetadataAsync()
    {
        if (_projectMetadata is not null)
        {
            return _projectMetadata;
        }

        var uri = $"{_organizationUrl}/_apis/projects/{Uri.EscapeDataString(_projectName)}?api-version=7.1";
        var response = await _httpClient.GetAsync(uri);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Failed to fetch Azure DevOps project metadata for {Project}: {Status} - {Body}",
                _projectName,
                response.StatusCode,
                responseBody);
            throw new Exception($"Failed to fetch Azure DevOps project metadata for '{_projectName}': {response.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var id = item.GetProperty("id").GetString()
            ?? throw new Exception($"Azure DevOps project '{_projectName}' did not return an id.");
        var name = item.GetProperty("name").GetString() ?? _projectName;
        var isPrivate = !(
            item.TryGetProperty("visibility", out var visibility)
            && string.Equals(visibility.GetString(), "public", StringComparison.OrdinalIgnoreCase));

        _projectMetadata = new ProjectMetadata(id, name, isPrivate);
        return _projectMetadata;
    }

    private static (string OrganizationSegment, string ProjectName) ParseProjectUrl(string projectUrl)
    {
        var uri = new Uri(projectUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
        {
            throw new InvalidOperationException(
                "Azure DevOps URL must include organization and project, e.g. https://dev.azure.com/myorg/myproject");
        }

        var organizationSegment = segments[0];
        var projectName = segments[1];

        return (organizationSegment, projectName);
    }

    private static string BuildOrganizationUrl(string projectUrl, string organizationSegment)
    {
        var uri = new Uri(projectUrl);
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{organizationSegment}";
    }

    private sealed record ProjectMetadata(string Id, string Name, bool IsPrivate);
}
