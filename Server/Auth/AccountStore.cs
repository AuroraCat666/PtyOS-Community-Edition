using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPGReServer.Auth;

public enum StatusCode
{
    Unknown = -1,
    OK = 100,
    InvalidParam = 101,
    ServerInternalError = 102,
    IllegalLogin = 2011,
    InvalidUsername = 2012,
    InvalidPassword = 2013,
    InvalidToken = 2014,
    NoPermission = 2015,
    UserBanned = 2016
}

public class AccountRecord
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = "";
    [JsonPropertyName("verifyToken")] public string VerifyToken { get; set; } = "";
    [JsonPropertyName("banned")] public bool Banned { get; set; }
    [JsonPropertyName("isAdmin")] public bool IsAdmin { get; set; }
    [JsonPropertyName("avatarFile")] public string AvatarFile { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("emailVerified")] public bool EmailVerified { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("lastLoginAt")] public DateTimeOffset? LastLoginAt { get; set; }
}

public class AccountStore
{
    private const int PasswordIterations = 120_000;
    private readonly string _path;
    private readonly Dictionary<string, AccountRecord> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AccountStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "accounts.json");
        Directory.CreateDirectory(dataDir);
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<AccountRecord>>(File.ReadAllText(_path)) ?? new();
            foreach (var account in list)
            {
                if (account.CreatedAt == default) account.CreatedAt = DateTimeOffset.UtcNow;
                _accounts[account.Username] = account;
            }
        }
        catch (Exception e)
        {
            Log.Error($"[Auth] 无法读取账号文件: {e.Message}");
        }
    }

    private void Save()
    {
        string tempPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(_accounts.Values.OrderBy(a => a.Username),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, true);
    }

    public AccountRecord? Get(string username)
    {
        lock (_lock) return _accounts.TryGetValue(username, out var account) ? account : null;
    }

    public bool Create(string username, string password, bool isAdmin = false)
    {
        username = username.Trim();
        if (!IsValidUsername(username) || password.Length < 8 || password.Length > 128) return false;

        lock (_lock)
        {
            if (_accounts.ContainsKey(username)) return false;
            _accounts[username] = new AccountRecord
            {
                Username = username,
                PasswordHash = HashPassword(password),
                VerifyToken = NewToken(),
                IsAdmin = isAdmin,
                CreatedAt = DateTimeOffset.UtcNow
            };
            Save();
            return true;
        }
    }

    public void EnsureAdmin(string username, string password)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account))
            {
                _accounts[username] = new AccountRecord
                {
                    Username = username,
                    PasswordHash = HashPassword(password),
                    VerifyToken = NewToken(),
                    IsAdmin = true,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                Save();
                return;
            }

            if (!account.IsAdmin)
            {
                account.IsAdmin = true;
                Save();
            }
        }
    }

    public void ResetToSingleAdmin(string username, string password)
    {
        lock (_lock)
        {
            string? avatarFile = null;
            var previousAdmin = _accounts.Values.FirstOrDefault(account => account.IsAdmin);
            if (previousAdmin != null && !string.IsNullOrEmpty(previousAdmin.AvatarFile))
                avatarFile = previousAdmin.AvatarFile;

            _accounts.Clear();
            _accounts[username] = new AccountRecord
            {
                Username = username,
                PasswordHash = HashPassword(password),
                VerifyToken = NewToken(),
                IsAdmin = true,
                AvatarFile = avatarFile ?? "",
                CreatedAt = DateTimeOffset.UtcNow
            };
            Save();
        }
    }

    public bool VerifyPassword(string username, string password, out AccountRecord? account)
    {
        lock (_lock)
        {
            account = _accounts.TryGetValue(username, out var found) ? found : null;
            if (account == null || !VerifyHash(account.PasswordHash, password)) return false;

            if (!account.PasswordHash.StartsWith("pbkdf2$", StringComparison.Ordinal))
            {
                account.PasswordHash = HashPassword(password);
            }
            account.LastLoginAt = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    public bool VerifyToken(string username, string token, out AccountRecord? account)
    {
        lock (_lock)
        {
            account = _accounts.TryGetValue(username, out var found) ? found : null;
            return account != null && account.VerifyToken == token && !account.Banned;
        }
    }

    public string RotateToken(string username)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account)) return "";
            account.VerifyToken = NewToken();
            account.LastLoginAt = DateTimeOffset.UtcNow;
            Save();
            return account.VerifyToken;
        }
    }

    public List<AccountRecord> GetAll()
    {
        lock (_lock) return _accounts.Values.OrderBy(a => a.Username).ToList();
    }

    public bool SetBanned(string username, bool banned)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account) || account.IsAdmin) return false;
            account.Banned = banned;
            if (banned) account.VerifyToken = NewToken();
            Save();
            return true;
        }
    }

    public bool SetAvatar(string username, string avatarFile)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account)) return false;
            account.AvatarFile = avatarFile;
            Save();
            return true;
        }
    }

    public bool SetEmail(string username, string email)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account)) return false;
            if (!string.IsNullOrEmpty(email) && _accounts.Values.Any(a => a != account && a.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                return false;
            account.Email = email;
            account.EmailVerified = false;
            Save();
            return true;
        }
    }

    public bool SetEmailVerified(string username)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account)) return false;
            account.EmailVerified = true;
            Save();
            return true;
        }
    }

    public AccountRecord? FindByEmail(string email)
    {
        lock (_lock) return _accounts.Values.FirstOrDefault(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    public bool SetPassword(string username, string password)
    {
        if (password.Length is < 8 or > 128) return false;
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account)) return false;
            account.PasswordHash = HashPassword(password);
            account.VerifyToken = NewToken();
            Save();
            return true;
        }
    }

    public bool Delete(string username)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(username, out var account) || account.IsAdmin) return false;
            _accounts.Remove(username);
            Save();
            return true;
        }
    }

    public static bool IsValidUsername(string username)
        => username.Length is >= 3 and <= 24 && username.All(c =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '_' or '-');

    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyHash(string storedHash, string password)
    {
        if (!storedHash.StartsWith("pbkdf2$", StringComparison.Ordinal))
        {
            string legacy = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("RPGR-salt-" + password)));
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(storedHash), System.Text.Encoding.ASCII.GetBytes(legacy));
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out int iterations)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(64));
}
