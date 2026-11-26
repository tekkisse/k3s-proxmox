using System.Security.Claims;
using System.Text.Json;
using K3sProxmox.Web.Data;
using K3sProxmox.Web.Options;
using K3sProxmox.Web.Services;
using K3sProxmox.Web.Services.Dns;
using K3sProxmox.Web.Services.Provisioning;
using K3sProxmox.Web.Services.Proxmox;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- options
builder.Services.Configure<KeycloakOptions>(config.GetSection(KeycloakOptions.Section));
builder.Services.Configure<ProxmoxOptions>(config.GetSection(ProxmoxOptions.Section));
builder.Services.Configure<NetworkOptions>(config.GetSection(NetworkOptions.Section));
builder.Services.Configure<DnsOptions>(config.GetSection(DnsOptions.Section));
builder.Services.Configure<ClusterDefaultsOptions>(config.GetSection(ClusterDefaultsOptions.Section));

// ---------------------------------------------------------------- database
var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString), ServiceLifetime.Scoped);
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// Keys that encrypt stored secrets live in PostgreSQL so all secrets survive restarts/redeploys.
builder.Services.AddDataProtection()
    .SetApplicationName("k3s-proxmox")
    .PersistKeysToDbContext<AppDbContext>();

// ---------------------------------------------------------------- authentication (Keycloak OIDC)
var keycloak = config.GetSection(KeycloakOptions.Section).Get<KeycloakOptions>() ?? new KeycloakOptions();

builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "k3sp.auth";
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.AccessDeniedPath = "/Home/AccessDenied";
    })
    .AddOpenIdConnect(o =>
    {
        o.Authority = keycloak.Authority;
        o.ClientId = keycloak.ClientId;
        o.ClientSecret = keycloak.ClientSecret;
        o.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;
        if (!string.IsNullOrWhiteSpace(keycloak.MetadataAddress))
            o.MetadataAddress = keycloak.MetadataAddress;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;
        o.SaveTokens = true; // id_token is needed as id_token_hint for Keycloak logout
        o.GetClaimsFromUserInfoEndpoint = true;
        o.MapInboundClaims = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("email");
        if (!keycloak.RequireHttpsMetadata)
        {
            // Plain-HTTP deployments (e.g. http://<host-ip>:8081): the default correlation/nonce cookies are
            // SameSite=None + Secure, which browsers drop over HTTP on anything but localhost ("Correlation failed").
            // Lax cookies are only sent on a cross-site *GET* navigation, so the callback must be a redirect
            // (response_mode=query) rather than the default auto-submitted form_post: if the browser sees Keycloak
            // and the app as different sites (different hostname/IP, e.g. localhost vs 10.x), a POST callback
            // arrives without the correlation cookie.
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
        }
        o.TokenValidationParameters.NameClaimType = "preferred_username";
        o.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        o.Events.OnTokenValidated = ctx =>
        {
            // Keycloak puts roles in the access token (realm_access / resource_access); flatten them into role claims.
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            var accessToken = ctx.TokenEndpointResponse?.AccessToken;
            if (!string.IsNullOrEmpty(accessToken))
            {
                foreach (var role in KeycloakRoles.Extract(new JsonWebToken(accessToken), keycloak.ClientId))
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(o =>
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
    if (!string.IsNullOrWhiteSpace(keycloak.UserRole))
        policy.RequireAssertion(c => c.User.IsInRole(keycloak.UserRole!) || c.User.IsInRole(keycloak.AdminRole));
    o.FallbackPolicy = policy.Build();
    o.AddPolicy(CurrentUser.AdminPolicy, p => p.RequireRole(keycloak.AdminRole));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear(); // the app is expected to sit behind a trusted ingress/reverse proxy
    o.KnownProxies.Clear();
});

// ---------------------------------------------------------------- infrastructure clients
builder.Services.AddHttpClient<ProxmoxClient>((sp, http) =>
    {
        var pve = sp.GetRequiredService<IOptions<ProxmoxOptions>>().Value;
        http.BaseAddress = new Uri(pve.BaseUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={pve.TokenId}={pve.TokenSecret}");
        http.Timeout = TimeSpan.FromMinutes(2);
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var pve = sp.GetRequiredService<IOptions<ProxmoxOptions>>().Value;
        var handler = new HttpClientHandler();
        if (pve.IgnoreTlsErrors)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return handler;
    });

var dnsProvider = config.GetValue<string>($"{DnsOptions.Section}:Provider") ?? "None";
switch (dnsProvider.ToLowerInvariant())
{
    case "powerdns":
        builder.Services.AddHttpClient<IDnsProvider, PowerDnsProvider>();
        break;
    case "cloudflare":
        builder.Services.AddHttpClient<IDnsProvider, CloudflareDnsProvider>();
        break;
    case "none":
        builder.Services.AddSingleton<IDnsProvider, NoDnsProvider>();
        break;
    default:
        throw new InvalidOperationException($"Unknown DNS provider '{dnsProvider}'. Use None, PowerDns or Cloudflare.");
}

builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<ISshRunner, SshRunner>();
builder.Services.AddScoped<IIpAllocator, IpAllocator>();
builder.Services.AddScoped<ClusterService>();
builder.Services.AddScoped<ClusterProvisioner>();
builder.Services.AddSingleton<OperationCancellation>();
builder.Services.AddHostedService<OperationWorker>();
builder.Services.AddHostedService<DnsZoneBootstrapper>();

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();

// ---------------------------------------------------------------- database migration
if (config.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz").AllowAnonymous();

app.MapGet("/account/login", (string? returnUrl) =>
        Results.Challenge(new() { RedirectUri = string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ? "/" : returnUrl },
            [OpenIdConnectDefaults.AuthenticationScheme]))
    .AllowAnonymous();

app.MapPost("/account/logout", () =>
    Results.SignOut(new() { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

app.MapControllerRoute(name: "default", pattern: "{controller=Clusters}/{action=Index}/{id?}");

app.Run();

/// <summary>Extracts Keycloak realm roles and client roles for this application from an access token.</summary>
public static class KeycloakRoles
{
    public static IEnumerable<string> Extract(JsonWebToken token, string clientId)
    {
        if (token.TryGetPayloadValue<JsonElement>("realm_access", out var realm) &&
            realm.ValueKind == JsonValueKind.Object &&
            realm.TryGetProperty("roles", out var realmRoles))
        {
            foreach (var r in realmRoles.EnumerateArray())
                if (r.GetString() is { } s) yield return s;
        }

        if (token.TryGetPayloadValue<JsonElement>("resource_access", out var resources) &&
            resources.ValueKind == JsonValueKind.Object &&
            resources.TryGetProperty(clientId, out var client) &&
            client.TryGetProperty("roles", out var clientRoles))
        {
            foreach (var r in clientRoles.EnumerateArray())
                if (r.GetString() is { } s) yield return s;
        }
    }
}

public partial class Program;
