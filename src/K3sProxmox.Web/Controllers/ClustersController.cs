using System.Text;
using K3sProxmox.Web.Data;
using K3sProxmox.Web.Models;
using K3sProxmox.Web.Options;
using K3sProxmox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace K3sProxmox.Web.Controllers;

public class ClustersController(
    ClusterService clusters,
    AppDbContext db,
    ISecretProtector secrets,
    IOptions<ClusterDefaultsOptions> clusterOptions,
    IOptions<DnsOptions> dnsOptions,
    IOptions<KeycloakOptions> keycloakOptions,
    ILogger<ClustersController> logger) : Controller
{
    private CurrentUser Me => CurrentUser.From(User, keycloakOptions.Value.AdminRole);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var list = await clusters.VisibleTo(Me)
            .Include(c => c.Nodes)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        ViewBag.IsAdmin = Me.IsAdmin;
        return View(list);
    }

    [HttpGet]
    public IActionResult Create()
    {
        var opts = clusterOptions.Value;
        var model = new CreateClusterViewModel
        {
            K3sVersion = opts.K3sVersions.FirstOrDefault() ?? "",
            SelectedStandardApps = opts.StandardApps.Where(a => a.DefaultEnabled || a.Required).Select(a => a.Key).ToList(),
        };
        return View(Populate(model));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateClusterViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(Populate(model));

        try
        {
            var cluster = await clusters.RequestCreateAsync(model, Me, ct);
            TempData["Message"] = $"Cluster '{cluster.Name}' has been queued for provisioning.";
            return RedirectToAction(nameof(Details), new { id = cluster.Id });
        }
        catch (ClusterRequestException ex)
        {
            ModelState.AddModelError("", ex.Message);
            model.GitPassword = null;
            return View(Populate(model));
        }
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var cluster = await clusters.FindAsync(id, Me, ct);
        if (cluster is null) return NotFound();

        var ops = await db.ClusterOperations.AsNoTracking()
            .Where(o => o.ClusterId == id).OrderByDescending(o => o.CreatedAt).Take(20).ToListAsync(ct);

        return View(new ClusterDetailsViewModel
        {
            Cluster = cluster,
            Operations = ops,
            StandardApps = clusterOptions.Value.StandardApps.Where(a => cluster.StandardApps.Contains(a.Key)).ToList(),
            HasActiveOperation = ops.Any(o => o.Status is OperationStatus.Queued or OperationStatus.Running),
            CanRetry = cluster.Status == ClusterStatus.Failed && ops.FirstOrDefault()?.Status == OperationStatus.Failed,
            MaxWorkers = clusterOptions.Value.MaxNodesPerCluster - cluster.ControlPlaneCount,
        });
    }

    /// <summary>Polled by the details page to show live progress.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(Guid id, CancellationToken ct)
    {
        var cluster = await clusters.FindAsync(id, Me, ct);
        if (cluster is null) return NotFound();

        var op = await db.ClusterOperations.AsNoTracking()
            .Where(o => o.ClusterId == id).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);

        return Json(new
        {
            status = cluster.Status.ToString(),
            nodes = cluster.Nodes.OrderBy(n => n.Role).ThenBy(n => n.Ordinal).Select(n => new
            {
                n.Name, role = n.Role.ToString(), status = n.Status.ToString(), n.IpAddress, n.VmId, n.ProxmoxNode,
            }),
            operation = op is null ? null : new
            {
                op.Id, type = op.Type.ToString(), status = op.Status.ToString(), op.Log, op.Error,
            },
        });
    }

    [HttpPost]
    public async Task<IActionResult> Resize(ResizeClusterViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Invalid worker count.";
            return RedirectToAction(nameof(Details), new { id = model.Id });
        }
        return await Run(model.Id, () => clusters.RequestResizeAsync(model.Id, model.WorkerCount, Me, ct),
            $"Resize to {model.WorkerCount} worker(s) queued.");
    }

    [HttpPost]
    public Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
        Run(id, () => clusters.RequestRetryAsync(id, Me, ct), "Retry queued.");

    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, string confirmName, CancellationToken ct)
    {
        var cluster = await clusters.FindAsync(id, Me, ct);
        if (cluster is null) return NotFound();
        if (!string.Equals(confirmName?.Trim(), cluster.Name, StringComparison.Ordinal))
        {
            TempData["Error"] = "Type the cluster name exactly to confirm deletion.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return await Run(id, () => clusters.RequestDeleteAsync(id, Me, ct), $"Deletion of '{cluster.Name}' queued.");
    }

    [HttpGet]
    public async Task<IActionResult> Kubeconfig(Guid id, CancellationToken ct)
    {
        var cluster = await clusters.FindAsync(id, Me, ct);
        var kubeconfig = secrets.Unprotect(cluster?.KubeconfigProtected);
        if (cluster is null || kubeconfig is null) return NotFound();

        logger.LogInformation("User {User} downloaded kubeconfig for cluster {Cluster}", Me.DisplayName, cluster.Name);
        Response.Headers.CacheControl = "no-store";
        return File(Encoding.UTF8.GetBytes(kubeconfig), "application/yaml", $"{cluster.Name}.kubeconfig.yaml");
    }

    /// <summary>Returns the Argo CD admin password on demand (not rendered into the page).</summary>
    [HttpPost]
    public async Task<IActionResult> ArgoCdPassword(Guid id, CancellationToken ct)
    {
        var cluster = await clusters.FindAsync(id, Me, ct);
        var password = secrets.Unprotect(cluster?.ArgoCdPasswordProtected);
        if (cluster is null || password is null) return NotFound();

        logger.LogInformation("User {User} revealed the Argo CD password for cluster {Cluster}", Me.DisplayName, cluster.Name);
        Response.Headers.CacheControl = "no-store";
        return Json(new { username = cluster.ArgoCdUsername, password });
    }

    private async Task<IActionResult> Run(Guid id, Func<Task> action, string success)
    {
        try
        {
            await action();
            TempData["Message"] = success;
        }
        catch (ClusterRequestException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private CreateClusterViewModel Populate(CreateClusterViewModel m)
    {
        var opts = clusterOptions.Value;
        m.AvailableK3sVersions = opts.K3sVersions;
        m.AvailableStandardApps = opts.StandardApps;
        m.BaseDomain = dnsOptions.Value.BaseDomain;
        m.Limits = opts;
        return m;
    }
}
