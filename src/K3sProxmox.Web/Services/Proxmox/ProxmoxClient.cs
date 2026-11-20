using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using K3sProxmox.Web.Options;
using Microsoft.Extensions.Options;

namespace K3sProxmox.Web.Services.Proxmox;

public record ProxmoxNodeInfo(string Node, string Status, long MaxMem, long Mem, int MaxCpu, double Cpu)
{
    public long FreeMem => MaxMem - Mem;
}

public record ProxmoxVmInfo(int VmId, string Node, string Name, string Status);

public record VmSpec(
    string Name,
    int Cores,
    int MemoryMb,
    int DiskGb,
    string IpCidr,
    string Gateway,
    IReadOnlyList<string> DnsServers,
    string? SearchDomain,
    string CiUser,
    string SshPublicKey,
    string Tags,
    string Description);

public class ProxmoxApiException(string message) : Exception(message);

/// <summary>Thin client over the Proxmox VE REST API (API token authentication).</summary>
public partial class ProxmoxClient(HttpClient http, IOptions<ProxmoxOptions> options, ILogger<ProxmoxClient> logger)
{
    private readonly ProxmoxOptions _opt = options.Value;

    // Allocating a VMID and issuing the clone call must not interleave across concurrent operations.
    private static readonly SemaphoreSlim CloneLock = new(1, 1);

    public async Task<IReadOnlyList<ProxmoxNodeInfo>> GetNodesAsync(CancellationToken ct)
    {
        var data = await GetAsync("cluster/resources?type=node", ct);
        return data.EnumerateArray()
            .Select(n => new ProxmoxNodeInfo(
                n.GetProperty("node").GetString()!,
                n.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown",
                GetLong(n, "maxmem"), GetLong(n, "mem"),
                (int)GetLong(n, "maxcpu"),
                n.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0))
            .ToList();
    }

    public async Task<IReadOnlyList<ProxmoxVmInfo>> GetVmsAsync(CancellationToken ct)
    {
        var data = await GetAsync("cluster/resources?type=vm", ct);
        return data.EnumerateArray()
            .Where(v => v.TryGetProperty("vmid", out _))
            .Select(v => new ProxmoxVmInfo(
                (int)GetLong(v, "vmid"),
                v.GetProperty("node").GetString()!,
                v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                v.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ""))
            .ToList();
    }

