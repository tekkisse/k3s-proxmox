using System.Security.Claims;

namespace K3sProxmox.Web.Services;

public record CurrentUser(string Id, string DisplayName, bool IsAdmin)
{
    public const string AdminPolicy = "ClusterAdmin";

    public static CurrentUser From(ClaimsPrincipal principal, string adminRole) => new(
        principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user has no subject claim."),
        principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("name") ?? principal.Identity?.Name ?? "unknown",
        principal.IsInRole(adminRole));
}
