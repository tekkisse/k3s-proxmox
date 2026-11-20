using System.Text;
using K3sProxmox.Web.Options;
using K3sProxmox.Web.Services;
using K3sProxmox.Web.Services.Provisioning;
using K3sProxmox.Web.Services.Proxmox;
using Microsoft.IdentityModel.JsonWebTokens;
using Renci.SshNet;
using YamlDotNet.RepresentationModel;

namespace K3sProxmox.Tests;

public class ProvisioningTests
{
    private static readonly ClusterInstallContext Ctx = new(
        ClusterName: "team-dev",
        Fqdn: "team-dev.k8s.example.com",
        K3sVersion: "v1.33.5+k3s1",
        Token: "secret-token",
        FirstServerIp: "10.20.0.100",
        IngressIp: "10.20.0.200",
        CiliumVersion: "1.18.3",
        ArgoCdChartVersion: "9.1.0",
        HelmInstallScriptUrl: "https://example.com/get-helm");

    [Fact]
    public void Kubeconfig_is_rewritten_to_cluster_api_and_names()
    {
        const string raw = """
            apiVersion: v1
            clusters:
            - cluster:
                certificate-authority-data: AAA
                server: https://127.0.0.1:6443
              name: default
            contexts:
            - context:
                cluster: default
                user: default
              name: default
            current-context: default
            kind: Config
            users:
            - name: default
              user:
                client-certificate-data: BBB
            """;

        var result = K3sScripts.RewriteKubeconfig(raw, "team-dev", "api.team-dev.k8s.example.com");

        Assert.Contains("server: https://api.team-dev.k8s.example.com:6443", result);
        Assert.Contains("current-context: team-dev", result);
        Assert.DoesNotContain("default", result);

        // Still valid YAML with the expected structure
        var yaml = new YamlStream();
        yaml.Load(new StringReader(result));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var ctx = (YamlMappingNode)((YamlSequenceNode)root["contexts"])[0];
        Assert.Equal("team-dev", ((YamlScalarNode)ctx["name"]).Value);
    }

    [Fact]
    public void Ip_range_enumerates_inclusively_across_octets()
    {
        var ips = IpAllocator.Enumerate("10.0.0.254", "10.0.1.1").ToList();
        Assert.Equal(["10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1"], ips);
    }

    [Fact]
    public void Ip_range_rejects_reversed_range() =>
        Assert.Throws<ArgumentException>(() => IpAllocator.Enumerate("10.0.0.5", "10.0.0.1").ToList());

