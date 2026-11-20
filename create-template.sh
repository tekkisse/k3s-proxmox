#!/usr/bin/env bash
# Build an Ubuntu 24.04 cloud-init template on a Proxmox node.
# Run as root ON the Proxmox host:  bash create-template.sh
set -euo pipefail

VMID=9000
NAME=ubuntu-2404-k3s
STORAGE=local-lvm          # change to ceph-vm later
BRIDGE=vmbr0
WORKDIR=/root/cloudimg
IMG=noble-server-cloudimg-amd64.img
BASE_URL=https://cloud-images.ubuntu.com/noble/current

mkdir -p "$WORKDIR"
cd "$WORKDIR"

# --- Download + verify image -------------------------------------------------
wget -q -O SHA256SUMS "$BASE_URL/SHA256SUMS"
if ! sha256sum -c SHA256SUMS --ignore-missing --status 2>/dev/null || [ ! -f "$IMG" ]; then
  echo ">> Downloading $IMG"
  rm -f "$IMG"
  wget -O "$IMG" "$BASE_URL/$IMG"
fi
echo ">> Verifying checksum"
sha256sum -c SHA256SUMS --ignore-missing
qemu-img check "$IMG"

# --- Remove any previous VM/template with this ID ----------------------------
if qm status "$VMID" >/dev/null 2>&1; then
  echo ">> Destroying existing VM/template $VMID"
  qm stop "$VMID" >/dev/null 2>&1 || true
  qm destroy "$VMID" --purge
fi

# --- Create VM ----------------------------------------------------------------
echo ">> Creating VM $VMID"
qm create "$VMID" --name "$NAME" --memory 2048 --cores 2 \
  --net0 "virtio,bridge=$BRIDGE" \
  --scsihw virtio-scsi-pci --agent enabled=1 \
  --serial0 socket --vga serial0 --ostype l26 --cpu host

echo ">> Importing disk"
qm set "$VMID" --scsi0 "$STORAGE:0,import-from=$WORKDIR/$IMG,discard=on,ssd=1"

if ! qm config "$VMID" | grep -q '^scsi0:'; then
  echo "!! scsi0 was not created - aborting" >&2
  exit 1
fi

echo ">> Adding cloud-init drive and boot order"
qm set "$VMID" --ide2 "$STORAGE:cloudinit" --boot order=scsi0
qm set "$VMID" --ipconfig0 ip=dhcp

echo ">> Converting to template"
qm template "$VMID"

echo ">> Done:"
qm config "$VMID"
