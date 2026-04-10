# GitSync

A .NET console application that **bidirectionally mirrors** git repositories between platforms (GitHub, Gitea, GitLab, Azure DevOps). It replicates all repositories — public and private — preserving their visibility when the provider supports per-repository visibility, automatically creates new repositories on the other side when they appear, and synchronizes all available branches between both repositories.

## Features

- **Bidirectional sync**: repositories are mirrored in both directions, including all available branches
- **Multi-platform**: supports GitHub, Gitea, GitLab, and Azure DevOps
- **Preserves visibility**: public repos stay public, private repos stay private
- **Auto-create**: new repositories are automatically created on the other provider
- **Full mirror**: all branches, tags, and refs are synchronized, and missing branches are created automatically
- **Docker ready**: includes Dockerfile and docker-compose.yml
- **Continuous sync**: runs in a loop with configurable interval

## Architecture

The application uses an `IGitProvider` interface, making it easy to add new providers. The sync flow:

1. Lists all repositories from both providers
2. Creates missing repositories on each side (preserving visibility)
3. Fetches both remotes, reconciles all available branches, creates missing branches locally and remotely when needed, and pushes branches/tags back to both sides

```
Provider A (e.g., GitHub)  <──────>  GitSync  <──────>  Provider B (e.g., Gitea)
         ▲                                                      ▲
         │              Bare repos stored locally                │
         └──────────────── /data/repos/ ────────────────────────┘
```

---

## Token Generation Guide

### GitHub

