using Microsoft.AspNetCore.DataProtection;

namespace K3sProxmox.Web.Services;

/// <summary>Encrypts secrets (kubeconfigs, passwords, keys) before they are stored in PostgreSQL.</summary>
public interface ISecretProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? protectedText);
}

public class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("K3sProxmox.ClusterSecrets.v1");

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? protectedText) =>
        string.IsNullOrEmpty(protectedText) ? null : _protector.Unprotect(protectedText);
}
