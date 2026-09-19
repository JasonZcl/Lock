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
    private readonly Dictionary<string, DateTime> _tokens = new();

    public ConfigManager()
    {
        _config = ConfigStore.Load();
    }

    public bool HasPassword { get { lock (_sync) return _config.HasPassword; } }

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
            };
        }
    }

    public void SetSettings(LockSettings settings)
    {
        lock (_sync)
        {
            foreach (var app in settings.LockedApps)
            {
                app.ExeName = app.ExeName.ToLowerInvariant();
                if (app.MatchMode == MatchMode.FullPath && string.IsNullOrWhiteSpace(app.FullPath))
                    app.MatchMode = MatchMode.ExeName;
            }
            settings.MaxAttempts = Math.Max(1, settings.MaxAttempts);
            settings.UnlockGraceSeconds = Math.Max(0, settings.UnlockGraceSeconds);

            _config.Settings = settings;
            ConfigStore.Save(_config);
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
            _tokens.Clear();
            return SetPasswordCore(newPassword);
        }
    }

    /// <summary>用恢复密钥重置密码，成功返回新的恢复密钥，失败返回 null。</summary>
    public string? ResetPassword(string recoveryKey, string newPassword)
    {
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
            _tokens.Clear();
            return SetPasswordCore(newPassword);
        }
    }

    private string SetPasswordCore(string password)
    {
        var key = RecoveryKey.Generate();
        _config.Password = PasswordHasher.Create(password);
        _config.Recovery = PasswordHasher.Create(RecoveryKey.Normalize(key));
        ConfigStore.Save(_config);
        return key;
    }

    // ---- 管理登录 token ----

    public string? Login(string password)
    {
        lock (_sync)
        {
            if (!VerifyPassword(password)) return null;

            PruneTokens();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _tokens[token] = DateTime.UtcNow + TokenLifetime;
            return token;
        }
    }

    public void Logout(string? token)
    {
        if (token == null) return;
        lock (_sync) _tokens.Remove(token);
    }

    /// <summary>校验 token 并顺延有效期。</summary>
    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_sync)
        {
            PruneTokens();
            if (!_tokens.ContainsKey(token)) return false;
            _tokens[token] = DateTime.UtcNow + TokenLifetime;
            return true;
        }
    }

    private void PruneTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _tokens.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            _tokens.Remove(expired);
    }
}
