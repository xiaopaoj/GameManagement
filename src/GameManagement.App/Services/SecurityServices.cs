using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameManagement.Services;

public sealed class SecurityConfiguration
{
    public int Version { get; set; } = 1;
    public bool SecurityModeEnabled { get; set; }
    public string KeyProtection { get; set; } = "DPAPI-CurrentUser";
    public string ProtectedMasterKey { get; set; } = string.Empty;
}

public static class MasterKeyService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameManagement.MasterKey.v1");
    private static readonly object Sync = new();

    public static byte[] GetOrCreate(string configurationPath)
    {
        lock (Sync)
        {
            if (File.Exists(configurationPath))
            {
                var existingConfiguration = JsonSerializer.Deserialize<SecurityConfiguration>(File.ReadAllText(configurationPath, Encoding.UTF8))
                    ?? throw new InvalidDataException("安全配置内容无效。");
                if (existingConfiguration.Version != 1 || existingConfiguration.KeyProtection != "DPAPI-CurrentUser" || string.IsNullOrWhiteSpace(existingConfiguration.ProtectedMasterKey))
                    throw new InvalidDataException("安全配置版本或主密钥保护方式不受支持。");
                return ProtectedData.Unprotect(Convert.FromBase64String(existingConfiguration.ProtectedMasterKey), Entropy, DataProtectionScope.CurrentUser);
            }

            var key = RandomNumberGenerator.GetBytes(32);
            var newConfiguration = new SecurityConfiguration
            {
                ProtectedMasterKey = Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser))
            };
            WriteConfigurationAtomic(configurationPath, newConfiguration);
            return key;
        }
    }

    private static void WriteConfigurationAtomic(string path, SecurityConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporary, path, true);
    }
}

public static class EncryptedDataFile
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GMSECDB1");
    private const byte Version = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static void WriteAtomic(string path, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new CryptographicException("数据主密钥长度无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength)) aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Magic); stream.WriteByte(Version); stream.Write(nonce); stream.Write(tag); stream.Write(ciphertext);
            stream.Flush(true);
        }
        _ = Read(temporary, key);
        File.Move(temporary, path, true);
    }

    public static byte[] Read(string path, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new CryptographicException("数据主密钥长度无效。");
        var content = File.ReadAllBytes(path);
        var headerLength = Magic.Length + 1 + NonceLength + TagLength;
        if (content.Length < headerLength || !content.AsSpan(0, Magic.Length).SequenceEqual(Magic) || content[Magic.Length] != Version)
            throw new InvalidDataException("文件不是受支持的加密数据库格式。");
        var nonce = content.AsSpan(Magic.Length + 1, NonceLength);
        var tag = content.AsSpan(Magic.Length + 1 + NonceLength, TagLength);
        var ciphertext = content.AsSpan(headerLength);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, TagLength)) aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        return plaintext;
    }
}
