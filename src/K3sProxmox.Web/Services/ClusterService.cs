using K3sProxmox.Web.Data;
using K3sProxmox.Web.Models;
using K3sProxmox.Web.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace K3sProxmox.Web.Services;

public class ClusterRequestException(string message) : Exception(message);

/// <summary>Validates user requests and queues the corresponding operations for the background worker.</summary>
public class ClusterService(
    AppDbContext db,
    ISecretProtector secrets,
    IOptions<ClusterDefaultsOptions> clusterOptions,
    IOptions<DnsOptions> dnsOptions)
{
    private readonly ClusterDefaultsOptions _limits = clusterOptions.Value;
    private readonly DnsOptions _dns = dnsOptions.Value;

    public IQueryable<Cluster> VisibleTo(CurrentUser user) =>
        db.Clusters.Where(c => c.Status != ClusterStatus.Deleted && (user.IsAdmin || c.OwnerId == user.Id));

    public Task<Cluster?> FindAsync(Guid id, CurrentUser user, CancellationToken ct) =>
        VisibleTo(user).Include(c => c.Nodes).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<bool> HasActiveOperationAsync(Guid clusterId, CancellationToken ct) =>
        db.ClusterOperations.AnyAsync(o => o.ClusterId == clusterId &&
            (o.Status == OperationStatus.Queued || o.Status == OperationStatus.Running), ct);

    public async Task<Cluster> RequestCreateAsync(CreateClusterViewModel m, CurrentUser user, CancellationToken ct)
    {
        if (!_limits.K3sVersions.Contains(m.K3sVersion))
            throw new ClusterRequestException("Unsupported k3s version.");
        if (m.ControlPlaneCount + m.WorkerCount > _limits.MaxNodesPerCluster)
            throw new ClusterRequestException($"A cluster may have at most {_limits.MaxNodesPerCluster} nodes.");
        if (m.Cpu > _limits.MaxCpuPerNode)
            throw new ClusterRequestException($"At most {_limits.MaxCpuPerNode} vCPU per node.");
        if (m.MemoryGb > _limits.MaxMemoryGbPerNode)
            throw new ClusterRequestException($"At most {_limits.MaxMemoryGbPerNode} GB memory per node.");
        if (m.DiskGb < _limits.MinDiskGb || m.DiskGb > _limits.MaxDiskGb)
            throw new ClusterRequestException($"Disk must be between {_limits.MinDiskGb} and {_limits.MaxDiskGb} GB.");
        if (await db.Clusters.AnyAsync(c => c.Name == m.Name && c.Status != ClusterStatus.Deleted, ct))
            throw new ClusterRequestException($"A cluster named '{m.Name}' already exists.");

        if (!user.IsAdmin && _limits.MaxClustersPerUser > 0 &&
            await db.Clusters.CountAsync(c => c.OwnerId == user.Id && c.Status != ClusterStatus.Deleted, ct) >= _limits.MaxClustersPerUser)
            throw new ClusterRequestException($"You already own the maximum of {_limits.MaxClustersPerUser} clusters.");

        var knownApps = _limits.StandardApps.Select(a => a.Key).ToHashSet();
        var apps = m.SelectedStandardApps.Where(knownApps.Contains)
            .Union(_limits.StandardApps.Where(a => a.Required).Select(a => a.Key))
            .Distinct().ToList();

        var hasRepo = !string.IsNullOrWhiteSpace(m.GitRepoUrl);

        var cluster = new Cluster
        {
            Name = m.Name,
            Description = m.Description,
            OwnerId = user.Id,
            OwnerName = user.DisplayName,
            K3sVersion = m.K3sVersion,
            ControlPlaneCount = m.ControlPlaneCount,
            WorkerCount = m.WorkerCount,
            NodeCpu = m.Cpu,
            NodeMemoryMb = m.MemoryGb * 1024,
            NodeDiskGb = m.DiskGb,
            Fqdn = $"{m.Name}.{_dns.BaseDomain}",
            GitRepoUrl = hasRepo ? m.GitRepoUrl!.Trim() : null,
            GitRevision = string.IsNullOrWhiteSpace(m.GitRevision) ? "HEAD" : m.GitRevision.Trim(),
            GitPath = string.IsNullOrWhiteSpace(m.GitPath) ? "." : m.GitPath.Trim(),
            // Credentials are only kept when there is a repository to use them with.
            GitUsername = !hasRepo || string.IsNullOrWhiteSpace(m.GitUsername) ? null : m.GitUsername.Trim(),
            GitPasswordProtected = secrets.Protect(!hasRepo || string.IsNullOrWhiteSpace(m.GitPassword) ? null : m.GitPassword),
            StandardApps = apps,
        };

        db.Clusters.Add(cluster);
        db.ClusterOperations.Add(new ClusterOperation
        {
            ClusterId = cluster.Id,
            Type = OperationType.Create,
            RequestedBy = user.DisplayName,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ClusterRequestException($"A cluster named '{m.Name}' already exists.");
        }
        return cluster;
    }

    public async Task RequestResizeAsync(Guid id, int workers, CurrentUser user, CancellationToken ct)
    {
        var cluster = await FindAsync(id, user, ct) ?? throw new ClusterRequestException("Cluster not found.");
        if (cluster.Status != ClusterStatus.Ready)
            throw new ClusterRequestException("Only clusters in the Ready state can be resized.");
        if (workers < 0 || cluster.ControlPlaneCount + workers > _limits.MaxNodesPerCluster)
            throw new ClusterRequestException($"Worker count must be between 0 and {_limits.MaxNodesPerCluster - cluster.ControlPlaneCount}.");
        if (workers == cluster.WorkerCount)
            throw new ClusterRequestException("The cluster already has that many workers.");

        await EnqueueAsync(cluster, OperationType.Resize, workers, user, ct);
    }

    public async Task RequestDeleteAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var cluster = await FindAsync(id, user, ct) ?? throw new ClusterRequestException("Cluster not found.");
        await EnqueueAsync(cluster, OperationType.Delete, null, user, ct);
    }

    /// <summary>Re-queues the most recent failed operation; provisioning steps skip work that already completed.</summary>
    public async Task RequestRetryAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var cluster = await FindAsync(id, user, ct) ?? throw new ClusterRequestException("Cluster not found.");
        var last = await db.ClusterOperations.Where(o => o.ClusterId == id)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        if (cluster.Status != ClusterStatus.Failed || last is not { Status: OperationStatus.Failed })
            throw new ClusterRequestException("There is no failed operation to retry.");

        await EnqueueAsync(cluster, last.Type, last.TargetWorkerCount, user, ct);
    }

    private async Task EnqueueAsync(Cluster cluster, OperationType type, int? targetWorkers, CurrentUser user, CancellationToken ct)
    {
        if (await HasActiveOperationAsync(cluster.Id, ct))
            throw new ClusterRequestException("Another operation is already queued or running for this cluster.");

        db.ClusterOperations.Add(new ClusterOperation
        {
            ClusterId = cluster.Id,
            Type = type,
            TargetWorkerCount = targetWorkers,
            RequestedBy = user.DisplayName,
        });
        cluster.Status = type switch
        {
            OperationType.Delete => ClusterStatus.Deleting,
            OperationType.Resize => ClusterStatus.Resizing,
            _ => ClusterStatus.Pending,
        };
        cluster.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ClusterRequestException("The cluster was changed by someone else. Reload and try again.");
        }
    }
}
