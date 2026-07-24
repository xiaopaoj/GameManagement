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
    public string PasswordSalt { get; set; } = string.Empty;
    public int PasswordIterations { get; set; } = 600_000;
    public string PasswordWrappedMasterKey { get; set; } = string.Empty;
    public string ProtectedFailureState { get; set; } = string.Empty;
    public int AutoLockMinutes { get; set; } = 15;
}

public sealed class PasswordFailureState { public int FailureCount { get; set; } public DateTime? RetryAfterUtc { get; set; } }

public static class MasterKeyService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameManagement.MasterKey.v1");
    private static readonly byte[] FailureEntropy = Encoding.UTF8.GetBytes("GameManagement.PasswordFailures.v1");
    private static readonly object Sync = new();
    private static byte[]? _unlockedPasswordKey;

    public static SecurityConfiguration LoadConfiguration(string configurationPath) => File.Exists(configurationPath)
        ? JsonSerializer.Deserialize<SecurityConfiguration>(File.ReadAllText(configurationPath, Encoding.UTF8)) ?? throw new InvalidDataException("安全配置内容无效。")
        : new SecurityConfiguration();

    public static bool IsPasswordRequired(string configurationPath) => File.Exists(configurationPath) && LoadConfiguration(configurationPath).SecurityModeEnabled;

    public static byte[] GetOrCreate(string configurationPath)
    {
        lock (Sync)
        {
            if (File.Exists(configurationPath))
            {
                var existingConfiguration = LoadConfiguration(configurationPath);
                if (existingConfiguration.SecurityModeEnabled)
                {
                    if (_unlockedPasswordKey is null) throw new UnauthorizedAccessException("安全密码模式尚未解锁。");
                    return _unlockedPasswordKey.ToArray();
                }
                if (existingConfiguration.Version != 1 || existingConfiguration.KeyProtection != "DPAPI-CurrentUser" || string.IsNullOrWhiteSpace(existingConfiguration.ProtectedMasterKey)) throw new InvalidDataException("安全配置版本或主密钥保护方式不受支持。");
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

    public static bool TryUnlock(string configurationPath, string password)
    {
        lock (Sync)
        {
            byte[]? wrappingKey = null;
            try
            {
                var configuration = LoadConfiguration(configurationPath);
                if (GetRemainingRetryDelay(configurationPath) > TimeSpan.Zero) return false;
                if (!configuration.SecurityModeEnabled || configuration.KeyProtection != "Password-PBKDF2-SHA256") return false;
                wrappingKey = DerivePasswordKey(password, Convert.FromBase64String(configuration.PasswordSalt), configuration.PasswordIterations);
                var masterKey = DecryptWrappedKey(Convert.FromBase64String(configuration.PasswordWrappedMasterKey), wrappingKey);
                _unlockedPasswordKey = masterKey;
                configuration.ProtectedFailureState = string.Empty;
                WriteConfigurationAtomic(configurationPath, configuration);
                return true;
            }
            catch (CryptographicException) { RecordFailedAttempt(configurationPath); return false; }
            catch (FormatException) { RecordFailedAttempt(configurationPath); return false; }
            finally { if (wrappingKey is not null) CryptographicOperations.ZeroMemory(wrappingKey); }
        }
    }

    public static TimeSpan GetRemainingRetryDelay(string configurationPath)
    {
        if (!File.Exists(configurationPath)) return TimeSpan.Zero;
        var configuration = LoadConfiguration(configurationPath);
        var state = ReadFailureState(configuration);
        if (state.RetryAfterUtc is not DateTime retryAfter) return TimeSpan.Zero;
        var remaining = retryAfter - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void RecordFailedAttempt(string configurationPath)
    {
        var configuration = LoadConfiguration(configurationPath);
        var state = ReadFailureState(configuration); state.FailureCount++;
        var delay = state.FailureCount switch { <= 3 => TimeSpan.Zero, <= 5 => TimeSpan.FromSeconds(5), <= 9 => TimeSpan.FromSeconds(30), _ => TimeSpan.FromMinutes(5) };
        state.RetryAfterUtc = delay > TimeSpan.Zero ? DateTime.UtcNow.Add(delay) : null;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state));
        configuration.ProtectedFailureState = Convert.ToBase64String(ProtectedData.Protect(bytes, FailureEntropy, DataProtectionScope.CurrentUser));
        CryptographicOperations.ZeroMemory(bytes);
        WriteConfigurationAtomic(configurationPath, configuration);
    }

    private static PasswordFailureState ReadFailureState(SecurityConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ProtectedFailureState)) return new PasswordFailureState();
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(configuration.ProtectedFailureState), FailureEntropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<PasswordFailureState>(bytes) ?? new PasswordFailureState(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch { return new PasswordFailureState(); }
    }

    public static void EnablePassword(string configurationPath, string password)
    {
        ValidatePassword(password);
        lock (Sync)
        {
            var configuration = LoadConfiguration(configurationPath);
            if (configuration.SecurityModeEnabled) throw new InvalidOperationException("安全密码模式已经开启。");
            var masterKey = ProtectedData.Unprotect(Convert.FromBase64String(configuration.ProtectedMasterKey), Entropy, DataProtectionScope.CurrentUser);
            var salt = RandomNumberGenerator.GetBytes(16);
            var wrappingKey = DerivePasswordKey(password, salt, configuration.PasswordIterations);
            configuration.SecurityModeEnabled = true;
            configuration.KeyProtection = "Password-PBKDF2-SHA256";
            configuration.PasswordSalt = Convert.ToBase64String(salt);
            configuration.PasswordWrappedMasterKey = Convert.ToBase64String(EncryptWrappedKey(masterKey, wrappingKey));
            configuration.ProtectedMasterKey = string.Empty;
            WriteConfigurationAtomic(configurationPath, configuration);
            _unlockedPasswordKey = masterKey;
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    public static void ChangePassword(string configurationPath, string currentPassword, string newPassword)
    {
        ValidatePassword(newPassword);
        if (!TryUnlock(configurationPath, currentPassword)) throw new UnauthorizedAccessException("当前安全密码不正确。");
        lock (Sync)
        {
            var configuration = LoadConfiguration(configurationPath);
            var salt = RandomNumberGenerator.GetBytes(16);
            var wrappingKey = DerivePasswordKey(newPassword, salt, configuration.PasswordIterations);
            configuration.PasswordSalt = Convert.ToBase64String(salt);
            configuration.PasswordWrappedMasterKey = Convert.ToBase64String(EncryptWrappedKey(_unlockedPasswordKey!, wrappingKey));
            WriteConfigurationAtomic(configurationPath, configuration);
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    public static void DisablePassword(string configurationPath, string currentPassword)
    {
        if (!TryUnlock(configurationPath, currentPassword)) throw new UnauthorizedAccessException("当前安全密码不正确。");
        lock (Sync)
        {
            var configuration = LoadConfiguration(configurationPath);
            configuration.SecurityModeEnabled = false;
            configuration.KeyProtection = "DPAPI-CurrentUser";
            configuration.ProtectedMasterKey = Convert.ToBase64String(ProtectedData.Protect(_unlockedPasswordKey!, Entropy, DataProtectionScope.CurrentUser));
            configuration.PasswordSalt = string.Empty; configuration.PasswordWrappedMasterKey = string.Empty;
            WriteConfigurationAtomic(configurationPath, configuration);
            ClearSession();
        }
    }

    public static void ClearSession()
    {
        if (_unlockedPasswordKey is null) return;
        CryptographicOperations.ZeroMemory(_unlockedPasswordKey); _unlockedPasswordKey = null;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8) throw new ArgumentException("安全密码至少需要 8 个字符。", nameof(password));
    }

    private static byte[] DerivePasswordKey(string password, byte[] salt, int iterations) => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] EncryptWrappedKey(byte[] masterKey, byte[] wrappingKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[masterKey.Length];
        using (var aes = new AesGcm(wrappingKey, 16)) aes.Encrypt(nonce, masterKey, cipher, tag, Entropy);
        return [.. nonce, .. tag, .. cipher];
    }

    private static byte[] DecryptWrappedKey(byte[] blob, byte[] wrappingKey)
    {
        if (blob.Length != 60) throw new CryptographicException("密码包装主密钥格式无效。");
        var key = new byte[32];
        using (var aes = new AesGcm(wrappingKey, 16)) aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), key, Entropy);
        return key;
    }

    public static void WriteConfigurationAtomic(string path, SecurityConfiguration configuration)
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
