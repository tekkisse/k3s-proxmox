using System.Diagnostics;
using K3sProxmox.Web.Options;
using K3sProxmox.Web.Services.Provisioning;

namespace K3sProxmox.Tests;

/// <summary>Runs `bash -n` over every generated script to catch quoting/heredoc mistakes.</summary>
public class ScriptSyntaxTests
{
    private static readonly ClusterInstallContext Ctx = new("team-dev", "team-dev.k8s.example.com", "v1.33.5+k3s1",
        "tok", "10.20.0.100", "10.20.0.200", "1.18.3", "9.1.0", "https://example.com/get-helm");

    public static TheoryData<string> Scripts() => new()
    {
        "prepare", "server0", "server1", "agent", "helm", "cilium", "wait", "argocd", "apply", "drain",
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_is_valid_bash(string name)
    {
        var script = name switch
        {
            "prepare" => K3sScripts.PrepareNode(),
            "server0" => K3sScripts.InstallFirstServer(Ctx, "n0", "10.20.0.100"),
            "server1" => K3sScripts.InstallAdditionalServer(Ctx, "n1", "10.20.0.101"),
            "agent" => K3sScripts.InstallAgent(Ctx, "a0", "10.20.0.102"),
            "helm" => K3sScripts.InstallHelm(Ctx),
            "cilium" => K3sScripts.InstallCilium(Ctx),
            "wait" => K3sScripts.WaitForNodesReady(["n0", "a0"]),
            "argocd" => K3sScripts.InstallArgoCd(Ctx, "$2a$10$abcdefghijklmnopqrstuuABCDEFGHIJKLMNOPQRSTUVWXYZ01234"),
            "apply" => K3sScripts.ApplyManifest(ArgoManifests.Build(Ctx,
                new GitSource("https://git.example.com/a.git", "main", ".", "u", "p"),
                [new StandardAppOptions { Key = "x", RepoUrl = "https://c.example.com", Chart = "x", HelmValues = "a: 1\n" }])),
            "drain" => K3sScripts.DrainAndDeleteNode("a0"),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        var bash = FindBash();
        if (bash is null) return; // bash not available on this machine

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, script.Replace("\r\n", "\n"));
            var psi = new ProcessStartInfo(bash, ["-n", file]) { RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"bash -n failed for {name}: {err}\n{script}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string? FindBash()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];
        return candidates.FirstOrDefault(File.Exists);
    }
}