    [Fact]
    public void Generated_ssh_key_is_loadable_by_sshnet_and_public_key_is_openssh_format()
    {
        var pair = SshKeyGenerator.Generate("test", bits: 2048);

        var key = new PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivateKeyPem)));
        Assert.NotNull(key.Key);

        var parts = pair.PublicKeyOpenSsh.Split(' ');
        Assert.Equal("ssh-rsa", parts[0]);
        Assert.Equal("test", parts[2]);
        var blob = Convert.FromBase64String(parts[1]);
        Assert.Equal("ssh-rsa", Encoding.ASCII.GetString(blob, 4, 7));
    }

    [Theory]
    [InlineData("ceph-vm:vm-9000-disk-0,discard=on,size=3584M", 3.5)]
    [InlineData("local-lvm:vm-100-disk-0,size=32G", 32)]
    [InlineData("local-lvm:vm-100-disk-0,iothread=1,size=1T", 1024)]
    [InlineData("none", 0)]
    public void Proxmox_disk_size_is_parsed(string spec, double expectedGb) =>
        Assert.Equal(expectedGb, ProxmoxClient.ParseSizeGb(spec), 3);

    [Fact]
    public void Argo_manifests_contain_repo_secret_user_app_and_standard_apps()
    {
        var apps = new[]
        {
            new StandardAppOptions
            {
                Key = "monitoring", RepoUrl = "https://charts.example.com", Chart = "kube-prometheus-stack",
                TargetRevision = "1.0.0", Namespace = "monitoring", SyncWave = 0,
                HelmValues = "grafana:\n  host: grafana.{{clusterFqdn}}\n",
            },
            new StandardAppOptions
            {
                Key = "launch-control", RepoUrl = "https://git.example.com/lc.git", Path = "deploy",
                TargetRevision = "main", Namespace = "launch-control", SyncWave = 5,
            },
        };

        var yaml = ArgoManifests.Build(Ctx,
            new GitSource("https://git.example.com/team/apps.git", "main", "clusters/dev", "bot", "p@ss: 'word\""),
            apps);

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        Assert.Equal(4, stream.Documents.Count);

        var secret = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal("Secret", secret["kind"].ToString());
        Assert.Equal("p@ss: 'word\"", ((YamlMappingNode)secret["stringData"])["password"].ToString());

        var user = (YamlMappingNode)stream.Documents[1].RootNode;
        var userSource = (YamlMappingNode)((YamlMappingNode)user["spec"])["source"];
        Assert.Equal("clusters/dev", userSource["path"].ToString());
        Assert.Equal("main", userSource["targetRevision"].ToString());

        var monitoring = (YamlMappingNode)stream.Documents[2].RootNode;
        var monSource = (YamlMappingNode)((YamlMappingNode)monitoring["spec"])["source"];
        Assert.Equal("kube-prometheus-stack", monSource["chart"].ToString());
        Assert.Contains("grafana.team-dev.k8s.example.com", ((YamlMappingNode)monSource["helm"])["values"].ToString());

        var lc = (YamlMappingNode)stream.Documents[3].RootNode;
        var lcSource = (YamlMappingNode)((YamlMappingNode)lc["spec"])["source"];
        Assert.Equal("deploy", lcSource["path"].ToString());
        Assert.False(lcSource.Children.ContainsKey("chart"));
    }

    [Fact]
    public void Argo_manifests_skip_repo_secret_for_public_repos()
    {
        var yaml = ArgoManifests.Build(Ctx, new GitSource("https://github.com/org/apps.git", "HEAD", ".", null, null), []);
        Assert.DoesNotContain("kind: Secret", yaml);
        Assert.Contains("name: workloads", yaml);
    }

    [Fact]
    public void Server_script_disables_flannel_and_kube_proxy_for_cilium()
    {
        var script = K3sScripts.InstallFirstServer(Ctx, "team-dev-server-0", "10.20.0.100");
        Assert.Contains("cluster-init: true", script);
        Assert.Contains("flannel-backend: none", script);
        Assert.Contains("disable-kube-proxy: true", script);
        Assert.Contains("api.team-dev.k8s.example.com", script);
        Assert.Contains("INSTALL_K3S_VERSION='v1.33.5+k3s1'", script);
    }

    [Fact]
    public void Agent_script_joins_first_server()
    {
        var script = K3sScripts.InstallAgent(Ctx, "team-dev-agent-0", "10.20.0.101");
        Assert.Contains("server: \"https://10.20.0.100:6443\"", script);
        Assert.Contains("sh -s - agent", script);
    }

    [Fact]
    public void Cilium_script_pins_ingress_vip()
    {
        var script = K3sScripts.InstallCilium(Ctx);
        Assert.Contains("lbipam.cilium.io/ips: \"10.20.0.200\"", script);
        Assert.Contains("start: \"10.20.0.200\"", script);
        Assert.Contains("--version '1.18.3'", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void Keycloak_realm_and_client_roles_are_extracted()
    {
        var header = B64("""{"alg":"none","typ":"JWT"}""");
        var payload = B64("""
            {"sub":"u1","realm_access":{"roles":["k3s-user","offline_access"]},
             "resource_access":{"k3s-proxmox":{"roles":["k3s-admin"]},"other":{"roles":["x"]}}}
            """);
        var token = new JsonWebToken($"{header}.{payload}.");

        var roles = KeycloakRoles.Extract(token, "k3s-proxmox").ToList();

        Assert.Contains("k3s-user", roles);
        Assert.Contains("k3s-admin", roles);
        Assert.DoesNotContain("x", roles);
    }

    [Fact]
    public void Generated_passwords_have_requested_length_and_are_unique()
    {
        var a = ClusterProvisioner.GeneratePassword(24);
        var b = ClusterProvisioner.GeneratePassword(24);
        Assert.Equal(24, a.Length);
        Assert.NotEqual(a, b);
    }

    private static string B64(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
