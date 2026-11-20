using System.Diagnostics;
using K3sProxmox.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace K3sProxmox.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Clusters");

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
