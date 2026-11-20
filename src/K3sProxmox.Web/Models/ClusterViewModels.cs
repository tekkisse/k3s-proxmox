using System.ComponentModel.DataAnnotations;
using K3sProxmox.Web.Data;
using K3sProxmox.Web.Options;

namespace K3sProxmox.Web.Models;

public class CreateClusterViewModel
{
    [Required, RegularExpression("^[a-z][a-z0-9-]{1,22}[a-z0-9]$",
        ErrorMessage = "3-24 characters: lowercase letters, digits and hyphens, starting with a letter.")]
    [Display(Name = "Cluster name")]
    public string Name { get; set; } = "";

    [StringLength(256)]
    public string? Description { get; set; }

    [Display(Name = "Control plane nodes")]
    [AllowedValues(1, 3, ErrorMessage = "Choose 1 (single) or 3 (highly available) control plane nodes.")]
    public int ControlPlaneCount { get; set; } = 1;

    [Display(Name = "Worker nodes"), Range(0, 100)]
    public int WorkerCount { get; set; } = 2;

    [Display(Name = "vCPU per node"), Range(1, 128)]
    public int Cpu { get; set; } = 4;

    [Display(Name = "Memory per node (GB)"), Range(2, 1024)]
    public int MemoryGb { get; set; } = 8;

    [Display(Name = "Disk per node (GB)"), Range(10, 4096)]
    public int DiskGb { get; set; } = 50;

    [Required, Display(Name = "k3s version")]
    public string K3sVersion { get; set; } = "";

    [Url, Display(Name = "Git repository URL (optional)")]
    [RegularExpression("^https://.+", ErrorMessage = "Use an https:// git URL.")]
    public string? GitRepoUrl { get; set; }

    [Display(Name = "Revision (branch, tag or commit)"), StringLength(128)]
    public string GitRevision { get; set; } = "HEAD";

    [Display(Name = "Path within repository"), StringLength(256)]
    public string GitPath { get; set; } = ".";

    [Display(Name = "Git username (private repos)"), StringLength(128)]
    public string? GitUsername { get; set; }

    [Display(Name = "Git password / access token"), DataType(DataType.Password), StringLength(512)]
    public string? GitPassword { get; set; }

    public List<string> SelectedStandardApps { get; set; } = [];

    // Form rendering only
    public IReadOnlyList<string> AvailableK3sVersions { get; set; } = [];
    public IReadOnlyList<StandardAppOptions> AvailableStandardApps { get; set; } = [];
    public string BaseDomain { get; set; } = "";
    public ClusterDefaultsOptions? Limits { get; set; }
}

public class ResizeClusterViewModel
{
    public Guid Id { get; set; }

    [Display(Name = "Worker nodes"), Range(0, 100)]
    public int WorkerCount { get; set; }
}

public class ClusterDetailsViewModel
{
    public required Cluster Cluster { get; init; }
    public required IReadOnlyList<ClusterOperation> Operations { get; init; }
    public required IReadOnlyList<StandardAppOptions> StandardApps { get; init; }
    public bool HasActiveOperation { get; init; }
    public bool CanRetry { get; init; }
    public int MaxWorkers { get; init; }
}
