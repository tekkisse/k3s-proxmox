using System.ComponentModel.DataAnnotations;

namespace K3sProxmox.Web.Data;

public enum ClusterStatus
{
    Pending,
    Provisioning,
    Ready,
    Resizing,
    Deleting,
    Deleted,
    Failed
}

public enum NodeRole
{
    Server,
    Agent
}

public enum NodeStatus
{
    Provisioning,
    Ready,
    Draining,
    Deleting,
    Failed
}

public enum OperationType
{
    Create,
    Resize,
    Delete
}

public enum OperationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed
}

public enum IpPurpose
{
    Node,
    IngressVip
}

public class Cluster
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(32)]
    public required string Name { get; set; }

    [MaxLength(256)]
    public string? Description { get; set; }

    /// <summary>Keycloak subject (sub claim) of the owner.</summary>
    [MaxLength(128)]
    public required string OwnerId { get; set; }

    [MaxLength(256)]
    public required string OwnerName { get; set; }

    public ClusterStatus Status { get; set; } = ClusterStatus.Pending;

    [MaxLength(64)]
    public required string K3sVersion { get; set; }

    public int ControlPlaneCount { get; set; } = 1;

    /// <summary>Desired number of worker (agent) nodes.</summary>
    public int WorkerCount { get; set; }

    public int NodeCpu { get; set; }
    public int NodeMemoryMb { get; set; }
    public int NodeDiskGb { get; set; }

    /// <summary>DNS zone this cluster lives in, e.g. "mycluster.k8s.example.com".</summary>
    [MaxLength(253)]
    public required string Fqdn { get; set; }

    [MaxLength(64)]
    public string? ApiAddress { get; set; }

    [MaxLength(64)]
    public string? IngressIp { get; set; }

    // --- GitOps (user's own products) ---
    /// <summary>Optional. When null, no "workloads" Argo CD application is created.</summary>
    [MaxLength(512)]
    public string? GitRepoUrl { get; set; }

    [MaxLength(128)]
    public string GitRevision { get; set; } = "HEAD";

    [MaxLength(256)]
    public string GitPath { get; set; } = ".";

    [MaxLength(128)]
    public string? GitUsername { get; set; }

    /// <summary>Protected (Data Protection API) git password or token.</summary>
    public string? GitPasswordProtected { get; set; }

    /// <summary>Keys of standard platform apps enabled on this cluster.</summary>
    public List<string> StandardApps { get; set; } = [];

    // --- Outputs / secrets (all protected at rest) ---
    public string? KubeconfigProtected { get; set; }

    [MaxLength(512)]
    public string? ArgoCdUrl { get; set; }

    [MaxLength(64)]
    public string? ArgoCdUsername { get; set; }

    public string? ArgoCdPasswordProtected { get; set; }

    public string? ClusterTokenProtected { get; set; }

    public string? SshPrivateKeyProtected { get; set; }

    public string? SshPublicKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (maps to PostgreSQL xmin).</summary>
    public uint Version { get; set; }

    public List<ClusterNode> Nodes { get; set; } = [];
    public List<ClusterOperation> Operations { get; set; } = [];
}

public class ClusterNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public Cluster? Cluster { get; set; }

    [MaxLength(63)]
    public required string Name { get; set; }

    public NodeRole Role { get; set; }

    /// <summary>Ordinal within the role, used for stable naming (server-0, agent-3 ...).</summary>
    public int Ordinal { get; set; }

    public int? VmId { get; set; }

    [MaxLength(64)]
    public string? ProxmoxNode { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public NodeStatus Status { get; set; } = NodeStatus.Provisioning;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClusterOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public Cluster? Cluster { get; set; }

    public OperationType Type { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Queued;

    /// <summary>For Resize: the requested worker count.</summary>
    public int? TargetWorkerCount { get; set; }

    [MaxLength(256)]
    public required string RequestedBy { get; set; }

    public string Log { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class IpAllocation
{
    [MaxLength(64)]
    public required string Address { get; set; }

    public Guid ClusterId { get; set; }
    public Cluster? Cluster { get; set; }

    public IpPurpose Purpose { get; set; }

    public Guid? NodeId { get; set; }

    public DateTimeOffset AllocatedAt { get; set; } = DateTimeOffset.UtcNow;
}
