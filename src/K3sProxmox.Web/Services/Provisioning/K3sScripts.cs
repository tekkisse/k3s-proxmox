using System.Text;

namespace K3sProxmox.Web.Services.Provisioning;

/// <summary>Everything the installer scripts need to know about the cluster.</summary>
public record ClusterInstallContext(
    string ClusterName,
    string Fqdn,
    string K3sVersion,
    string Token,
    string FirstServerIp,
    string IngressIp,
    string CiliumVersion,
    string ArgoCdChartVersion,
    string HelmInstallScriptUrl)
{
    public string ApiHost => $"api.{Fqdn}";
    public string ArgoCdHost => $"argocd.{Fqdn}";
}

/// <summary>
/// Bash scripts executed (as root) on the VMs. Each script is safe to run again, so failed
/// operations can be retried.
/// </summary>
public static class K3sScripts
{
    private const string Kubeconfig = "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml\nexport PATH=$PATH:/usr/local/bin\n";

    public static string PrepareNode() => """
        # Wait for cloud-init to finish (package installs, user creation, growpart)
        cloud-init status --wait >/dev/null 2>&1 || true
        swapoff -a || true
        sed -i '/\sswap\s/s/^/#/' /etc/fstab || true
        systemctl disable --now ufw >/dev/null 2>&1 || true
        # Cilium/eBPF friendly sysctls
        cat > /etc/sysctl.d/90-k3s.conf <<'EOF'
        net.ipv4.ip_forward = 1
        fs.inotify.max_user_instances = 1024
        fs.inotify.max_user_watches = 1048576
        EOF
        sysctl --system >/dev/null
        command -v curl >/dev/null || (apt-get update -qq && apt-get install -y -qq curl)
        echo "node prepared: $(hostname)"
        """;

    /// <summary>First control-plane node: initialises embedded etcd, no flannel/kube-proxy (Cilium replaces them).</summary>
    public static string InstallFirstServer(ClusterInstallContext c, string nodeName, string nodeIp) =>
        InstallServer(c, nodeName, nodeIp, joinExisting: false);

    public static string InstallAdditionalServer(ClusterInstallContext c, string nodeName, string nodeIp) =>
        InstallServer(c, nodeName, nodeIp, joinExisting: true);

