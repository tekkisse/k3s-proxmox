using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace K3sProxmox.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Cluster> Clusters => Set<Cluster>();
    public DbSet<ClusterNode> ClusterNodes => Set<ClusterNode>();
    public DbSet<ClusterOperation> ClusterOperations => Set<ClusterOperation>();
    public DbSet<IpAllocation> IpAllocations => Set<IpAllocation>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Cluster>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            // Names must be unique among clusters that still exist; deleted clusters free their name.
            e.HasIndex(x => x.Name).IsUnique().HasFilter("\"Status\" <> 'Deleted'");
            e.HasIndex(x => x.OwnerId);
            e.HasMany(x => x.Nodes).WithOne(x => x.Cluster).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Operations).WithOne(x => x.Cluster).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
        });

        b.Entity<ClusterNode>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ClusterId, x.Name }).IsUnique();
        });

        b.Entity<ClusterOperation>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        b.Entity<IpAllocation>(e =>
        {
            e.HasKey(x => x.Address);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Cluster).WithMany().HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
