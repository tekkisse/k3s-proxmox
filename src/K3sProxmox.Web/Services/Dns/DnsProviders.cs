using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using K3sProxmox.Web.Options;
using Microsoft.Extensions.Options;

namespace K3sProxmox.Web.Services.Dns;

public interface IDnsProvider
{
    string Name { get; }
    Task UpsertARecordAsync(string fqdn, string ip, CancellationToken ct);
    Task DeleteARecordAsync(string fqdn, CancellationToken ct);
}

/// <summary>Used when DNS is managed outside this system; it only logs what would be needed.</summary>
public class NoDnsProvider(ILogger<NoDnsProvider> logger) : IDnsProvider
{
    public string Name => "None";

    public Task UpsertARecordAsync(string fqdn, string ip, CancellationToken ct)
    {
        logger.LogInformation("DNS provider 'None': create A record {Fqdn} -> {Ip} manually", fqdn, ip);
        return Task.CompletedTask;
    }

    public Task DeleteARecordAsync(string fqdn, CancellationToken ct)
    {
        logger.LogInformation("DNS provider 'None': remove A record {Fqdn} manually", fqdn);
        return Task.CompletedTask;
    }
}

public class PowerDnsProvider(HttpClient http, IOptions<DnsOptions> options, ILogger<PowerDnsProvider> logger) : IDnsProvider
{
    private readonly DnsOptions _opt = options.Value;
    public string Name => "PowerDNS";

    private PowerDnsOptions P => _opt.PowerDns;
    private string ZoneName => Dot(_opt.EffectiveZone);
    private string ServerUrl => $"{P.ApiUrl.TrimEnd('/')}/api/v1/servers/{P.ServerId}";
    private string ZoneUrl => $"{ServerUrl}/zones/{ZoneName}";
    private string NameserverFqdn => Dot($"{P.NameserverName}.{_opt.EffectiveZone}");

    public Task UpsertARecordAsync(string fqdn, string ip, CancellationToken ct) =>
        PatchAsync([ARecord(fqdn, ip)], ct);

    public Task DeleteARecordAsync(string fqdn, CancellationToken ct) =>
        PatchAsync([new { name = Dot(fqdn), type = "A", changetype = "DELETE" }], ct);

    /// <summary>
    /// Creates the zone with SOA/NS when missing, and keeps the name server's A record (the glue target
    /// for the parent zone's delegation) in line with configuration. Safe to call repeatedly.
    /// </summary>
    public async Task EnsureZoneAsync(CancellationToken ct)
    {
        using (var get = Request(HttpMethod.Get, ZoneUrl))
        using (var resp = await http.SendAsync(get, ct))
        {
            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity)
            {
                logger.LogInformation("Creating PowerDNS zone {Zone} with name server {Ns}", ZoneName, NameserverFqdn);
                using var create = Request(HttpMethod.Post, $"{ServerUrl}/zones", new
                {
                    name = ZoneName,
                    kind = "Master",          // allows NOTIFY/AXFR to secondaries (e.g. AD DNS)
                    nameservers = new[] { NameserverFqdn },
                    soa_edit_api = "DEFAULT", // bump the SOA serial on every API change so secondaries refresh
                });
                using var created = await http.SendAsync(create, ct);
                await EnsureSuccessAsync(created, "zone creation", ct);
            }
            else
            {
                await EnsureSuccessAsync(resp, "zone lookup", ct);
            }
        }

        if (!string.IsNullOrWhiteSpace(P.NameserverIp))
            await PatchAsync([ARecord(NameserverFqdn, P.NameserverIp!)], ct, ensureZone: false);
        else
            logger.LogWarning("Dns:PowerDns:NameserverIp is not set; no A record for {Ns}. The parent zone's delegation needs glue for it.", NameserverFqdn);
    }

    private async Task PatchAsync(object[] rrsets, CancellationToken ct, bool ensureZone = true)
    {
        using (var req = Request(HttpMethod.Patch, ZoneUrl, new { rrsets }))
        using (var resp = await http.SendAsync(req, ct))
        {
            var missingZone = resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity
                              && (await resp.Content.ReadAsStringAsync(ct)).Contains("Could not find domain", StringComparison.OrdinalIgnoreCase);
            if (!(missingZone && ensureZone && P.AutoCreateZone))
            {
                await EnsureSuccessAsync(resp, "update", ct);
                return;
            }
        }

        await EnsureZoneAsync(ct);
        await PatchAsync(rrsets, ct, ensureZone: false);
    }

    private object ARecord(string fqdn, string ip) => new
    {
        name = Dot(fqdn), type = "A", ttl = _opt.Ttl, changetype = "REPLACE",
        records = new[] { new { content = ip, disabled = false } }
    };

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.Add("X-API-Key", P.ApiKey);
        return req;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PowerDNS {what} failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
    }

    private static string Dot(string name) => name.EndsWith('.') ? name : name + ".";
}

/// <summary>
/// When PowerDNS owns a delegated zone, make sure the zone exists at startup so the parent's delegation
/// (or AD conditional forwarder / secondary zone) works before the first cluster is created.
/// </summary>
public class DnsZoneBootstrapper(IServiceScopeFactory scopes, IOptions<DnsOptions> options, ILogger<DnsZoneBootstrapper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PowerDns.AutoCreateZone) return;

        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetRequiredService<IDnsProvider>() is not PowerDnsProvider pdns) return;
                await pdns.EnsureZoneAsync(stoppingToken);
                logger.LogInformation("DNS zone {Zone} is ready", options.Value.EffectiveZone);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("PowerDNS zone bootstrap attempt {Attempt} failed: {Error}", attempt, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}

public class CloudflareDnsProvider(HttpClient http, IOptions<DnsOptions> options) : IDnsProvider
{
    private readonly DnsOptions _opt = options.Value;
    public string Name => "Cloudflare";

    public async Task UpsertARecordAsync(string fqdn, string ip, CancellationToken ct)
    {
        var existing = await FindAsync(fqdn, ct);
        var body = new { type = "A", name = fqdn, content = ip, ttl = _opt.Ttl, proxied = false };
        using var req = existing is null
            ? new HttpRequestMessage(HttpMethod.Post, $"{Base}/dns_records")
            : new HttpRequestMessage(HttpMethod.Put, $"{Base}/dns_records/{existing}");
        req.Content = JsonContent.Create(body);
        await SendAsync(req, ct);
    }

    public async Task DeleteARecordAsync(string fqdn, CancellationToken ct)
    {
        var existing = await FindAsync(fqdn, ct);
        if (existing is null) return;
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/dns_records/{existing}");
        await SendAsync(req, ct);
    }

    private string Base => $"https://api.cloudflare.com/client/v4/zones/{_opt.Cloudflare.ZoneId}";

    private async Task<string?> FindAsync(string fqdn, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/dns_records?type=A&name={Uri.EscapeDataString(fqdn)}");
        var doc = await SendAsync(req, ct);
        var result = doc.RootElement.GetProperty("result");
        return result.GetArrayLength() > 0 ? result[0].GetProperty("id").GetString() : null;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.Cloudflare.ApiToken);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cloudflare DNS call failed: {(int)resp.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }
}
