using K3sProxmox.Web.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace K3sProxmox.Web.Services.Provisioning;

public record GitSource(string RepoUrl, string Revision, string Path, string? Username, string? Password);

/// <summary>Builds the Argo CD resources (repository credentials and Applications) applied to new clusters.</summary>
public static class ArgoManifests
{
    public const string UserAppName = "workloads";

    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .Build();

    public static string Build(ClusterInstallContext ctx, GitSource userRepo, IEnumerable<StandardAppOptions> standardApps)
    {
        var docs = new List<object>();

        if (!string.IsNullOrEmpty(userRepo.Username) || !string.IsNullOrEmpty(userRepo.Password))
            docs.Add(RepoSecret("repo-workloads", userRepo.RepoUrl, userRepo.Username, userRepo.Password));

        docs.Add(UserApplication(userRepo));

        foreach (var app in standardApps)
            docs.Add(StandardApplication(ctx, app));

        return string.Join("---\n", docs.Select(d => Yaml.Serialize(d)));
    }

    private static Dictionary<string, object?> RepoSecret(string name, string url, string? username, string? password) => new()
    {
        ["apiVersion"] = "v1",
        ["kind"] = "Secret",
        ["metadata"] = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["namespace"] = "argocd",
            ["labels"] = new Dictionary<string, string> { ["argocd.argoproj.io/secret-type"] = "repository" },
        },
        ["type"] = "Opaque",
        ["stringData"] = new Dictionary<string, string?>
        {
            ["type"] = "git",
            ["url"] = url,
            ["username"] = username ?? "git",
            ["password"] = password,
        },
    };

    private static Dictionary<string, object?> UserApplication(GitSource repo) => Application(
        UserAppName,
        wave: 10,
        source: new Dictionary<string, object?>
        {
            ["repoURL"] = repo.RepoUrl,
            ["targetRevision"] = string.IsNullOrWhiteSpace(repo.Revision) ? "HEAD" : repo.Revision,
            ["path"] = string.IsNullOrWhiteSpace(repo.Path) ? "." : repo.Path,
            ["directory"] = new Dictionary<string, object?> { ["recurse"] = true },
        },
        destinationNamespace: "argocd",
        labels: new() { ["k3s-proxmox/kind"] = "workloads" });

    private static Dictionary<string, object?> StandardApplication(ClusterInstallContext ctx, StandardAppOptions app)
    {
        var source = new Dictionary<string, object?>
        {
            ["repoURL"] = app.RepoUrl,
            ["targetRevision"] = app.TargetRevision,
        };

        if (!string.IsNullOrWhiteSpace(app.Chart)) source["chart"] = app.Chart;
        else source["path"] = string.IsNullOrWhiteSpace(app.Path) ? "." : app.Path;

        if (!string.IsNullOrWhiteSpace(app.HelmValues))
            source["helm"] = new Dictionary<string, object?> { ["values"] = Substitute(app.HelmValues!, ctx) };

        return Application(app.Key, app.SyncWave, source, app.Namespace, new() { ["k3s-proxmox/kind"] = "platform" });
    }

    private static Dictionary<string, object?> Application(string name, int wave, Dictionary<string, object?> source,
        string destinationNamespace, Dictionary<string, string> labels) => new()
    {
        ["apiVersion"] = "argoproj.io/v1alpha1",
        ["kind"] = "Application",
        ["metadata"] = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["namespace"] = "argocd",
            ["labels"] = labels,
            ["annotations"] = new Dictionary<string, string> { ["argocd.argoproj.io/sync-wave"] = wave.ToString() },
            ["finalizers"] = new[] { "resources-finalizer.argocd.argoproj.io" },
        },
        ["spec"] = new Dictionary<string, object?>
        {
            ["project"] = "default",
            ["source"] = source,
            ["destination"] = new Dictionary<string, object?>
            {
                ["server"] = "https://kubernetes.default.svc",
                ["namespace"] = destinationNamespace,
            },
            ["syncPolicy"] = new Dictionary<string, object?>
            {
                ["automated"] = new Dictionary<string, object?> { ["prune"] = true, ["selfHeal"] = true },
                ["syncOptions"] = new[] { "CreateNamespace=true", "ServerSideApply=true" },
                ["retry"] = new Dictionary<string, object?>
                {
                    ["limit"] = 10,
                    ["backoff"] = new Dictionary<string, object?> { ["duration"] = "30s", ["factor"] = 2, ["maxDuration"] = "10m" },
                },
            },
        },
    };

    public static string Substitute(string template, ClusterInstallContext ctx) => template
        .Replace("{{clusterName}}", ctx.ClusterName)
        .Replace("{{clusterFqdn}}", ctx.Fqdn)
        .Replace("{{ingressIp}}", ctx.IngressIp)
        .Replace("{{apiAddress}}", ctx.FirstServerIp);
}