1. Go to [github.com/settings/tokens](https://github.com/settings/tokens)
2. Click **"Generate new token"** → **"Generate new token (classic)"**
3. Give it a descriptive name (e.g., `GitSync`)
4. Set expiration as desired (or "No expiration")
5. Select the following scopes:
   - ✅ **`repo`** (Full control of private repositories)
     - This includes `repo:status`, `repo_deployment`, `public_repo`, `repo:invite`, `security_events`
   - ✅ **`delete_repo`** (optional, only if you want to manage deletions)
6. Click **"Generate token"**
7. **Copy the token immediately** — you won't be able to see it again

> **Fine-grained tokens** (alternative): Go to Settings → Developer Settings → Fine-grained tokens → Generate. Select "All repositories", and grant **Read and Write** access to:
> - Repository permissions → **Contents** (read/write)
> - Repository permissions → **Administration** (read/write) — needed to create repos
> - Repository permissions → **Metadata** (read-only)

### Gitea

1. Log in to your Gitea instance
2. Go to **Settings** → **Applications** (or navigate to `/user/settings/applications`)
3. Under **"Generate New Token"**:
   - Enter a token name (e.g., `GitSync`)
   - Select permissions:
     - ✅ **repository**: Read and Write
     - ✅ **user**: Read (to list your repos)
4. Click **"Generate Token"**
5. **Copy the token immediately**

> For older Gitea versions that don't have granular permissions, the token will have full access by default.

### GitLab

1. Log in to your GitLab instance (or gitlab.com)
2. Go to **User Settings** → **Access Tokens** (or navigate to `/-/user_settings/personal_access_tokens`)
3. Click **"Add new token"**:
   - **Token name**: `GitSync`
   - **Expiration date**: set as desired
   - **Select scopes**:
     - ✅ **`api`** (full API access)
     - ✅ **`read_repository`** (read repo contents)
     - ✅ **`write_repository`** (push to repos)
4. Click **"Create personal access token"**
5. **Copy the token immediately**

### Azure DevOps

1. Open Azure DevOps and go to **User settings** → **Personal access tokens**
2. Click **"New Token"**
3. Configure the token:
   - **Name**: `GitSync`
   - **Organization**: choose the target organization
   - **Expiration**: set as desired
   - **Scopes**:
     - ✅ **Code**: Read, write, and manage
     - ✅ **Project and Team**: Read
4. Create the token
5. **Copy the token immediately**

---

## Configuration

All configuration is done via environment variables:

| Variable | Required | Description | Example |
|---|---|---|---|
| `PROVIDER_A_TYPE` | ✅ | Provider type | `github`, `gitea`, `gitlab`, `azuredevops` |
| `PROVIDER_A_URL` | For Gitea/GitLab/Azure DevOps | Base URL of the instance or Azure DevOps project URL | `https://gitea.example.com` |
| `PROVIDER_A_TOKEN` | ✅ | Access token | `ghp_xxxxxxxxxxxx` |
| `PROVIDER_A_USERNAME` | ✅ | Username on the provider. For Azure DevOps, any valid username/email for Git HTTPS auth | `myuser` |
| `PROVIDER_B_TYPE` | ✅ | Provider type | `github`, `gitea`, `gitlab`, `azuredevops` |
| `PROVIDER_B_URL` | For Gitea/GitLab/Azure DevOps | Base URL of the instance or Azure DevOps project URL | `https://gitlab.example.com` |
| `PROVIDER_B_TOKEN` | ✅ | Access token | `glpat-xxxxxxxxxxxx` |
| `PROVIDER_B_USERNAME` | ✅ | Username on the provider. For Azure DevOps, any valid username/email for Git HTTPS auth | `myuser` |
| `SYNC_INTERVAL_SECONDS` | No | Interval between syncs (default: `300`) | `600` |
| `REPOS_PATH` | No | Path to store bare repos (default: `/data/repos`) | `/tmp/repos` |
| `LOG_LEVEL` | No | Log level (default: `Information`) | `Debug` |

> **Note**: `PROVIDER_A_URL` is not required for GitHub since it always uses `api.github.com`. For Gitea and GitLab, provide the instance base URL. For Azure DevOps, provide the full project URL, for example `https://dev.azure.com/myorg/myproject`.
>
> **Azure DevOps visibility note**: repository visibility is inherited from the Azure DevOps project. GitSync cannot force a repository to be public or private independently inside Azure DevOps.

---

## Running with Docker Compose (Recommended)

1. Clone this repository:
   ```bash
   git clone https://github.com/your-user/gitsync.git
   cd gitsync
   ```

2. Edit `docker-compose.yml` with your configuration:
   ```yaml
   services:
     gitsync:
       build: .
       environment:
         - PROVIDER_A_TYPE=github
         - PROVIDER_A_TOKEN=ghp_your_github_token
         - PROVIDER_A_USERNAME=your_github_user
         - PROVIDER_B_TYPE=gitea
         - PROVIDER_B_URL=https://gitea.example.com
         - PROVIDER_B_TOKEN=your_gitea_token
         - PROVIDER_B_USERNAME=your_gitea_user
         - SYNC_INTERVAL_SECONDS=300
       volumes:
         - repos_data:/data/repos
       restart: unless-stopped
   
   volumes:
     repos_data:
   ```

3. Start the service:
   ```bash
   docker compose up -d
   ```

4. Check the logs:
   ```bash
   docker compose logs -f gitsync
   ```

5. Stop the service:
   ```bash
   docker compose down
   ```

---

## Running Locally

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Git](https://git-scm.com/) installed and available in PATH

### Steps

1. Clone and build:
   ```bash
   git clone https://github.com/your-user/gitsync.git
   cd gitsync
   dotnet build
   ```

2. Set environment variables:
   ```bash
   export PROVIDER_A_TYPE=github
   export PROVIDER_A_TOKEN=ghp_your_github_token
   export PROVIDER_A_USERNAME=your_github_user

   export PROVIDER_B_TYPE=gitea
   export PROVIDER_B_URL=https://gitea.example.com
   export PROVIDER_B_TOKEN=your_gitea_token
   export PROVIDER_B_USERNAME=your_gitea_user

   export SYNC_INTERVAL_SECONDS=300
   export REPOS_PATH=./data/repos
   ```

3. Run:
   ```bash
   dotnet run
   ```

4. Press `Ctrl+C` to stop gracefully.

---

## Examples

### GitHub ↔ Gitea
```bash
PROVIDER_A_TYPE=github
PROVIDER_A_TOKEN=ghp_abc123
PROVIDER_A_USERNAME=johndoe

PROVIDER_B_TYPE=gitea
PROVIDER_B_URL=https://git.myserver.com
PROVIDER_B_TOKEN=1234567890abcdef
PROVIDER_B_USERNAME=johndoe
```

### GitHub ↔ GitLab
```bash
PROVIDER_A_TYPE=github
PROVIDER_A_TOKEN=ghp_abc123
PROVIDER_A_USERNAME=johndoe

PROVIDER_B_TYPE=gitlab
PROVIDER_B_URL=https://gitlab.com
PROVIDER_B_TOKEN=glpat-xxxxxxxxxxxx
PROVIDER_B_USERNAME=johndoe
```

### Gitea ↔ GitLab
```bash
PROVIDER_A_TYPE=gitea
PROVIDER_A_URL=https://gitea.myserver.com
PROVIDER_A_TOKEN=abc123
PROVIDER_A_USERNAME=johndoe

PROVIDER_B_TYPE=gitlab
PROVIDER_B_URL=https://gitlab.myserver.com
PROVIDER_B_TOKEN=glpat-xxxxxxxxxxxx
PROVIDER_B_USERNAME=johndoe
```

### GitHub ↔ Azure DevOps
```bash
PROVIDER_A_TYPE=github
PROVIDER_A_TOKEN=ghp_abc123
PROVIDER_A_USERNAME=johndoe

PROVIDER_B_TYPE=azuredevops
PROVIDER_B_URL=https://dev.azure.com/myorg/myproject
PROVIDER_B_TOKEN=azdopatxxxxxxxx
PROVIDER_B_USERNAME=johndoe@company.com
```

---

## Adding a New Provider

To add support for a new git hosting platform:

1. Create a new class in `Providers/` implementing `IGitProvider`
2. Register it in the `CreateProvider` switch in `Program.cs`

```csharp
public class MyProvider : IGitProvider
{
    public string ProviderName => "MyProvider";
    
    public Task<List<RepositoryInfo>> GetRepositoriesAsync() { /* ... */ }
    public Task<RepositoryInfo> CreateRepositoryAsync(string name, string description, bool isPrivate) { /* ... */ }
    public string GetAuthenticatedCloneUrl(string repoName) { /* ... */ }
}
```

---

## License

MIT
