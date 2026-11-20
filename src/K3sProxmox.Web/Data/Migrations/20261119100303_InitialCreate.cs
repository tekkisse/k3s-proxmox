using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace K3sProxmox.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    K3sVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ControlPlaneCount = table.Column<int>(type: "integer", nullable: false),
                    WorkerCount = table.Column<int>(type: "integer", nullable: false),
                    NodeCpu = table.Column<int>(type: "integer", nullable: false),
                    NodeMemoryMb = table.Column<int>(type: "integer", nullable: false),
                    NodeDiskGb = table.Column<int>(type: "integer", nullable: false),
                    Fqdn = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ApiAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IngressIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GitRepoUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    GitRevision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GitPath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GitUsername = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GitPasswordProtected = table.Column<string>(type: "text", nullable: true),
                    StandardApps = table.Column<List<string>>(type: "text[]", nullable: false),
                    KubeconfigProtected = table.Column<string>(type: "text", nullable: true),
                    ArgoCdUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ArgoCdUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ArgoCdPasswordProtected = table.Column<string>(type: "text", nullable: true),
                    ClusterTokenProtected = table.Column<string>(type: "text", nullable: true),
                    SshPrivateKeyProtected = table.Column<string>(type: "text", nullable: true),
                    SshPublicKey = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clusters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClusterNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    VmId = table.Column<int>(type: "integer", nullable: true),
                    ProxmoxNode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClusterNodes_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClusterOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetWorkerCount = table.Column<int>(type: "integer", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Log = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClusterOperations_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IpAllocations",
                columns: table => new
                {
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpAllocations", x => x.Address);
                    table.ForeignKey(
                        name: "FK_IpAllocations_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClusterNodes_ClusterId_Name",
                table: "ClusterNodes",
                columns: new[] { "ClusterId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClusterOperations_ClusterId",
                table: "ClusterOperations",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_ClusterOperations_Status_CreatedAt",
                table: "ClusterOperations",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Clusters_Name",
                table: "Clusters",
                column: "Name",
                unique: true,
                filter: "\"Status\" <> 'Deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_Clusters_OwnerId",
                table: "Clusters",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_IpAllocations_ClusterId",
                table: "IpAllocations",
                column: "ClusterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClusterNodes");

            migrationBuilder.DropTable(
                name: "ClusterOperations");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "IpAllocations");

            migrationBuilder.DropTable(
                name: "Clusters");
        }
    }
}
