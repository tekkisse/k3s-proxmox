using System.Net;
using K3sProxmox.Web.Data;
using K3sProxmox.Web.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace K3sProxmox.Web.Services;

public interface IIpAllocator
{
    Task<string> AllocateAsync(Guid clusterId, IpPurpose purpose, Guid? nodeId, CancellationToken ct);
    Task ReleaseAsync(string address, CancellationToken ct);
    Task ReleaseAllAsync(Guid clusterId, CancellationToken ct);
}

/// <summary>
/// Hands out static addresses from the configured ranges. The address is the primary key of
/// <see cref="IpAllocation"/>, so concurrent allocations of the same address fail on insert and are retried.
/// </summary>
public class IpAllocator(AppDbContext db, IOptions<NetworkOptions> options) : IIpAllocator
{
    private readonly NetworkOptions _opt = options.Value;

    public async Task<string> AllocateAsync(Guid clusterId, IpPurpose purpose, Guid? nodeId, CancellationToken ct)
    {
        var (start, end) = purpose == IpPurpose.Node
            ? (_opt.NodeRangeStart, _opt.NodeRangeEnd)
            : (_opt.VipRangeStart, _opt.VipRangeEnd);

        var candidates = Enumerate(start, end).ToList();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var used = (await db.IpAllocations.AsNoTracking().Select(a => a.Address).ToListAsync(ct)).ToHashSet();
            var free = candidates.FirstOrDefault(c => !used.Contains(c))
                       ?? throw new InvalidOperationException($"No free {purpose} addresses left in range {start}-{end}.");

            var alloc = new IpAllocation { Address = free, ClusterId = clusterId, Purpose = purpose, NodeId = nodeId };
            db.IpAllocations.Add(alloc);
            try
            {
                await db.SaveChangesAsync(ct);
                return free;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(alloc).State = EntityState.Detached; // lost the race, try the next one
            }
        }

        throw new InvalidOperationException("Could not allocate an IP address after several attempts.");
    }

    public async Task ReleaseAsync(string address, CancellationToken ct) =>
        await db.IpAllocations.Where(a => a.Address == address).ExecuteDeleteAsync(ct);

    public async Task ReleaseAllAsync(Guid clusterId, CancellationToken ct) =>
        await db.IpAllocations.Where(a => a.ClusterId == clusterId).ExecuteDeleteAsync(ct);

    public static IEnumerable<string> Enumerate(string start, string end)
    {
        var s = ToUInt(IPAddress.Parse(start));
        var e = ToUInt(IPAddress.Parse(end));
        if (e < s) throw new ArgumentException($"Range end {end} is before start {start}.");
        for (var i = s; i <= e && i >= s; i++)
            yield return FromUInt(i).ToString();
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) throw new ArgumentException("Only IPv4 ranges are supported.");
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint v) =>
        new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
}
