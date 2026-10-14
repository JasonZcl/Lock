using System.Security.Cryptography;
using Lock.Core.Models;
using Lock.Core.Services;

namespace Lock.Service;

/// <summary>
/// 内存中的配置 + 管理登录 token。所有修改都会立即落盘。
/// </summary>
public sealed class ConfigManager
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    private readonly object _sync = new();
    private AppConfig _config;
    private readonly Dictionary<string, (DateTime Expires, int ClientId)> _tokens = new();

    public ConfigManager()
    {
        _config = ConfigStore.Load();
    }

    public bool HasPassword { get { lock (_sync) return _config.HasPassword; } }

    /// <summary>在锁内读写文件夹列表并落盘。回调返回 true 表示有修改需要保存。</summary>
    public T UpdateFolders<T>(Func<List<LockedFolder>, (bool Changed, T Result)> action)
    {
        lock (_sync)
        {
            var next = ConfigStore.Clone(_config);
            var (changed, result) = action(next.Folders);
            if (changed)
            {
                ConfigStore.Save(next);
                _config = next;
            }
            return result;
        }
    }

    public List<LockedFolder> GetFoldersSnapshot()
    {
        lock (_sync)
        {
            return _config.Folders.Select(f => new LockedFolder
            {
                Path = f.Path, DisplayName = f.DisplayName, OriginalSddl = f.OriginalSddl,
                Locked = f.Locked, UnlockedAtUtc = f.UnlockedAtUtc,
                LockPending = f.LockPending,
                AclEntries = f.AclEntries.Select(entry => new FolderAclEntry
                {
                    RelativePath = entry.RelativePath, Identity = entry.Identity, Sddl = entry.Sddl,
                }).ToList(),
            }).ToList();
        }
    }

    /// <summary>返回当前设置的快照（副本），调用方可自由读取。</summary>
    public LockSettings GetSettings()
    {
        lock (_sync)
        {
            return new LockSettings
            {
                LockedApps = _config.Settings.LockedApps.Select(a => new LockedApp
                {
                    ExeName = a.ExeName,
                    FullPath = a.FullPath,
                    DisplayName = a.DisplayName,
                    Enabled = a.Enabled,
                    MatchMode = a.MatchMode,
                }).ToList(),
                UnlockGraceSeconds = _config.Settings.UnlockGraceSeconds,
                MaxAttempts = _config.Settings.MaxAttempts,
                Paused = _config.Settings.Paused,
                FolderRelockMinutes = _config.Settings.FolderRelockMinutes,
            };
        }
    }

    public void SetSettings(LockSettings settings)
    {
        lock (_sync)
        {
            if (settings.LockedApps == null || settings.LockedApps.Count > 512
                || settings.MaxAttempts is < 1 or > 100 || settings.UnlockGraceSeconds is < 0 or > 604800
                || settings.FolderRelockMinutes is < 0 or > 10080)
                throw new ArgumentException("设置数值越界");
            foreach (var app in settings.LockedApps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.ExeName) || app.ExeName.Length > 260
                    || app.ExeName != Path.GetFileName(app.ExeName) || !Enum.IsDefined(app.MatchMode)
                    || app.DisplayName == null || app.DisplayName.Length > 256)
                    throw new ArgumentException("应用规则无效");
                app.ExeName = app.ExeName.ToLowerInvariant();
                if (app.MatchMode == MatchMode.FullPath && string.IsNullOrWhiteSpace(app.FullPath))
                    throw new ArgumentException("完整路径匹配必须提供路径");
                if (app.MatchMode == MatchMode.FullPath) app.FullPath = FolderLocker.Normalize(app.FullPath!);
            }
            var next = ConfigStore.Clone(_config);
            next.Settings = settings;
            ConfigStore.Save(next);
            _config = ConfigStore.Clone(next);
        }
    }

    // ---- 暴力破解防护：连续失败后指数退避锁定 ----
    // 管道对本机所有用户开放，没有这个任何程序都能以每秒几十次的速度试密码。

    private int _failures;
    private DateTime _lockedUntil = DateTime.MinValue;

    /// <summary>当前是否处于锁定期；是则返回剩余秒数。</summary>
    public int? LockoutRemainingSeconds
    {
        get
        {
            lock (_sync)
            {
                var remain = (_lockedUntil - DateTime.UtcNow).TotalSeconds;
                return remain > 0 ? (int)Math.Ceiling(remain) : null;
            }
        }
    }

    /// <summary>校验密码，同时维护失败计数。锁定期内一律返回 false。</summary>
    public bool VerifyPassword(string password)
    {
        if (password == null || password.Length > PasswordHasher.MaxSecretLength) return false;
        lock (_sync)
        {
            if (DateTime.UtcNow < _lockedUntil) return false;

            if (PasswordHasher.Verify(_config.Password, password))
            {
                _failures = 0;
                return true;
            }

            _failures++;
            // 前 5 次不惩罚；之后 10s、20s、40s … 封顶 10 分钟
            if (_failures > 5)
            {
                var seconds = Math.Min(600, 10 * (1 << Math.Min(6, _failures - 6)));
                _lockedUntil = DateTime.UtcNow.AddSeconds(seconds);
                ServiceLog.Info($"密码连续错误 {_failures} 次，锁定 {seconds} 秒");
            }
            return false;
        }
    }

    /// <summary>首次设置密码。已有密码时返回 null。</summary>
    public string? Setup(string password)
    {
        lock (_sync)
        {
            if (_config.HasPassword) return null;
            return SetPasswordCore(password);
        }
    }

    /// <summary>修改密码，返回新的恢复密钥。</summary>
    public string ChangePassword(string newPassword)
    {
        lock (_sync)
        {
            var key = SetPasswordCore(newPassword);
            _tokens.Clear();
            return key;
        }
    }

    /// <summary>用恢复密钥重置密码，成功返回新的恢复密钥，失败返回 null。</summary>
    public string? ResetPassword(string recoveryKey, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(recoveryKey) || recoveryKey.Length > PasswordHasher.MaxSecretLength
            || PasswordHasher.NewPasswordError(newPassword) != null) return null;
        lock (_sync)
        {
            // 恢复密钥与密码共用同一套锁定计数，避免绕过限速去猜密钥
            if (DateTime.UtcNow < _lockedUntil) return null;
            if (!PasswordHasher.Verify(_config.Recovery, RecoveryKey.Normalize(recoveryKey)))
            {
                _failures++;
                if (_failures > 5)
                    _lockedUntil = DateTime.UtcNow.AddSeconds(Math.Min(600, 10 * (1 << Math.Min(6, _failures - 6))));
                return null;
            }
            _failures = 0;
            var key = SetPasswordCore(newPassword);
            _tokens.Clear();
            return key;
        }
    }

    private string SetPasswordCore(string password)
    {
        if (PasswordHasher.NewPasswordError(password) is { } error) throw new ArgumentException(error);
        var key = RecoveryKey.Generate();
        var next = ConfigStore.Clone(_config);
        next.Password = PasswordHasher.Create(password);
        next.Recovery = PasswordHasher.Create(RecoveryKey.Normalize(key));
        ConfigStore.Save(next);
        _config = next;
        return key;
    }

    // ---- 管理登录 token ----

    public string? Login(string password, int clientId)
    {
        lock (_sync)
        {
            if (!VerifyPassword(password)) return null;

            PruneTokens();
            RevokeClientTokens(clientId);
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _tokens[token] = (DateTime.UtcNow + TokenLifetime, clientId);
            return token;
        }
    }

    public void Logout(string? token, int clientId)
    {
        if (token == null) return;
        lock (_sync)
            if (_tokens.TryGetValue(token, out var login) && login.ClientId == clientId) _tokens.Remove(token);
    }

    /// <summary>校验 token 并顺延有效期。</summary>
    public bool ValidateToken(string? token, int clientId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_sync)
        {
            PruneTokens();
            if (!_tokens.TryGetValue(token, out var login) || login.ClientId != clientId) return false;
            _tokens[token] = (DateTime.UtcNow + TokenLifetime, clientId);
            return true;
        }
    }

    private void PruneTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _tokens.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList())
            _tokens.Remove(expired);
    }

    public void RevokeClientTokens(int clientId)
    {
        lock (_sync)
            foreach (var token in _tokens.Where(entry => entry.Value.ClientId == clientId).Select(entry => entry.Key).ToList())
                _tokens.Remove(token);
    }
}
