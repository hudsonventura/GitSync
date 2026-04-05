using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitSync.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitSync.Providers;

/// <summary>
/// Git provider implementation for GitLab instances (including gitlab.com).
/// Uses PRIVATE-TOKEN header and visibility string instead of boolean.
/// </summary>
public class GitLabProvider : IGitProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _username;
    private readonly string _baseUrl;
    private readonly ILogger<GitLabProvider> _logger;

    public string ProviderName => "GitLab";

    public GitLabProvider(HttpClient httpClient, string token, string username, string baseUrl, ILogger<GitLabProvider> logger)
    {
        _httpClient = httpClient;
        _token = token;
        _username = username;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;

        _httpClient.BaseAddress = new Uri($"{_baseUrl}/api/v4/");
        _httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", _token);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<RepositoryInfo>> GetRepositoriesAsync()
    {
        var repos = new List<RepositoryInfo>();
        int page = 1;

        while (true)
        {
            var response = await _httpClient.GetAsync($"projects?owned=true&per_page=100&page={page}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var items = JsonSerializer.Deserialize<List<JsonElement>>(json);

            if (items == null || items.Count == 0)
                break;

            foreach (var item in items)
            {
                var visibility = item.GetProperty("visibility").GetString() ?? "private";

                repos.Add(new RepositoryInfo
                {
                    Name = item.GetProperty("path").GetString() ?? "",
                    Description = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null
                        ? desc.GetString() ?? ""
                        : "",
                    IsPrivate = visibility != "public",
                    CloneUrl = item.GetProperty("http_url_to_repo").GetString() ?? "",
                    HtmlUrl = item.GetProperty("web_url").GetString() ?? ""
                });
            }

            if (items.Count < 100)
                break;

            page++;
        }

        _logger.LogInformation("Found {Count} repositories on GitLab for user {User}", repos.Count, _username);
        return repos;
    }

    public async Task<RepositoryInfo> CreateRepositoryAsync(string name, string description, bool isPrivate)
    {
        var payload = new
        {
            name,
            description,
            visibility = isPrivate ? "private" : "public"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync("projects", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create repository '{Name}' on GitLab: {Status} - {Body}",
                name, response.StatusCode, responseBody);
            throw new Exception($"Failed to create repository '{name}' on GitLab: {response.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var visibility = item.GetProperty("visibility").GetString() ?? "private";

        var repo = new RepositoryInfo
        {
            Name = item.GetProperty("path").GetString() ?? "",
            Description = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null
                ? desc.GetString() ?? ""
                : "",
            IsPrivate = visibility != "public",
            CloneUrl = item.GetProperty("http_url_to_repo").GetString() ?? "",
            HtmlUrl = item.GetProperty("web_url").GetString() ?? ""
        };

        _logger.LogInformation("Created repository '{Name}' on GitLab ({Visibility})",
            repo.Name, repo.IsPrivate ? "private" : "public");

        return repo;
    }

    public string GetAuthenticatedCloneUrl(string repoName)
    {
        var uri = new Uri(_baseUrl);
        return $"{uri.Scheme}://oauth2:{_token}@{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{_username}/{repoName}.git";
    }
}
