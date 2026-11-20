namespace K3sProxmox.Web.Options;

public class KeycloakOptions
{
    public const string Section = "Keycloak";

    /// <summary>e.g. https://keycloak.example.com/realms/platform</summary>
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Optional discovery document URL used for server-to-server calls, when the app reaches Keycloak on a
    /// different address than browsers do (e.g. http://keycloak:8080/... inside Docker). Defaults to Authority.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Realm or client role that may see and manage every cluster.</summary>
    public string AdminRole { get; set; } = "k3s-admin";

    /// <summary>If set, users must hold this role to use the application at all.</summary>
    public string? UserRole { get; set; }
}

public class ProxmoxOptions
{
    public const string Section = "Proxmox";

    /// <summary>e.g. https://pve1.example.com:8006</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API token id in the form user@realm!tokenname</summary>
    public string TokenId { get; set; } = "";
    public string TokenSecret { get; set; } = "";
    public bool IgnoreTlsErrors { get; set; }

    /// <summary>VMID of the cloud-init enabled template to clone.</summary>
    public int TemplateVmId { get; set; }

    /// <summary>Proxmox node that holds the template.</summary>
    public string TemplateNode { get; set; } = "";

    /// <summary>Candidate nodes to place VMs on. Empty = all online nodes.</summary>
    public List<string> TargetNodes { get; set; } = [];

    /// <summary>Storage for the cloned disks.</summary>
    public string Storage { get; set; } = "local-lvm";

    /// <summary>Disk device name of the template's boot disk (resized to the requested size).</summary>
    public string DiskDevice { get; set; } = "scsi0";

    public string Bridge { get; set; } = "vmbr0";
    public int? VlanTag { get; set; }

    /// <summary>Optional resource pool to place VMs in.</summary>
    public string? Pool { get; set; }

    /// <summary>VMID range used for cluster VMs.</summary>
    public int VmIdRangeStart { get; set; } = 5000;
    public int VmIdRangeEnd { get; set; } = 5999;

    /// <summary>Cloud-init default user (must have passwordless sudo, true for Ubuntu/Debian cloud images).</summary>
    public string CiUser { get; set; } = "ubuntu";

    public int TaskTimeoutSeconds { get; set; } = 900;
}

public class NetworkOptions
{
    public const string Section = "Network";

    /// <summary>Prefix length of the node network, e.g. 24.</summary>
    public int PrefixLength { get; set; } = 24;
    public string Gateway { get; set; } = "";
    public List<string> DnsServers { get; set; } = [];
    public string? SearchDomain { get; set; }

    /// <summary>Inclusive range of addresses handed to VMs.</summary>
    public string NodeRangeStart { get; set; } = "";
    public string NodeRangeEnd { get; set; } = "";

    /// <summary>Inclusive range of addresses used as ingress LoadBalancer VIPs (announced by Cilium L2).</summary>
    public string VipRangeStart { get; set; } = "";
    public string VipRangeEnd { get; set; } = "";
}

public class DnsOptions
{
    public const string Section = "Dns";

    /// <summary>None | PowerDns | Cloudflare</summary>
    public string Provider { get; set; } = "None";

    /// <summary>Parent domain; each cluster gets &lt;name&gt;.&lt;BaseDomain&gt;.</summary>
    public string BaseDomain { get; set; } = "k8s.example.com";

    /// <summary>DNS zone the records are written to (defaults to BaseDomain).</summary>
    public string? Zone { get; set; }

    public int Ttl { get; set; } = 300;

    public PowerDnsOptions PowerDns { get; set; } = new();
    public CloudflareOptions Cloudflare { get; set; } = new();

    public string EffectiveZone => string.IsNullOrWhiteSpace(Zone) ? BaseDomain : Zone!;
}

public class PowerDnsOptions
{
    /// <summary>e.g. http://pdns.example.com:8081</summary>
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ServerId { get; set; } = "localhost";

    /// <summary>
    /// Create the zone (SOA + NS) if it does not exist. Use this when PowerDNS is authoritative for a
    /// delegated sub-domain owned by this system (e.g. tetredns.chi.swan.ac.uk).
    /// </summary>
    public bool AutoCreateZone { get; set; }

    /// <summary>Name server host label inside the zone, e.g. "ns1" → ns1.&lt;zone&gt;.</summary>
    public string NameserverName { get; set; } = "ns1";

    /// <summary>
    /// Public/LAN IP on which PowerDNS answers port 53. Written as the A record for the name server
    /// (the parent zone needs the same IP as glue). Leave empty to skip the A record.
    /// </summary>
    public string? NameserverIp { get; set; }
}

public class CloudflareOptions
{
    public string ApiToken { get; set; } = "";
    public string ZoneId { get; set; } = "";
}

public class ClusterDefaultsOptions
{
    public const string Section = "Clusters";

    public List<string> K3sVersions { get; set; } = ["v1.33.5+k3s1"];
    public string CiliumVersion { get; set; } = "1.18.3";
    public string ArgoCdChartVersion { get; set; } = "9.1.0";

    /// <summary>Helm installer script, run on the first server.</summary>
    public string HelmInstallScriptUrl { get; set; } = "https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3";

    public int MaxNodesPerCluster { get; set; } = 20;
    public int MaxCpuPerNode { get; set; } = 16;
    public int MaxMemoryGbPerNode { get; set; } = 64;
    public int MinDiskGb { get; set; } = 20;
    public int MaxDiskGb { get; set; } = 500;

    /// <summary>Maximum clusters a non-admin user may own (0 = unlimited).</summary>
    public int MaxClustersPerUser { get; set; } = 5;

    /// <summary>How many cluster operations may run at the same time.</summary>
    public int MaxConcurrentOperations { get; set; } = 3;

    /// <summary>Catalog of standard platform applications deployed through Argo CD.</summary>
    public List<StandardAppOptions> StandardApps { get; set; } = [];
}

public class StandardAppOptions
{
    /// <summary>Stable key, also used as the Argo CD Application name.</summary>
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Pre-selected on the create form.</summary>
    public bool DefaultEnabled { get; set; } = true;

    /// <summary>Always installed; the user cannot deselect it.</summary>
    public bool Required { get; set; }

    public string RepoUrl { get; set; } = "";

    /// <summary>Set for Helm-repository sources. Leave empty for git sources.</summary>
    public string? Chart { get; set; }

    /// <summary>Git path (git sources only).</summary>
    public string? Path { get; set; }

    public string TargetRevision { get; set; } = "HEAD";
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Inline Helm values. Supports placeholders: {{clusterName}}, {{clusterFqdn}}, {{ingressIp}}, {{apiAddress}}.
    /// </summary>
    public string? HelmValues { get; set; }

    /// <summary>Argo CD sync wave, lower installs first (e.g. operators before their consumers).</summary>
    public int SyncWave { get; set; }
}
