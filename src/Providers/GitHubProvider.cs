using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitSync.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitSync.Providers;

/// <summary>
/// Git provider implementation for GitHub (api.github.com).
/// </summary>
public class GitHubProvider : IGitProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _username;
    private readonly ILogger<GitHubProvider> _logger;

    public string ProviderName => "GitHub";

    public GitHubProvider(HttpClient httpClient, string token, string username, ILogger<GitHubProvider> logger)
    {
        _httpClient = httpClient;
        _token = token;
        _username = username;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.github.com");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GitSync/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<List<RepositoryInfo>> GetRepositoriesAsync()
    {
        var repos = new List<RepositoryInfo>();
        int page = 1;

        while (true)
        {
            var response = await _httpClient.GetAsync($"/user/repos?per_page=100&page={page}&affiliation=owner");
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch repositories from GitHub: {Status} - {Body}",
                    response.StatusCode, responseBody);
                response.EnsureSuccessStatusCode();
            }

            var items = JsonSerializer.Deserialize<List<JsonElement>>(responseBody);

            if (items == null || items.Count == 0)
                break;

            foreach (var item in items)
            {
                repos.Add(new RepositoryInfo
                {
                    Name = item.GetProperty("name").GetString() ?? "",
                    Description = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null
                        ? desc.GetString() ?? ""
                        : "",
                    IsPrivate = item.GetProperty("private").GetBoolean(),
                    CloneUrl = item.GetProperty("clone_url").GetString() ?? "",
                    HtmlUrl = item.GetProperty("html_url").GetString() ?? ""
                });
            }

            if (items.Count < 100)
                break;

            page++;
        }

        _logger.LogInformation("Found {Count} repositories on GitHub for user {User}", repos.Count, _username);
        return repos;
    }

    public async Task<RepositoryInfo> CreateRepositoryAsync(string name, string description, bool isPrivate)
    {
        var payload = new
        {
            name,
            description,
            @private = isPrivate,
            auto_init = false
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync("/user/repos", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create repository '{Name}' on GitHub: {Status} - {Body}",
                name, response.StatusCode, responseBody);
            throw new Exception($"Failed to create repository '{name}' on GitHub: {response.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<JsonElement>(responseBody);

        var repo = new RepositoryInfo
        {
            Name = item.GetProperty("name").GetString() ?? "",
            Description = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null
                ? desc.GetString() ?? ""
                : "",
            IsPrivate = item.GetProperty("private").GetBoolean(),
            CloneUrl = item.GetProperty("clone_url").GetString() ?? "",
            HtmlUrl = item.GetProperty("html_url").GetString() ?? ""
        };

        _logger.LogInformation("Created repository '{Name}' on GitHub ({Visibility})",
            repo.Name, repo.IsPrivate ? "private" : "public");

        return repo;
    }

    public string GetAuthenticatedCloneUrl(string repoName)
    {
        return $"https://{_username}:{_token}@github.com/{_username}/{repoName}.git";
    }
}
