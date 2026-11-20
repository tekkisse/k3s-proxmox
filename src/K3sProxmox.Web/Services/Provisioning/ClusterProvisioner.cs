using System.Security.Cryptography;
using K3sProxmox.Web.Data;
using K3sProxmox.Web.Options;
using K3sProxmox.Web.Services.Dns;
using K3sProxmox.Web.Services.Proxmox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace K3sProxmox.Web.Services.Provisioning;

/// <summary>
/// Executes cluster operations end to end: Proxmox VMs, k3s, Cilium, DNS, Argo CD and GitOps bootstrap.
/// Each step checks existing state first, so a failed operation can be retried.
/// </summary>
public class ClusterProvisioner(
    AppDbContext db,
    IDbContextFactory<AppDbContext> dbFactory,
    ProxmoxClient proxmox,
    ISshRunner ssh,
    IDnsProvider dns,
    IIpAllocator ipAllocator,
    ISecretProtector secrets,
    IOptions<ProxmoxOptions> proxmoxOptions,
    IOptions<NetworkOptions> networkOptions,
    IOptions<ClusterDefaultsOptions> clusterOptions,
    ILogger<ClusterProvisioner> logger)
{
    private readonly ProxmoxOptions _pve = proxmoxOptions.Value;
    private readonly NetworkOptions _net = networkOptions.Value;
    private readonly ClusterDefaultsOptions _defaults = clusterOptions.Value;

    private const int VmParallelism = 3;

    public async Task ExecuteAsync(Guid operationId, CancellationToken ct)
    {
        var op = await db.ClusterOperations.Include(o => o.Cluster!).ThenInclude(c => c.Nodes)
            .FirstAsync(o => o.Id == operationId, ct);
        var cluster = op.Cluster!;

        await using var log = new OperationLog(dbFactory, op.Id, logger);
        try
        {
            switch (op.Type)
            {
                case OperationType.Create:
                    await CreateAsync(cluster, log.Write, ct);
                    break;
                case OperationType.Resize:
                    await ResizeAsync(cluster, op.TargetWorkerCount ?? cluster.WorkerCount, log.Write, ct);
                    break;
                case OperationType.Delete:
                    await DeleteAsync(cluster, log.Write, ct);
                    break;
            }

            op.Status = OperationStatus.Succeeded;
            log.Write($"{op.Type} completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Operation {Op} ({Type}) for cluster {Cluster} failed", op.Id, op.Type, cluster.Name);
            log.Write($"FAILED: {ex.Message}");
            op.Status = OperationStatus.Failed;
            op.Error = Truncate(ex.Message, 2000);
            cluster.Status = ClusterStatus.Failed;
            foreach (var n in cluster.Nodes.Where(n => n.Status == NodeStatus.Provisioning)) n.Status = NodeStatus.Failed;
        }
        finally
        {
            op.CompletedAt = DateTimeOffset.UtcNow;
            cluster.UpdatedAt = DateTimeOffset.UtcNow;
            // Use CancellationToken.None so the final state is recorded even during shutdown.
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ create

    private async Task CreateAsync(Cluster cluster, Action<string> log, CancellationToken ct)
    {
        cluster.Status = ClusterStatus.Provisioning;
        await db.SaveChangesAsync(ct);

        await EnsureSecretsAsync(cluster, ct);

        if (cluster.IngressIp is null)
        {
            cluster.IngressIp = await ipAllocator.AllocateAsync(cluster.Id, IpPurpose.IngressVip, null, ct);
            log($"Allocated ingress VIP {cluster.IngressIp}");
        }

        // Desired node records
        for (var i = 0; i < cluster.ControlPlaneCount; i++) await EnsureNodeRecordAsync(cluster, NodeRole.Server, i, ct);
        for (var i = 0; i < cluster.WorkerCount; i++) await EnsureNodeRecordAsync(cluster, NodeRole.Agent, i, ct);
        await db.SaveChangesAsync(ct);

        var servers = cluster.Nodes.Where(n => n.Role == NodeRole.Server).OrderBy(n => n.Ordinal).ToList();
        var agents = cluster.Nodes.Where(n => n.Role == NodeRole.Agent).OrderBy(n => n.Ordinal).ToList();
        var first = servers[0];
        cluster.ApiAddress = first.IpAddress;

        await PublishDnsAsync(cluster, log, ct);
        await EnsureVmsAsync(cluster, cluster.Nodes, log, ct);

        var ctx = BuildContext(cluster);
        var allNodes = servers.Concat(agents).ToList();

        await ForEachParallelAsync(allNodes, async n =>
        {
            var target = Target(cluster, n);
            await ssh.WaitForSshAsync(target, TimeSpan.FromMinutes(10), log, ct);
            await ssh.RunScriptAsync(target, "Preparing node", K3sScripts.PrepareNode(), log, ct);
        }, ct);

        await ssh.RunScriptAsync(Target(cluster, first), "Installing k3s (first server)",
            K3sScripts.InstallFirstServer(ctx, first.Name, first.IpAddress!), log, ct);
        await ssh.RunScriptAsync(Target(cluster, first), "Installing Helm", K3sScripts.InstallHelm(ctx), log, ct);
        await ssh.RunScriptAsync(Target(cluster, first), "Installing Cilium", K3sScripts.InstallCilium(ctx), log, ct,
            timeout: TimeSpan.FromMinutes(25));

        // Additional servers join one at a time to keep etcd membership changes serial.
        foreach (var s in servers.Skip(1))
            await ssh.RunScriptAsync(Target(cluster, s), "Joining k3s server",
                K3sScripts.InstallAdditionalServer(ctx, s.Name, s.IpAddress!), log, ct);

        await ForEachParallelAsync(agents, n => ssh.RunScriptAsync(Target(cluster, n), "Joining k3s agent",
            K3sScripts.InstallAgent(ctx, n.Name, n.IpAddress!), log, ct), ct);

        await ssh.RunScriptAsync(Target(cluster, first), "Waiting for nodes to become Ready",
            K3sScripts.WaitForNodesReady(allNodes.Select(n => n.Name)), log, ct);
        foreach (var n in allNodes) n.Status = NodeStatus.Ready;
        await db.SaveChangesAsync(ct);

        // Argo CD with a generated admin password we keep for the user
        var argoPassword = secrets.Unprotect(cluster.ArgoCdPasswordProtected);
        if (argoPassword is null)
        {
            argoPassword = GeneratePassword(24);
            cluster.ArgoCdPasswordProtected = secrets.Protect(argoPassword);
            cluster.ArgoCdUsername = "admin";
            await db.SaveChangesAsync(ct);
        }
        await ssh.RunScriptAsync(Target(cluster, first), "Installing Argo CD",
            K3sScripts.InstallArgoCd(ctx, BCrypt.Net.BCrypt.HashPassword(argoPassword, 10)), log, ct,
            timeout: TimeSpan.FromMinutes(25));
        cluster.ArgoCdUrl = $"http://{ctx.ArgoCdHost}";

        await ApplyGitOpsAsync(cluster, ctx, log, ct);

        var raw = await ssh.RunScriptAsync(Target(cluster, first), "Fetching kubeconfig", K3sScripts.ReadKubeconfig(), log, ct,
            logOutput: false);
        var apiServer = dns is NoDnsProvider ? first.IpAddress! : ctx.ApiHost;
        cluster.KubeconfigProtected = secrets.Protect(K3sScripts.RewriteKubeconfig(raw, cluster.Name, apiServer));

        cluster.Status = ClusterStatus.Ready;
        log($"Cluster ready. API: https://{apiServer}:6443, Argo CD: {cluster.ArgoCdUrl}, ingress VIP: {cluster.IngressIp}");
    }

    private async Task ApplyGitOpsAsync(Cluster cluster, ClusterInstallContext ctx, Action<string> log, CancellationToken ct)
    {
        var standard = _defaults.StandardApps
            .Where(a => a.Required || cluster.StandardApps.Contains(a.Key))
            .ToList();
        var userRepo = string.IsNullOrWhiteSpace(cluster.GitRepoUrl)
            ? null
            : new GitSource(cluster.GitRepoUrl, cluster.GitRevision, cluster.GitPath, cluster.GitUsername,
                secrets.Unprotect(cluster.GitPasswordProtected));

        var names = standard.Select(a => a.Key).ToList();
        if (userRepo is not null) names.Insert(0, $"{ArgoManifests.UserAppName} ({userRepo.RepoUrl})");
        if (names.Count == 0)
        {
            log("No Git repository or platform apps selected; skipping Argo CD application registration");
            return;
        }
        log($"Registering Argo CD applications: {string.Join(", ", names)}");

        var manifest = ArgoManifests.Build(ctx, userRepo, standard);

        var first = cluster.Nodes.Single(n => n.Role == NodeRole.Server && n.Ordinal == 0);
        await ssh.RunScriptAsync(Target(cluster, first), "Applying Argo CD applications", K3sScripts.ApplyManifest(manifest), log, ct);
    }

    // ------------------------------------------------------------------ resize

    private async Task ResizeAsync(Cluster cluster, int targetWorkers, Action<string> log, CancellationToken ct)
    {
        cluster.Status = ClusterStatus.Resizing;
        await db.SaveChangesAsync(ct);

        var first = cluster.Nodes.Single(n => n.Role == NodeRole.Server && n.Ordinal == 0);
        static bool Leaving(ClusterNode n) => n.Status is NodeStatus.Draining or NodeStatus.Deleting;
        static bool NeedsProvisioning(ClusterNode n) => n.Status is NodeStatus.Provisioning or NodeStatus.Failed;

        var agents = cluster.Nodes.Where(n => n.Role == NodeRole.Agent).OrderBy(n => n.Ordinal).ToList();
        var staying = agents.Count(a => !Leaving(a));
        log($"Resizing workers: {staying} -> {targetWorkers}");

        // Scale up, or repair agents left half-provisioned by a failed attempt.
        if (targetWorkers > staying || agents.Any(NeedsProvisioning))
        {
            var nextOrdinal = agents.Count == 0 ? 0 : agents.Max(a => a.Ordinal) + 1;
            for (var i = staying; i < targetWorkers; i++)
                await EnsureNodeRecordAsync(cluster, NodeRole.Agent, nextOrdinal++, ct);
            await db.SaveChangesAsync(ct);

            var pending = cluster.Nodes.Where(n => n.Role == NodeRole.Agent && NeedsProvisioning(n)).ToList();
            await EnsureVmsAsync(cluster, pending, log, ct);

            var ctx = BuildContext(cluster);
            await ForEachParallelAsync(pending, async n =>
            {
                var target = Target(cluster, n);
                await ssh.WaitForSshAsync(target, TimeSpan.FromMinutes(10), log, ct);
                await ssh.RunScriptAsync(target, "Preparing node", K3sScripts.PrepareNode(), log, ct);
                await ssh.RunScriptAsync(target, "Joining k3s agent", K3sScripts.InstallAgent(ctx, n.Name, n.IpAddress!), log, ct);
            }, ct);

            await ssh.RunScriptAsync(Target(cluster, first), "Waiting for new nodes to become Ready",
                K3sScripts.WaitForNodesReady(pending.Select(n => n.Name)), log, ct);
            foreach (var n in pending) n.Status = NodeStatus.Ready;
            await db.SaveChangesAsync(ct);
        }

        // Scale down: nodes already on their way out first, then the newest agents.
        agents = cluster.Nodes.Where(n => n.Role == NodeRole.Agent)
            .OrderByDescending(Leaving).ThenByDescending(n => n.Ordinal).ToList();
        var toRemove = agents.Take(Math.Max(0, agents.Count - targetWorkers)).ToList();
        foreach (var n in toRemove)
        {
            n.Status = NodeStatus.Draining;
            await db.SaveChangesAsync(ct);
            await ssh.RunScriptAsync(Target(cluster, first), $"Draining and removing {n.Name}",
                K3sScripts.DrainAndDeleteNode(n.Name), log, ct, timeout: TimeSpan.FromMinutes(15));
            await RemoveNodeAsync(n, log, ct);
        }

        cluster.WorkerCount = targetWorkers;
        cluster.Status = ClusterStatus.Ready;
    }

    // ------------------------------------------------------------------ delete

    private async Task DeleteAsync(Cluster cluster, Action<string> log, CancellationToken ct)
    {
        cluster.Status = ClusterStatus.Deleting;
        await db.SaveChangesAsync(ct);

        foreach (var n in cluster.Nodes.OrderByDescending(n => n.Role).ThenByDescending(n => n.Ordinal).ToList())
            await RemoveNodeAsync(n, log, ct);

        foreach (var record in DnsRecords(cluster))
        {
            log($"Removing DNS record {record}");
            await dns.DeleteARecordAsync(record, ct);
        }

        await ipAllocator.ReleaseAllAsync(cluster.Id, ct);

        cluster.IngressIp = null;
        cluster.ApiAddress = null;
        cluster.KubeconfigProtected = null;
        cluster.ArgoCdPasswordProtected = null;
        cluster.ClusterTokenProtected = null;
        cluster.SshPrivateKeyProtected = null;
        cluster.GitPasswordProtected = null;
        cluster.Status = ClusterStatus.Deleted;
    }

    // ------------------------------------------------------------------ helpers

    private async Task EnsureSecretsAsync(Cluster cluster, CancellationToken ct)
    {
        if (cluster.ClusterTokenProtected is null)
            cluster.ClusterTokenProtected = secrets.Protect(GeneratePassword(48));

        if (cluster.SshPrivateKeyProtected is null)
        {
            var key = SshKeyGenerator.Generate($"k3s-proxmox-{cluster.Name}");
            cluster.SshPrivateKeyProtected = secrets.Protect(key.PrivateKeyPem);
            cluster.SshPublicKey = key.PublicKeyOpenSsh;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureNodeRecordAsync(Cluster cluster, NodeRole role, int ordinal, CancellationToken ct)
    {
        if (cluster.Nodes.Any(n => n.Role == role && n.Ordinal == ordinal)) return;

        var node = new ClusterNode
        {
            ClusterId = cluster.Id,
            Role = role,
            Ordinal = ordinal,
            Name = $"{cluster.Name}-{(role == NodeRole.Server ? "server" : "agent")}-{ordinal}",
        };
        node.IpAddress = await ipAllocator.AllocateAsync(cluster.Id, IpPurpose.Node, node.Id, ct);
        cluster.Nodes.Add(node);
        db.ClusterNodes.Add(node);
    }

    private async Task PublishDnsAsync(Cluster cluster, Action<string> log, CancellationToken ct)
    {
        var apiHost = $"api.{cluster.Fqdn}";
        log($"DNS ({dns.Name}): {apiHost} -> {cluster.ApiAddress}, *.{cluster.Fqdn} -> {cluster.IngressIp}");
        await dns.UpsertARecordAsync(apiHost, cluster.ApiAddress!, ct);
        await dns.UpsertARecordAsync($"*.{cluster.Fqdn}", cluster.IngressIp!, ct);
    }

    private static IEnumerable<string> DnsRecords(Cluster c) => [$"api.{c.Fqdn}", $"*.{c.Fqdn}"];

    /// <summary>Creates any VMs that are missing (or broken from a previous failed attempt), several at a time.</summary>
    private async Task EnsureVmsAsync(Cluster cluster, IEnumerable<ClusterNode> nodes, Action<string> log, CancellationToken ct)
    {
        var existing = (await proxmox.GetVmsAsync(ct)).ToDictionary(v => v.VmId);
        var toCreate = new List<ClusterNode>();

        foreach (var n in nodes)
        {
            if (n.VmId is { } id && existing.TryGetValue(id, out var vm))
            {
                if (vm.Status == "running") { n.ProxmoxNode = vm.Node; continue; }
                log($"VM {id} for {n.Name} exists but is {vm.Status}; recreating it");
                await proxmox.DestroyVmAsync(id, log, ct);
            }
            n.VmId = null;
            toCreate.Add(n);
        }

        // Placement decisions are made up front (sequentially) so that nodes are spread across hosts.
        var used = cluster.Nodes.Where(n => n.ProxmoxNode != null && !toCreate.Contains(n)).Select(n => n.ProxmoxNode!).ToList();
        foreach (var n in toCreate)
        {
            n.ProxmoxNode = await proxmox.PickNodeAsync(used, (long)cluster.NodeMemoryMb << 20, ct);
            used.Add(n.ProxmoxNode);
        }
        await db.SaveChangesAsync(ct);

        var pubKey = cluster.SshPublicKey!;
        await ForEachParallelAsync(toCreate, async n =>
        {
            var spec = new VmSpec(
                Name: n.Name,
                Cores: cluster.NodeCpu,
                MemoryMb: cluster.NodeMemoryMb,
                DiskGb: cluster.NodeDiskGb,
                IpCidr: $"{n.IpAddress}/{_net.PrefixLength}",
                Gateway: _net.Gateway,
                DnsServers: _net.DnsServers,
                SearchDomain: _net.SearchDomain,
                CiUser: _pve.CiUser,
                SshPublicKey: pubKey,
                Tags: $"k3s;{cluster.Name};{n.Role.ToString().ToLowerInvariant()}",
                Description: $"k3s {n.Role} for cluster {cluster.Name} (owner {cluster.OwnerName}). Managed by k3s-proxmox.");

            var nodeId = n.Id;
            n.VmId = await proxmox.CreateVmAsync(spec, n.ProxmoxNode!, log, ct, async vmId =>
            {
                // Persist the VMID as soon as it exists so a crash never orphans a VM.
                await using var side = await dbFactory.CreateDbContextAsync(ct);
                await side.ClusterNodes.Where(x => x.Id == nodeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.VmId, vmId), ct);
            });
        }, ct);

        // Tracked entities already carry the VMIDs; sync EF's snapshot with what the side-context wrote.
        await db.SaveChangesAsync(ct);
    }

    private async Task RemoveNodeAsync(ClusterNode n, Action<string> log, CancellationToken ct)
    {
        n.Status = NodeStatus.Deleting;
        await db.SaveChangesAsync(ct);

        if (n.VmId is { } vmId) await proxmox.DestroyVmAsync(vmId, log, ct);
        if (n.IpAddress is { } ip) await ipAllocator.ReleaseAsync(ip, ct);

        db.ClusterNodes.Remove(n);
        n.Cluster?.Nodes.Remove(n);
        await db.SaveChangesAsync(ct);
        log($"Removed node {n.Name}");
    }

    private ClusterInstallContext BuildContext(Cluster c)
    {
        var first = c.Nodes.Single(n => n.Role == NodeRole.Server && n.Ordinal == 0);
        return new ClusterInstallContext(
            ClusterName: c.Name,
            Fqdn: c.Fqdn,
            K3sVersion: c.K3sVersion,
            Token: secrets.Unprotect(c.ClusterTokenProtected)!,
            FirstServerIp: first.IpAddress!,
            IngressIp: c.IngressIp!,
            CiliumVersion: _defaults.CiliumVersion,
            ArgoCdChartVersion: _defaults.ArgoCdChartVersion,
            HelmInstallScriptUrl: _defaults.HelmInstallScriptUrl);
    }

    private SshTarget Target(Cluster c, ClusterNode n) =>
        new(n.IpAddress!, _pve.CiUser, secrets.Unprotect(c.SshPrivateKeyProtected)!);

    private static async Task ForEachParallelAsync<T>(IEnumerable<T> items, Func<T, Task> body, CancellationToken ct) =>
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = VmParallelism, CancellationToken = ct },
            async (item, _) => await body(item));

    public static string GeneratePassword(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
