# k3s on Proxmox

A self-service web application (ASP.NET Core MVC, Entity Framework Core, PostgreSQL, Keycloak login) that
creates and manages k3s Kubernetes clusters on Proxmox VE.

Users can:

* request a cluster: name, 1 or 3 control-plane nodes, N workers, and vCPU / memory / disk per node
* choose a Git repository (URL, revision, path and optional credentials) that Argo CD deploys into the cluster
* choose which standard platform applications to deploy (for example Launch Control, monitoring and the CloudNativePG operator)
* follow provisioning live, then download the kubeconfig and reveal the Argo CD admin password
* resize the cluster by adding or removing worker nodes
* retry a failed operation, or delete the cluster

## What happens when a cluster is requested

| Step | Detail |
|------|--------|
| 1. Secrets | Generates a k3s join token and an RSA SSH key pair for the cluster. Both are stored encrypted. |
| 2. IPs | Allocates a static IP for each node and an ingress VIP from the configured ranges. |
| 3. DNS | `api.<name>.<base>` → first server, `*.<name>.<base>` → ingress VIP (PowerDNS, Cloudflare or manual). |
| 4. VMs | Clones the cloud-init template (full clone), sets cores, memory, IP, SSH key and tags, grows the disk and starts the VM. VMs are spread across Proxmox hosts, up to 3 clones run at a time. |
| 5. k3s | Installs the first server with embedded etcd (`cluster-init`) and no flannel, kube-proxy, traefik or servicelb. Additional servers then join one at a time, and agents join in parallel. |
| 6. Cilium | Installs Cilium with Helm: kube-proxy replacement, L2 announcements, the Cilium ingress controller (default class `cilium`) and Hubble. A `CiliumLoadBalancerIPPool` pins the shared ingress service to the VIP. |
| 7. Argo CD | Installs the Helm chart with a generated admin password (bcrypt). Exposed at `http://argocd.<name>.<base>`. |
| 8. GitOps | Creates the `workloads` Application (the user's repo, path recursed) and one Application per selected standard app. Repo credentials are stored as an Argo CD repository secret. |
| 9. Kubeconfig | Reads `/etc/rancher/k3s/k3s.yaml`, rewrites it to `https://api.<name>.<base>:6443` and renames its entries after the cluster, then stores it encrypted. |

**Resize:** scaling up provisions and joins new agents. Scaling down cordons and drains the newest agents
(`kubectl drain`), deletes them from Kubernetes, destroys their VMs and releases their IPs. Control-plane
nodes are never removed by a resize.

**Delete:** destroys all the VMs, removes the DNS records, releases the IPs and wipes the stored secrets. The
cluster row and its operation history are kept for auditing.

### Execution model

* Requests are written to the `ClusterOperations` table and processed by `OperationWorker`, a hosted
  service. Operations survive restarts. If the app restarts mid-operation, that operation is marked failed and
  can be retried from the UI.
* Only one operation runs per cluster at a time. Across clusters, the limit is `Clusters:MaxConcurrentOperations`.
* Every step is idempotent: existing VMs are reused and scripts can safely run again. A **Retry** continues
  from where the operation broke.
* The VMID is saved the moment a clone starts, so a crash cannot leave a VM that the app doesn't know about.
* The app is designed to run as a **single instance**.

### Security

* Login uses Keycloak (OIDC authorization code flow with PKCE). Roles are read from `realm_access` and from
  `resource_access.<clientId>`.
  * `Keycloak:UserRole` (default `k3s-user`) is required to use the app. Set it to empty to allow any authenticated user.
  * `Keycloak:AdminRole` (default `k3s-admin`) can see and manage every cluster. Other users see only their own.
* Kubeconfigs, Argo CD passwords, git tokens, k3s tokens and SSH private keys are encrypted with ASP.NET
  Data Protection. The key ring is stored in PostgreSQL (the `DataProtectionKeys` table), so a database
  backup without those keys cannot reveal the secrets.
* The app logs whenever a kubeconfig is downloaded or an Argo CD password is revealed. Neither is rendered into page HTML.

## Code layout

```
src/K3sProxmox.Web
  Controllers/ClustersController.cs      UI actions: list, create, details, status (JSON), resize, retry, delete, kubeconfig
  Data/                                  EF Core entities, DbContext, migrations
  Options/Options.cs                     Strongly typed configuration
  Services/ClusterService.cs             Validation, quotas, queueing operations
  Services/Provisioning/
    ClusterProvisioner.cs                Orchestrates create / resize / delete
    OperationWorker.cs                   Background queue processor
    K3sScripts.cs                        Bash scripts: k3s, Helm, Cilium, Argo CD, drain
    ArgoManifests.cs                     Argo CD Application and repository YAML
    OperationLog.cs                      Live operation log streamed to the DB
  Services/Proxmox/ProxmoxClient.cs      Proxmox VE REST API (clone, config, resize, start, destroy, tasks)
  Services/Dns/DnsProviders.cs           None / PowerDNS / Cloudflare
  Services/SshRunner.cs                  SSH.NET script execution
  Services/IpAllocator.cs                Static IP and VIP allocation
tests/K3sProxmox.Tests                   Unit tests (including `bash -n` over every generated script)
```

## Prerequisites

### Proxmox VM template

Create an Ubuntu 24.04 cloud-image template once, on the node named by `Proxmox:TemplateNode`:

```bash
wget https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img
qm create 9000 --name ubuntu-2404-k3s --memory 2048 --cores 2 --net0 virtio,bridge=vmbr0 \
  --scsihw virtio-scsi-pci --agent enabled=1 --serial0 socket --vga serial0 --ostype l26
qm set 9000 --scsi0 ceph-vm:0,import-from=$PWD/noble-server-cloudimg-amd64.img,discard=on
qm set 9000 --ide2 ceph-vm:cloudinit --boot order=scsi0
qm template 9000
```

* Put the template on **shared storage** (Ceph, NFS and so on) when VMs must be placed on other nodes, because
  Proxmox only allows `target=` for clones from shared storage. If the template is on local storage, set
  `Proxmox:TargetNodes` to just the template's node.
* The cloud-init user (`Proxmox:CiUser`, `ubuntu` by default) needs passwordless sudo. Ubuntu cloud images provide this.
* The VMs need outbound internet access to `get.k3s.io`, GitHub (k3s binaries, Helm), `helm.cilium.io`,
  `argoproj.github.io` and the chart and git repositories you configure.

### Proxmox API token

```bash
pveum role add K3sProvisioner -privs "VM.Allocate VM.Clone VM.Config.CDROM VM.Config.CPU VM.Config.Cloudinit \
  VM.Config.Disk VM.Config.HWType VM.Config.Memory VM.Config.Network VM.Config.Options VM.Audit VM.PowerMgmt \
  Datastore.AllocateSpace Datastore.Audit SDN.Use Sys.Audit Pool.Audit"
pveum user add k3s-provisioner@pve
pveum aclmod / -user k3s-provisioner@pve -role K3sProvisioner
pveum user token add k3s-provisioner@pve app --privsep 0
```

Put the token id (`k3s-provisioner@pve!app`) and its secret into `Proxmox:TokenId` / `Proxmox:TokenSecret`.

### Network

* `Network:NodeRange*` addresses are handed to VMs through cloud-init (static).
* `Network:VipRange*` addresses must be on the **same L2 segment** as the nodes. Cilium answers ARP for them.
* Neither range may overlap your DHCP scope.

### DNS

`Dns:BaseDomain` is the parent domain. Each cluster gets `<name>.<BaseDomain>`, and records are written to
`Dns:Zone`. Use `Provider: PowerDns` or `Cloudflare` for automatic records. With `None`, the kubeconfig uses
the server IP and you create the `api.` and wildcard records yourself (the provisioning log shows them).

### Self-hosted DNS (PowerDNS + Active Directory)

`docker-compose.yml` runs PowerDNS as the authoritative server for a sub-domain that this system owns:
`tetredns.chi.swan.ac.uk` by default, set with `DNS_ZONE` in `.env`. Active Directory sends queries for that
sub-domain to PowerDNS, and the app writes all cluster records there.

```
client ──► AD DNS (chi.swan.ac.uk) ──delegation / forwarder──► PowerDNS on the Docker host :53
                                                                    tetredns.chi.swan.ac.uk
                                                                      ns1                → Docker host
                                                                      api.team-dev       → first server
                                                                      *.team-dev         → ingress VIP
```

**What happens automatically**

* On startup, the app creates the zone if it's missing (`Dns:PowerDns:AutoCreateZone`). The zone gets an SOA
  record, an NS record for `ns1.<zone>`, and an A record for `ns1` pointing at `PDNS_NS_IP`. The startup
  check repeats on every start, and existing zones are left as they are.
* Every record change bumps the SOA serial (`soa_edit_api=DEFAULT`), so secondary servers pick it up.
* PowerDNS is authoritative only. Names outside the zone get `REFUSED`, so it never acts as an open resolver.
  The API key stays inside the compose network.

**Host setup** (a Linux server with a fixed IP, not a workstation)

1. Set these in `.env`: `DNS_ZONE`, `PDNS_NS_IP` (the host's IP), and a strong `PDNS_API_KEY`.
2. If something on the host already uses port 53 (often `systemd-resolved`), set `PDNS_BIND_IP` to the
   host's LAN IP instead of `0.0.0.0`.
3. Allow UDP and TCP port 53 from the domain controllers to the host.
4. Run `docker compose up -d`.
5. Check from a DC: `Resolve-DnsName ns1.tetredns.chi.swan.ac.uk -Server <PDNS_NS_IP>`.

**Active Directory side.** Choose one of A or B, and optionally add C. Replace `10.x.x.x` with `PDNS_NS_IP`.

A. **Delegation.** Use this if AD DNS hosts the `chi.swan.ac.uk` zone. It's the cleanest option:

```powershell
Add-DnsServerZoneDelegation -Name "chi.swan.ac.uk" -ChildZoneName "tetredns" `
    -NameServer "ns1.tetredns.chi.swan.ac.uk" -IPAddress 10.x.x.x
```

B. **Conditional forwarder.** Use this if `chi.swan.ac.uk` is hosted elsewhere and only internal
resolution is needed:

```powershell
Add-DnsServerConditionalForwarderZone -Name "tetredns.chi.swan.ac.uk" `
    -MasterServers 10.x.x.x -ReplicationScope Forest
```

C. **Secondary zone (optional, for resilience).** Each DC keeps a copy of the zone and keeps answering if the
Docker host is down. Set `PDNS_ALLOW_AXFR_IPS` and `PDNS_ALSO_NOTIFY` to the DC IPs (comma separated), restart
PowerDNS, and then run this on each DC:

```powershell
Add-DnsServerSecondaryZone -Name "tetredns.chi.swan.ac.uk" `
    -ZoneFile "tetredns.chi.swan.ac.uk.dns" -MasterServers 10.x.x.x
```

PowerDNS checks the DCs' real source IPs against `PDNS_ALLOW_AXFR_IPS`. Docker on Linux keeps those source
IPs for published ports. If zone transfers are refused, check the PowerDNS log for the address it saw.

**Verify** from any domain-joined machine, once a cluster exists:

```powershell
Resolve-DnsName api.<cluster>.tetredns.chi.swan.ac.uk
Resolve-DnsName argocd.<cluster>.tetredns.chi.swan.ac.uk   # wildcard → ingress VIP
```

**Notes**

* Set the cluster VMs' DNS servers (`Network:DnsServers`) to the AD DNS servers. Nodes and pods can then
  resolve both university names and cluster names.
* The names resolve only for clients that use AD DNS. Publicly trusted certificates (Let's Encrypt) would need a
  public delegation. Otherwise, use an internal CA through cert-manager.
* Without option C, PowerDNS is a single point of failure for cluster names. Back up the `pdnsdata` volume.
  Each cluster's records (`api.` → `ApiAddress`, `*.` → `IngressIp`) are also stored in the app database, so
  they can be re-created from there.

### Keycloak

Create a confidential OIDC client (standard flow, PKCE S256) with the redirect URI
`https://<app-host>/signin-oidc` and post-logout URI `https://<app-host>/signout-callback-oidc`. Create the
`k3s-user` and `k3s-admin` realm roles (or client roles on this client). [`deploy/keycloak/platform-realm.json`](deploy/keycloak/platform-realm.json)
is a working example.

## Configuration

Everything is in [`appsettings.json`](src/K3sProxmox.Web/appsettings.json) and can be overridden with
environment variables (`Proxmox__TokenSecret`, `Keycloak__ClientSecret`, `ConnectionStrings__Default`, …).
Do not commit secrets.

### Standard applications

`Clusters:StandardApps` is the catalog shown on the create form. Each entry becomes an Argo CD `Application`:

```jsonc
{
  "Key": "launch-control",                // Application name
  "DisplayName": "Launch Control",
  "Required": false,                      // true = always installed, can't be deselected
  "DefaultEnabled": true,                 // pre-ticked on the form
  "RepoUrl": "https://git.example.com/platform/launch-control-deploy.git",
  "Path": "deploy/k3s",                   // git source ...
  "Chart": null,                          // ... or a Helm repo chart name
  "TargetRevision": "main",
  "Namespace": "launch-control",
  "SyncWave": 5,                          // operators low, consumers high
  "HelmValues": "host: lc.{{clusterFqdn}}" // {{clusterName}} {{clusterFqdn}} {{ingressIp}} {{apiAddress}}
}
```

The shipped catalog contains cert-manager, CloudNativePG, kube-prometheus-stack and a **placeholder Launch
Control entry**. Point that entry at your real repository and pin chart versions to the ones you have validated.

### User GitOps repository

The user's repository is deployed as the `workloads` Application with `directory.recurse: true` into the
`argocd` namespace. It is meant to hold Argo CD `Application`/`ApplicationSet` manifests (app-of-apps) or
namespaced manifests. Only HTTPS URLs are accepted. For private repositories, supply a username and a
token.

## Running locally

**Everything in Docker** (PostgreSQL, Keycloak, PowerDNS and the app):

```bash
cp .env.example .env                              # Proxmox / network / DNS settings (optional for UI-only testing)
docker compose up -d --build                      # app on http://localhost:8081
```

On a workstation, port 53 is usually already taken. Windows, for example, also uses 5353 for multicast DNS.
Publish DNS on a spare port for local testing, then query it with `dig`:

```bash
PDNS_DNS_PORT=1053 docker compose up -d
dig @127.0.0.1 -p 1053 tetredns.chi.swan.ac.uk SOA
```

Log in as `dev` / `dev` (admin) or `user` / `user` (regular user). The Keycloak console is at
http://localhost:8080 (`admin` / `admin`).

Browsers reach Keycloak at `localhost:8080`, but the app container reaches it at `keycloak:8080`. To make
that work, Keycloak runs with `KC_HOSTNAME=http://localhost:8080` and `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`,
and the app sets `Keycloak:MetadataAddress` to the internal discovery URL. No hosts-file changes are needed.

**App from your IDE, dependencies in Docker:**

```bash
docker compose up -d postgres keycloak
dotnet run --project src/K3sProxmox.Web           # http://localhost:5014
dotnet test                                       # unit tests
```

The database is migrated on startup (`Database:MigrateOnStartup`). To add a migration:

```bash
dotnet ef migrations add <Name> --project src/K3sProxmox.Web -o Data/Migrations
```

## Known limitations and next steps

* **API endpoint HA:** Cilium and the kubeconfig target the first server. With 3 servers, etcd is HA but the
  API entry point is not. Adding kube-vip (or a DNS name with all server IPs) as the API VIP would fix this.
* **TLS:** Argo CD and the Grafana ingress are served over HTTP until a cert-manager `ClusterIssuer` is
  added. Add one to a standard app and set `server.ingress.tls`.
* **Host keys:** new VMs' SSH host keys are trusted on first connect.
* **Single instance:** the worker assumes one app instance. Running several would need row locking
  (`FOR UPDATE SKIP LOCKED`) when claiming operations.
* **Node sizing:** a resize changes the node count only. Changing CPU or memory on existing nodes is not supported.
* **Kubernetes upgrades:** not implemented. The system-upgrade-controller could be added as a standard app.
