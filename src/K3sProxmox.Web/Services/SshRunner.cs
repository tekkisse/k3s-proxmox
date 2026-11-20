using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace K3sProxmox.Web.Services;

public record SshTarget(string Host, string User, string PrivateKeyPem);

public class RemoteCommandException(string message) : Exception(message);

public interface ISshRunner
{
    Task WaitForSshAsync(SshTarget target, TimeSpan timeout, Action<string> log, CancellationToken ct);

    /// <summary>Runs a bash script as root (via sudo). Returns stdout.</summary>
    Task<string> RunScriptAsync(SshTarget target, string description, string script, Action<string> log,
        CancellationToken ct, bool logOutput = true, TimeSpan? timeout = null);
}

public class SshRunner(ILogger<SshRunner> logger) : ISshRunner
{
    public async Task WaitForSshAsync(SshTarget target, TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        log($"Waiting for SSH on {target.Host}");
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = CreateClient(target);
                await client.ConnectAsync(ct);
                client.Disconnect();
                log($"SSH is up on {target.Host}");
                return;
            }
            catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or SshOperationTimeoutException or InvalidOperationException)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        throw new TimeoutException($"SSH on {target.Host} not reachable within {timeout}: {last?.Message}");
    }

    public async Task<string> RunScriptAsync(SshTarget target, string description, string script, Action<string> log,
        CancellationToken ct, bool logOutput = true, TimeSpan? timeout = null)
    {
        log($"[{target.Host}] {description}");

        // Ship the script base64-encoded so no quoting/escaping issues arise. Note the encoded script is part
        // of the remote shell's command line for its (short) lifetime; the VMs are single-purpose and only the
        // cluster's own users can log in, so this is acceptable. Switch to an SFTP upload if that changes.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n")));
        var remote = "f=$(mktemp); echo '" + b64 + "' | base64 -d > \"$f\"; " +
                     "sudo -n bash -euo pipefail \"$f\"; rc=$?; rm -f \"$f\"; exit $rc";

        using var client = CreateClient(target);
        await client.ConnectAsync(ct);
        try
        {
            using var cmd = client.CreateCommand(remote);
            cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(20);
            await cmd.ExecuteAsync(ct);

            var stdout = cmd.Result ?? "";
            var stderr = cmd.Error ?? "";

            if (logOutput)
            {
                foreach (var line in Tail(stdout, 40)) log($"  {line}");
            }

            if (cmd.ExitStatus != 0)
            {
                foreach (var line in Tail(stderr, 40)) log($"  ! {line}");
                logger.LogWarning("Remote step '{Step}' on {Host} failed with exit {Code}", description, target.Host, cmd.ExitStatus);
                throw new RemoteCommandException($"'{description}' failed on {target.Host} (exit {cmd.ExitStatus}): {Tail(stderr, 5).LastOrDefault()}");
            }

            return stdout;
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static SshClient CreateClient(SshTarget target)
    {
        var key = new PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(target.PrivateKeyPem)));
        var client = new SshClient(target.Host, 22, target.User, key);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        // VMs are freshly created with keys we generated; host keys are new on every VM so we accept them.
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        return client;
    }

    private static IEnumerable<string> Tail(string text, int lines) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines).Select(l => l.TrimEnd('\r'));
}
