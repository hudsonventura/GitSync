using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitSync.Interfaces;
using Microsoft.Extensions.Logging;

namespace GitSync.Providers;

/// <summary>
/// Git provider implementation for Gitea instances.
/// </summary>
public class GiteaProvider : IGitProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _username;
    private readonly string _baseUrl;
    private readonly ILogger<GiteaProvider> _logger;

    public string ProviderName => "Gitea";

    public GiteaProvider(HttpClient httpClient, string token, string username, string baseUrl, ILogger<GiteaProvider> logger)
    {
        _httpClient = httpClient;
        _token = token;
        _username = username;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;

        _httpClient.BaseAddress = new Uri($"{_baseUrl}/api/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _token);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<RepositoryInfo>> GetRepositoriesAsync()
    {
        var repos = new List<RepositoryInfo>();
        int page = 1;

        while (true)
        {
            var response = await _httpClient.GetAsync($"user/repos?limit=50&page={page}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var items = JsonSerializer.Deserialize<List<JsonElement>>(json);

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

            if (items.Count < 50)
                break;

            page++;
        }

        _logger.LogInformation("Found {Count} repositories on Gitea for user {User}", repos.Count, _username);
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

        var response = await _httpClient.PostAsync("user/repos", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create repository '{Name}' on Gitea: {Status} - {Body}",
                name, response.StatusCode, responseBody);
            throw new Exception($"Failed to create repository '{name}' on Gitea: {response.StatusCode}");
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

        _logger.LogInformation("Created repository '{Name}' on Gitea ({Visibility})",
            repo.Name, repo.IsPrivate ? "private" : "public");

        return repo;
    }

    public string GetAuthenticatedCloneUrl(string repoName)
    {
        var uri = new Uri(_baseUrl);
        return $"{uri.Scheme}://{_username}:{_token}@{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{_username}/{repoName}.git";
    }
}
