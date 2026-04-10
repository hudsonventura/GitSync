using GitSync.Interfaces;
using GitSync.Providers;
using GitSync.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using dotenv.net;

DotEnv.Load();

// ===== Configuration from environment variables =====
var providerAType = GetRequiredEnv("PROVIDER_A_TYPE");   // github, gitea, gitlab, azuredevops
var providerAUrl = Environment.GetEnvironmentVariable("PROVIDER_A_URL") ?? "";
var providerAToken = GetRequiredEnv("PROVIDER_A_TOKEN");
var providerAUsername = GetRequiredEnv("PROVIDER_A_USERNAME");

var providerBType = GetRequiredEnv("PROVIDER_B_TYPE");   // github, gitea, gitlab, azuredevops
var providerBUrl = Environment.GetEnvironmentVariable("PROVIDER_B_URL") ?? "";
var providerBToken = GetRequiredEnv("PROVIDER_B_TOKEN");
var providerBUsername = GetRequiredEnv("PROVIDER_B_USERNAME");

var syncIntervalSeconds = int.Parse(Environment.GetEnvironmentVariable("SYNC_INTERVAL_SECONDS") ?? "300");
var reposPath = Environment.GetEnvironmentVariable("REPOS_PATH") ?? "/data/repos";

// ===== DI Container =====
var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(
        Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOG_LEVEL"), true, out var level)
            ? level
            : LogLevel.Information
    );
});

services.AddHttpClient();

var serviceProvider = services.BuildServiceProvider();
var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

// ===== Create Providers =====
var providerA = CreateProvider("ProviderA", providerAType, providerAUrl, providerAToken, providerAUsername, httpClientFactory, loggerFactory);
var providerB = CreateProvider("ProviderB", providerBType, providerBUrl, providerBToken, providerBUsername, httpClientFactory, loggerFactory);

// ===== Create Services =====
var mirrorService = new GitMirrorService(reposPath, loggerFactory.CreateLogger<GitMirrorService>());
var syncService = new SyncService(providerA, providerB, mirrorService, loggerFactory.CreateLogger<SyncService>());

// ===== Main Loop =====
var logger = loggerFactory.CreateLogger("GitSync");

logger.LogInformation("GitSync started");
logger.LogInformation("Provider A: {Type} ({User})", providerAType, providerAUsername);
logger.LogInformation("Provider B: {Type} ({User})", providerBType, providerBUsername);
logger.LogInformation("Sync interval: {Interval} seconds", syncIntervalSeconds);
logger.LogInformation("Repos path: {Path}", reposPath);

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Shutdown requested...");
    cts.Cancel();
};

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        await syncService.SyncAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Sync cycle failed");
    }

    try
    {
        logger.LogInformation("Next sync in {Interval} seconds...", syncIntervalSeconds);
        await Task.Delay(TimeSpan.FromSeconds(syncIntervalSeconds), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

logger.LogInformation("GitSync stopped.");

// ===== Helper Functions =====

static string GetRequiredEnv(string name)
{
    return Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
}

static IGitProvider CreateProvider(
    string label,
    string type,
    string url,
    string token,
    string username,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory)
{
    var httpClient = httpClientFactory.CreateClient(label);

    return type.ToLowerInvariant() switch
    {
        "github" => new GitHubProvider(httpClient, token, username, loggerFactory.CreateLogger<GitHubProvider>()),
        "gitea" => new GiteaProvider(httpClient, token, username,
            !string.IsNullOrWhiteSpace(url) ? url : throw new InvalidOperationException($"'{label}': PROVIDER_*_URL is required for Gitea"),
            loggerFactory.CreateLogger<GiteaProvider>()),
        "gitlab" => new GitLabProvider(httpClient, token, username,
            !string.IsNullOrWhiteSpace(url) ? url : throw new InvalidOperationException($"'{label}': PROVIDER_*_URL is required for GitLab"),
            loggerFactory.CreateLogger<GitLabProvider>()),
        "azuredevops" => new AzureDevOpsProvider(httpClient, token, username,
            !string.IsNullOrWhiteSpace(url) ? url : throw new InvalidOperationException($"'{label}': PROVIDER_*_URL is required for Azure DevOps"),
            loggerFactory.CreateLogger<AzureDevOpsProvider>()),
        _ => throw new InvalidOperationException($"Unknown provider type: '{type}'. Supported: github, gitea, gitlab, azuredevops")
    };
}