    private static string InstallServer(ClusterInstallContext c, string nodeName, string nodeIp, bool joinExisting)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mkdir -p /etc/rancher/k3s");
        sb.AppendLine("umask 077");
        sb.AppendLine("cat > /etc/rancher/k3s/config.yaml <<'EOF'");
        if (joinExisting) sb.AppendLine($"server: \"https://{c.FirstServerIp}:6443\"");
        else sb.AppendLine("cluster-init: true");
        sb.AppendLine($"token: \"{c.Token}\"");
        sb.AppendLine($"node-name: \"{nodeName}\"");
        sb.AppendLine($"node-ip: \"{nodeIp}\"");
        sb.AppendLine("tls-san:");
        sb.AppendLine($"  - \"{c.ApiHost}\"");
        sb.AppendLine($"  - \"{nodeIp}\"");
        sb.AppendLine($"  - \"{c.FirstServerIp}\"");
        sb.AppendLine("flannel-backend: none");
        sb.AppendLine("disable-network-policy: true");
        sb.AppendLine("disable-kube-proxy: true");
        sb.AppendLine("disable:");
        sb.AppendLine("  - traefik");
        sb.AppendLine("  - servicelb");
        sb.AppendLine("write-kubeconfig-mode: \"0600\"");
        sb.AppendLine("EOF");
        sb.AppendLine($"curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION='{c.K3sVersion}' sh -s - server");
        sb.Append(Kubeconfig);
        sb.AppendLine("for i in $(seq 1 120); do kubectl get --raw=/readyz >/dev/null 2>&1 && break; sleep 5; done");
        sb.AppendLine("kubectl get --raw=/readyz");
        sb.AppendLine("echo; echo \"k3s server running on $(hostname)\"");
        return sb.ToString();
    }

    public static string InstallAgent(ClusterInstallContext c, string nodeName, string nodeIp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mkdir -p /etc/rancher/k3s");
        sb.AppendLine("umask 077");
        sb.AppendLine("cat > /etc/rancher/k3s/config.yaml <<'EOF'");
        sb.AppendLine($"server: \"https://{c.FirstServerIp}:6443\"");
        sb.AppendLine($"token: \"{c.Token}\"");
        sb.AppendLine($"node-name: \"{nodeName}\"");
        sb.AppendLine($"node-ip: \"{nodeIp}\"");
        sb.AppendLine("EOF");
        sb.AppendLine($"curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION='{c.K3sVersion}' sh -s - agent");
        sb.AppendLine("echo \"k3s agent running on $(hostname)\"");
        return sb.ToString();
    }

    public static string InstallHelm(ClusterInstallContext c) => $"""
        if ! command -v helm >/dev/null 2>&1; then
          curl -fsSL '{c.HelmInstallScriptUrl}' | bash
        fi
        helm version --short
        """;

    /// <summary>Installs Cilium (kube-proxy replacement, L2 LB announcements, ingress controller) and the ingress VIP pool.</summary>
    public static string InstallCilium(ClusterInstallContext c) => Kubeconfig + $$"""
        helm repo add cilium https://helm.cilium.io --force-update >/dev/null
        cat > /root/cilium-values.yaml <<'EOF'
        kubeProxyReplacement: true
        k8sServiceHost: "{{c.FirstServerIp}}"
        k8sServicePort: 6443
        ipam:
          mode: cluster-pool
          operator:
            clusterPoolIPv4PodCIDRList: ["10.42.0.0/16"]
        operator:
          replicas: 1
        l2announcements:
          enabled: true
        k8sClientRateLimit:
          qps: 20
          burst: 40
        externalIPs:
          enabled: true
        ingressController:
          enabled: true
          default: true
          loadbalancerMode: shared
          service:
            annotations:
              lbipam.cilium.io/ips: "{{c.IngressIp}}"
        hubble:
          relay:
            enabled: true
          ui:
            enabled: true
        EOF
        # No --wait: helm would block until the cilium-ingress LoadBalancer Service has an IP, but that IP
        # comes from the CiliumLoadBalancerIPPool created below (its CRD only exists once Cilium runs).
        helm upgrade --install cilium cilium/cilium --version '{{c.CiliumVersion}}' \
          --namespace kube-system -f /root/cilium-values.yaml --timeout 15m
        kubectl -n kube-system rollout status ds/cilium --timeout=15m
        kubectl -n kube-system rollout status deploy/cilium-operator --timeout=10m
        for i in $(seq 1 60); do
          kubectl get crd ciliumloadbalancerippools.cilium.io ciliuml2announcementpolicies.cilium.io >/dev/null 2>&1 && break
          sleep 5
        done
        kubectl wait --for condition=established --timeout=5m crd/ciliumloadbalancerippools.cilium.io crd/ciliuml2announcementpolicies.cilium.io
        cat <<'EOF' | kubectl apply -f -
        apiVersion: cilium.io/v2alpha1
        kind: CiliumLoadBalancerIPPool
        metadata:
          name: ingress-vip
        spec:
          blocks:
            - start: "{{c.IngressIp}}"
              stop: "{{c.IngressIp}}"
        ---
        apiVersion: cilium.io/v2alpha1
        kind: CiliumL2AnnouncementPolicy
        metadata:
          name: default-l2
        spec:
          loadBalancerIPs: true
          externalIPs: true
          interfaces:
            - ^eth[0-9]+
            - ^ens[0-9]+
            - ^enp[0-9s]+
        EOF
        for i in $(seq 1 60); do
          ip=$(kubectl -n kube-system get svc cilium-ingress -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || true)
          [ "$ip" = "{{c.IngressIp}}" ] && break
          sleep 5
        done
        kubectl -n kube-system get svc cilium-ingress
        echo "cilium installed"
        """;

    public static string WaitForNodesReady(IEnumerable<string> nodeNames) =>
        Kubeconfig + string.Join('\n', nodeNames.Select(n =>
            $"for i in $(seq 1 60); do kubectl get node '{n}' >/dev/null 2>&1 && break; sleep 5; done\n" +
            $"kubectl wait --for=condition=Ready 'node/{n}' --timeout=10m")) + "\nkubectl get nodes -o wide\n";

    public static string InstallArgoCd(ClusterInstallContext c, string adminPasswordBcrypt) => Kubeconfig + $$"""
        helm repo add argo https://argoproj.github.io/argo-helm --force-update >/dev/null
        umask 077
        cat > /root/argocd-values.yaml <<'EOF'
        global:
          domain: "{{c.ArgoCdHost}}"
        configs:
          params:
            server.insecure: true
          secret:
            argocdServerAdminPassword: '{{adminPasswordBcrypt}}'
            argocdServerAdminPasswordMtime: "{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}"
        server:
          ingress:
            enabled: true
            ingressClassName: cilium
        EOF
        helm upgrade --install argocd argo/argo-cd --version '{{c.ArgoCdChartVersion}}' \
          --namespace argocd --create-namespace -f /root/argocd-values.yaml --wait --timeout 15m
        kubectl -n argocd rollout status deploy/argocd-server --timeout=10m
        echo "argocd installed"
        """;

    /// <summary>Applies a manifest (multi-document YAML) on the first server.</summary>
    public static string ApplyManifest(string yaml) => Kubeconfig + $"""
        cat <<'__MANIFEST_EOF__' | kubectl apply -f -
        {yaml.TrimEnd()}
        __MANIFEST_EOF__
        """;

    public static string ReadKubeconfig() => "cat /etc/rancher/k3s/k3s.yaml";

    public static string DrainAndDeleteNode(string nodeName) => Kubeconfig + $"""
        if kubectl get node '{nodeName}' >/dev/null 2>&1; then
          kubectl cordon '{nodeName}'
          kubectl drain '{nodeName}' --ignore-daemonsets --delete-emptydir-data --force --timeout=10m || echo "drain timed out, continuing"
          kubectl delete node '{nodeName}' --wait=true
        else
          echo "node {nodeName} not registered, nothing to drain"
        fi
        """;

    /// <summary>Rewrites the k3s kubeconfig to point at the cluster's API host and names its entries after the cluster.</summary>
    public static string RewriteKubeconfig(string raw, string clusterName, string apiServer)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n').Select(l =>
        {
            var t = l.TrimStart();
            var indent = l[..(l.Length - t.Length)];
            if (t.StartsWith("server: https://127.0.0.1:6443")) return $"{indent}server: https://{apiServer}:6443";
            if (t == "name: default" || t == "- name: default") return l.Replace("default", clusterName);
            if (t == "cluster: default") return $"{indent}cluster: {clusterName}";
            if (t == "user: default") return $"{indent}user: {clusterName}";
            if (t == "current-context: default") return $"{indent}current-context: {clusterName}";
            return l;
        });
        return string.Join('\n', lines);
    }
}
