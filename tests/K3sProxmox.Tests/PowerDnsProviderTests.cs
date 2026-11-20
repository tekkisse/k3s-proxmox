using System.Net;
using System.Text.Json;
using K3sProxmox.Web.Options;
using K3sProxmox.Web.Services.Dns;
using Microsoft.Extensions.Logging.Abstractions;

namespace K3sProxmox.Tests;

public class PowerDnsProviderTests
{
    private sealed class FakePowerDns(bool zoneExists) : HttpMessageHandler
    {
        public bool ZoneExists { get; private set; } = zoneExists;
        public List<(HttpMethod Method, string Path, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Assert.Equal("key", request.Headers.GetValues("X-API-Key").Single());
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));

            if (request.Method == HttpMethod.Post) { ZoneExists = true; return new HttpResponseMessage(HttpStatusCode.Created); }
            if (!ZoneExists)
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"Could not find domain 'tetredns.example.'"}""") };
            return new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.NoContent)
            { Content = new StringContent("{}") };
        }
    }

    private static (PowerDnsProvider, FakePowerDns) Create(bool zoneExists, bool autoCreate = true, string? nsIp = "192.0.2.53")
    {
        var fake = new FakePowerDns(zoneExists);
        var opts = Microsoft.Extensions.Options.Options.Create(new DnsOptions
        {
            BaseDomain = "tetredns.example",
            Zone = "tetredns.example",
            PowerDns = new PowerDnsOptions
            {
                ApiUrl = "http://pdns:8081", ApiKey = "key", AutoCreateZone = autoCreate, NameserverIp = nsIp,
            },
        });
        return (new PowerDnsProvider(new HttpClient(fake), opts, NullLogger<PowerDnsProvider>.Instance), fake);
    }

    [Fact]
    public async Task Missing_zone_is_created_with_ns_and_glue_before_first_record()
    {
        var (dns, fake) = Create(zoneExists: false);

        await dns.UpsertARecordAsync("*.team-dev.tetredns.example", "10.0.0.200", CancellationToken.None);

        // PATCH (404) -> GET zone (404) -> POST zone -> PATCH ns1 glue -> PATCH record again
        Assert.Equal(
            [HttpMethod.Patch, HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Patch],
            fake.Calls.Select(c => c.Method));

        var create = JsonDocument.Parse(fake.Calls[2].Body!).RootElement;
        Assert.Equal("tetredns.example.", create.GetProperty("name").GetString());
        Assert.Equal("Master", create.GetProperty("kind").GetString());
        Assert.Equal("ns1.tetredns.example.", create.GetProperty("nameservers")[0].GetString());

        Assert.Contains("\"ns1.tetredns.example.\"", fake.Calls[3].Body);
        Assert.Contains("192.0.2.53", fake.Calls[3].Body);
        Assert.Contains("\"*.team-dev.tetredns.example.\"", fake.Calls[4].Body);
    }

    [Fact]
    public async Task Existing_zone_is_patched_directly()
    {
        var (dns, fake) = Create(zoneExists: true);
        await dns.UpsertARecordAsync("api.team-dev.tetredns.example", "10.0.0.100", CancellationToken.None);
        Assert.Single(fake.Calls);
        Assert.Equal("/api/v1/servers/localhost/zones/tetredns.example.", fake.Calls[0].Path);
    }

    [Fact]
    public async Task Missing_zone_without_auto_create_fails()
    {
        var (dns, _) = Create(zoneExists: false, autoCreate: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dns.UpsertARecordAsync("api.x.tetredns.example", "10.0.0.1", CancellationToken.None));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Ensure_zone_is_idempotent_and_skips_glue_without_ip()
    {
        var (dns, fake) = Create(zoneExists: true, nsIp: null);
        await dns.EnsureZoneAsync(CancellationToken.None);
        Assert.Equal([HttpMethod.Get], fake.Calls.Select(c => c.Method));
    }
}