    /// <summary>
    /// Chooses the Proxmox node for a new VM: online, in the allowed list, preferring nodes that host
    /// the fewest VMs of this cluster (spread) and then the most free memory.
    /// </summary>
    public async Task<string> PickNodeAsync(IReadOnlyCollection<string> nodesAlreadyUsedByCluster, long requiredMemBytes, CancellationToken ct)
    {
        var all = await GetNodesAsync(ct);
        var nodes = all
            .Where(n => n.Status == "online")
            .Where(n => _opt.TargetNodes.Count == 0 || _opt.TargetNodes.Contains(n.Node, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (nodes.Count == 0)
        {
            var seen = all.Count == 0
                ? "the API returned no nodes at all - the API token most likely lacks Sys.Audit on /nodes " +
                  "(a privilege-separated token only sees what it has been granted)"
                : "nodes visible to the token: " + string.Join(", ", all.Select(n => $"{n.Node} ({n.Status})"));
            var filter = _opt.TargetNodes.Count == 0 ? "" : $"; Proxmox:TargetNodes = [{string.Join(", ", _opt.TargetNodes)}]";
            throw new ProxmoxApiException($"No online Proxmox nodes are available for placement: {seen}{filter}.");
        }

        var best = nodes
            .OrderBy(n => nodesAlreadyUsedByCluster.Count(u => u == n.Node))
            .ThenByDescending(n => n.FreeMem)
            .First();

        if (best.FreeMem < requiredMemBytes)
            logger.LogWarning("Node {Node} has only {Free} MiB free for a {Req} MiB VM", best.Node, best.FreeMem >> 20, requiredMemBytes >> 20);

        return best.Node;
    }

    /// <summary>Clones the template onto <paramref name="targetNode"/>, configures cloud-init and starts the VM.</summary>
    public async Task<int> CreateVmAsync(VmSpec spec, string targetNode, Action<string> log, CancellationToken ct,
        Func<int, Task>? onVmIdAssigned = null)
    {
        int vmId;
        string upid;

        await CloneLock.WaitAsync(ct);
        try
        {
            vmId = await FindFreeVmIdAsync(ct);
            log($"Cloning template {_opt.TemplateVmId} -> VM {vmId} ({spec.Name}) on {targetNode}");

            var form = new Dictionary<string, string>
            {
                ["newid"] = vmId.ToString(CultureInfo.InvariantCulture),
                ["name"] = spec.Name,
                ["full"] = "1",
                ["storage"] = _opt.Storage,
                ["description"] = spec.Description,
            };
            if (!string.Equals(targetNode, _opt.TemplateNode, StringComparison.OrdinalIgnoreCase))
                form["target"] = targetNode;
            if (!string.IsNullOrWhiteSpace(_opt.Pool))
                form["pool"] = _opt.Pool!;

            upid = (await PostAsync($"nodes/{_opt.TemplateNode}/qemu/{_opt.TemplateVmId}/clone", form, ct)).GetString()!;
        }
        finally
        {
            CloneLock.Release();
        }

        if (onVmIdAssigned is not null) await onVmIdAssigned(vmId);

        await WaitForTaskAsync(_opt.TemplateNode, upid, ct);

        var net = $"virtio,bridge={_opt.Bridge}";
        if (_opt.VlanTag is { } tag) net += $",tag={tag}";

        var config = new Dictionary<string, string>
        {
            ["cores"] = spec.Cores.ToString(CultureInfo.InvariantCulture),
            ["memory"] = spec.MemoryMb.ToString(CultureInfo.InvariantCulture),
            ["net0"] = net,
            ["ipconfig0"] = $"ip={spec.IpCidr},gw={spec.Gateway}",
            ["ciuser"] = spec.CiUser,
            // Proxmox expects the sshkeys value itself to be URL-encoded (spaces as %20); the form encoding is applied on top.
            ["sshkeys"] = Uri.EscapeDataString(spec.SshPublicKey.Trim() + "\n"),
            ["agent"] = "1",
            ["onboot"] = "1",
            ["tags"] = spec.Tags,
        };
        var nameservers = spec.DnsServers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (nameservers.Count > 0) config["nameserver"] = string.Join(' ', nameservers);
        if (!string.IsNullOrWhiteSpace(spec.SearchDomain)) config["searchdomain"] = spec.SearchDomain!;

        log($"Configuring VM {vmId}: {spec.Cores} vCPU, {spec.MemoryMb} MiB, {spec.IpCidr}");
        var cfgResult = await PostAsync($"nodes/{targetNode}/qemu/{vmId}/config", config, ct);
        if (cfgResult.ValueKind == JsonValueKind.String && cfgResult.GetString() is { } cfgUpid && cfgUpid.StartsWith("UPID:"))
            await WaitForTaskAsync(targetNode, cfgUpid, ct);

        await EnsureDiskSizeAsync(targetNode, vmId, spec.DiskGb, log, ct);

        log($"Starting VM {vmId}");
        var startUpid = (await PostAsync($"nodes/{targetNode}/qemu/{vmId}/status/start", new(), ct)).GetString()!;
        await WaitForTaskAsync(targetNode, startUpid, ct);

        return vmId;
    }

    /// <summary>Stops and destroys a VM wherever it currently lives. Missing VMs are ignored.</summary>
    public async Task DestroyVmAsync(int vmId, Action<string> log, CancellationToken ct)
    {
        var vm = (await GetVmsAsync(ct)).FirstOrDefault(v => v.VmId == vmId);
        if (vm is null)
        {
            log($"VM {vmId} not found on Proxmox, nothing to destroy");
            return;
        }

        if (vm.Status == "running")
        {
            log($"Stopping VM {vmId} on {vm.Node}");
            var stop = (await PostAsync($"nodes/{vm.Node}/qemu/{vmId}/status/stop", new(), ct)).GetString()!;
            await WaitForTaskAsync(vm.Node, stop, ct);
        }

        log($"Destroying VM {vmId} on {vm.Node}");
        var del = (await SendAsync(HttpMethod.Delete, $"nodes/{vm.Node}/qemu/{vmId}?purge=1&destroy-unreferenced-disks=1", null, ct)).GetString()!;
        await WaitForTaskAsync(vm.Node, del, ct);
    }

    private async Task EnsureDiskSizeAsync(string node, int vmId, int diskGb, Action<string> log, CancellationToken ct)
    {
        var cfg = await GetAsync($"nodes/{node}/qemu/{vmId}/config", ct);
        if (!cfg.TryGetProperty(_opt.DiskDevice, out var disk))
            throw new ProxmoxApiException($"Template has no disk '{_opt.DiskDevice}'. Set Proxmox:DiskDevice.");

        var current = ParseSizeGb(disk.GetString() ?? "");
        if (current >= diskGb)
        {
            log($"Disk {_opt.DiskDevice} is {current:0.#}G (requested {diskGb}G), not resizing");
            return;
        }

        log($"Resizing disk {_opt.DiskDevice} to {diskGb}G");
        var result = await SendAsync(HttpMethod.Put, $"nodes/{node}/qemu/{vmId}/resize",
            new Dictionary<string, string> { ["disk"] = _opt.DiskDevice, ["size"] = $"{diskGb}G" }, ct);
        if (result.ValueKind == JsonValueKind.String && result.GetString() is { } upid && upid.StartsWith("UPID:"))
            await WaitForTaskAsync(node, upid, ct);
    }

    public static double ParseSizeGb(string diskSpec)
    {
        var m = SizeRegex().Match(diskSpec);
        if (!m.Success) return 0;
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "T" => value * 1024,
            "G" => value,
            "M" => value / 1024,
            "K" => value / 1024 / 1024,
            _ => value
        };
    }

    [GeneratedRegex(@"size=(\d+(?:\.\d+)?)([KMGT])", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    private async Task<int> FindFreeVmIdAsync(CancellationToken ct)
    {
        var used = (await GetVmsAsync(ct)).Select(v => v.VmId).ToHashSet();
        for (var id = _opt.VmIdRangeStart; id <= _opt.VmIdRangeEnd; id++)
            if (!used.Contains(id)) return id;
        throw new ProxmoxApiException($"No free VMID in range {_opt.VmIdRangeStart}-{_opt.VmIdRangeEnd}.");
    }

    public async Task WaitForTaskAsync(string node, string upid, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_opt.TaskTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var status = await GetAsync($"nodes/{node}/tasks/{Uri.EscapeDataString(upid)}/status", ct);
            if (status.GetProperty("status").GetString() == "stopped")
            {
                var exit = status.TryGetProperty("exitstatus", out var e) ? e.GetString() : null;
                if (exit != "OK" && !(exit?.StartsWith("WARNINGS") ?? false))
                    throw new ProxmoxApiException($"Proxmox task {upid} failed: {exit}");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException($"Proxmox task {upid} did not finish within {_opt.TaskTimeoutSeconds}s.");
    }

    private Task<JsonElement> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    private Task<JsonElement> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, form, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, "api2/json/" + path);
        if (form is not null) req.Content = new FormUrlEncodedContent(form);

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ProxmoxApiException($"Proxmox {method} {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {body}");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
