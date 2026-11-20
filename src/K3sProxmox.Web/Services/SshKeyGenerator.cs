using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace K3sProxmox.Web.Services;

public record SshKeyPair(string PrivateKeyPem, string PublicKeyOpenSsh);

/// <summary>Generates a per-cluster RSA key pair that is injected into the VMs via cloud-init.</summary>
public static class SshKeyGenerator
{
    public static SshKeyPair Generate(string comment, int bits = 4096)
    {
        using var rsa = RSA.Create(bits);
        var p = rsa.ExportParameters(false);

        using var ms = new MemoryStream();
        WriteString(ms, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteMpint(ms, p.Exponent!);
        WriteMpint(ms, p.Modulus!);
        var publicKey = $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} {comment}";

        // PKCS#1 PEM ("BEGIN RSA PRIVATE KEY") is understood by SSH.NET and OpenSSH.
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        return new SshKeyPair(privatePem, publicKey);
    }

    private static void WriteString(Stream s, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(data);
    }

    private static void WriteMpint(Stream s, byte[] bigEndianUnsigned)
    {
        // Strip leading zeros, then prepend 0x00 if the high bit is set (mpint is signed).
        var i = 0;
        while (i < bigEndianUnsigned.Length - 1 && bigEndianUnsigned[i] == 0) i++;
        var trimmed = bigEndianUnsigned[i..];
        if ((trimmed[0] & 0x80) != 0)
        {
            var padded = new byte[trimmed.Length + 1];
            Buffer.BlockCopy(trimmed, 0, padded, 1, trimmed.Length);
            trimmed = padded;
        }
        WriteString(s, trimmed);
    }
}
