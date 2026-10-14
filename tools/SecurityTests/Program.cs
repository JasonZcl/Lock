using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Lock.Core.Ipc;
using Lock.Core.Models;
using Lock.Core.Native;
using Lock.Core.Services;
using Lock.Service;

if (args.Contains("--child")) { Thread.Sleep(30000); return; }
if (args.Contains("--launcher"))
{
    using var launched = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        ArgumentList = { "--child" }, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
    })!;
    Console.WriteLine(launched.Id);
    return;
}

using var runningIdentity = WindowsIdentity.GetCurrent();
var fixtureBase = runningIdentity.IsSystem ? Path.GetPathRoot(Environment.SystemDirectory)! : Path.GetTempPath();
var root = Path.Combine(fixtureBase, "applock-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("APPLOCK_DATA", Path.Combine(root, "data"));
Environment.SetEnvironmentVariable("APPLOCK_PIPE", "AppLock.SecurityTests." + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("APPLOCK_ALLOW_DEBUG_CLIENTS", null);
var passed = 0;
var skipped = 0;

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL " + description);
    passed++;
    Console.WriteLine("PASS " + description);
}

void Reject<TException>(Action action, string description) where TException : Exception
{
    try { action(); }
    catch (TException) { Check(true, description); return; }
    throw new InvalidOperationException("FAIL " + description);
}

async Task RejectAsync<TException>(Func<Task> action, string description) where TException : Exception
{
    try { await action(); }
    catch (TException) { Check(true, description); return; }
    throw new InvalidOperationException("FAIL " + description);
}

try
{
    Check(!ConfigStore.Load().HasPassword, "fresh isolated data is uninitialized");
    var manager = new ConfigManager();
    Reject<ArgumentException>(() => manager.Setup("1234"), "new short passwords rejected");
    Check(manager.Setup("test1234") != null, "initial password saved");
    Check(PasswordHasher.NewPasswordError(new string('a', 257)) != null, "oversized passwords rejected");
    var appendPath = Path.Combine(ConfigStore.DataDirectory, "append-test.log");
    ConfigStore.AppendLog(appendPath, "first\n");
    ConfigStore.AppendLog(appendPath, "second\n");
    Check(File.ReadAllText(appendPath) == "first\nsecond\n", "safe log handles append without overwriting");
    Check(!PasswordHasher.Verify(new SecretRecord { Salt = "", Hash = "", Iterations = 100000 }, "anything"), "empty password hash rejected");
    var legacySalt = RandomNumberGenerator.GetBytes(16);
    var legacy = new SecretRecord
    {
        Salt = Convert.ToBase64String(legacySalt), Iterations = 100000,
        Hash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2("1234", legacySalt, 100000, HashAlgorithmName.SHA256, 32)),
    };
    Check(PasswordHasher.Verify(legacy, "1234"), "existing four-character passwords remain verifiable");

    foreach (var minutes in new[] { 0, 1, 37 })
    {
        var settings = manager.GetSettings();
        settings.FolderRelockMinutes = minutes;
        manager.SetSettings(settings);
        Check(manager.GetSettings().FolderRelockMinutes == minutes && ConfigStore.Load().Settings.FolderRelockMinutes == minutes,
            $"relock interval {minutes} survives snapshots and persistence");
    }
    var invalid = manager.GetSettings();
    invalid.FolderRelockMinutes = -1;
    Reject<ArgumentException>(() => manager.SetSettings(invalid), "invalid relock interval rejected server-side");
    Check(manager.GetSettings().FolderRelockMinutes == 37, "failed settings update leaves memory unchanged");
    var backupBeforeFailure = File.ReadAllBytes(ConfigStore.BackupPath);
    using (var blockedPrimary = new FileStream(ConfigStore.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var uncommitted = manager.GetSettings();
        uncommitted.FolderRelockMinutes = 91;
        Reject<UnauthorizedAccessException>(() => manager.SetSettings(uncommitted), "primary replacement failure rejects settings update");
    }
    Check(manager.GetSettings().FolderRelockMinutes == 37, "primary failure leaves current settings unchanged");
    Check(File.ReadAllBytes(ConfigStore.BackupPath).SequenceEqual(backupBeforeFailure), "primary failure rolls back backup to committed settings");
    File.WriteAllText(ConfigStore.ConfigPath, "{ interrupted primary");
    Check(ConfigStore.Load().Settings.FolderRelockMinutes == 37, "backup recovery cannot activate rejected settings");
    var token = manager.Login("test1234", 100)!;
    Check(manager.ValidateToken(token, 100) && !manager.ValidateToken(token, 200), "tokens bound to issuing connection");
    manager.Logout(token, 200);
    Check(manager.ValidateToken(token, 100), "other connection cannot log out token");
    manager.RevokeClientTokens(100);
    Check(!manager.ValidateToken(token, 100), "disconnect revokes tokens");

    File.WriteAllText(ConfigStore.ConfigPath, "{ broken json");
    Check(ConfigStore.Load().HasPassword, "corrupt primary restored from valid backup");
    Check(new ConfigManager().Setup("replacement") == null, "corrupt primary cannot reopen initial setup");
    var validConfig = File.ReadAllText(ConfigStore.ConfigPath);
    File.WriteAllText(ConfigStore.ConfigPath, "{}");
    Check(ConfigStore.Load().HasPassword, "truncated valid JSON cannot erase protection");
    File.WriteAllText(ConfigStore.ConfigPath, "null");
    File.WriteAllText(ConfigStore.BackupPath, "null");
    Reject<InvalidDataException>(() => ConfigStore.Load(), "unusable primary and backup fail closed");
    File.WriteAllText(ConfigStore.ConfigPath, validConfig);
    File.WriteAllText(ConfigStore.BackupPath, validConfig);
    File.Delete(ConfigStore.ConfigPath);
    File.Delete(ConfigStore.BackupPath);
    Reject<InvalidDataException>(() => ConfigStore.Load(), "missing initialized config cannot reset password");
    File.WriteAllText(ConfigStore.ConfigPath, validConfig);
    File.WriteAllText(ConfigStore.BackupPath, validConfig);

    var target = Path.Combine(root, "target");
    Directory.CreateDirectory(target);
    var baseline = new DirectoryInfo(target).GetAccessControl(AccessControlSections.All & ~AccessControlSections.Audit).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
    using (var pathGuard = PathGuard.Pin(target)) Check(FolderLocker.WhyForbidden(target) == null, "ordinary local test folder allowed");
    Reject<ArgumentException>(() => FolderLocker.Normalize(@"\\?\C:\Windows"), "device paths rejected");
    Reject<ArgumentException>(() => FolderLocker.Normalize(@"C:\example:stream"), "alternate data streams rejected");
    Reject<ArgumentException>(() => FolderLocker.Normalize(@"C:\example.\child"), "ambiguous path components rejected");
    Check(FolderLocker.WhyForbidden(Path.GetPathRoot(Environment.SystemDirectory)!) != null, "drive root rejected");
    var alias = Path.Combine(root, "alias");
    using (var junction = Process.Start(new ProcessStartInfo("powershell.exe")
    {
        UseShellExecute = false, CreateNoWindow = true,
        ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", $"New-Item -ItemType Junction -Path '{alias.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null" },
    })!)
    {
        await junction.WaitForExitAsync();
        Check(junction.ExitCode == 0, "isolated junction fixture created");
    }
    try
    {
        Check(FolderLocker.WhyForbidden(alias) != null, "junction root rejected");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        Check(FolderLocker.WhyForbidden(Path.Combine(alias, "child")) != null, "junction in ancestor path rejected");
        Reject<IOException>(() => { using var guard = PathGuard.Pin(Path.Combine(alias, "child")); }, "operation handles reject junction ancestors");
    }
    finally { Directory.Delete(alias); }

    var fileSecurityType = typeof(NativeMethods).Assembly.GetType("Lock.Core.Native.FileSecurity")!;
    string rootIdentity;
    using (var pinnedFile = (IDisposable)fileSecurityType.GetMethod("Open")!.Invoke(null, [target, true, 0u, false])!)
    {
        var original = (string)fileSecurityType.GetMethod("ReadSddl")!.Invoke(pinnedFile, null)!;
        rootIdentity = (string)fileSecurityType.GetProperty("Identity")!.GetValue(pinnedFile)!;
        Check(original == baseline, "native handle reads complete security descriptor");
        fileSecurityType.GetMethod("WriteSddl")!.Invoke(pinnedFile, [original]);
        Check((string)fileSecurityType.GetMethod("ReadSddl")!.Invoke(pinnedFile, null)! == original, "handle ACL restore preserves descriptor");
    }

    manager.UpdateFolders(list =>
    {
        list.Add(new LockedFolder { Path = target, OriginalSddl = baseline, AclEntries = [new FolderAclEntry { RelativePath = "", Identity = rootIdentity, Sddl = baseline }] });
        return (true, 0);
    });
    var savedBackup = ConfigStore.BackupPath + ".saved";
    File.Move(ConfigStore.BackupPath, savedBackup);
    Directory.CreateDirectory(ConfigStore.BackupPath);
    try
    {
        Reject<UnauthorizedAccessException>(() => manager.UpdateFolders(list => { list[0].LockPending = true; return (true, 0); }), "ACL journal cannot commit if durable backup fails");
        Check(!manager.GetFoldersSnapshot()[0].LockPending, "failed journal write leaves in-memory recovery state unchanged");
        Check(new DirectoryInfo(target).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access) == baseline,
            "failed journal write leaves filesystem ACL unchanged");
    }
    finally
    {
        Directory.Delete(ConfigStore.BackupPath);
        File.Move(savedBackup, ConfigStore.BackupPath);
    }
    manager.UpdateFolders(list => { list.Clear(); return (true, 0); });

    var timerSettings = manager.GetSettings();
    timerSettings.FolderRelockMinutes = 1;
    manager.SetSettings(timerSettings);
    var missingFolder = Path.Combine(root, "missing-relock-fixture");
    manager.UpdateFolders(list =>
    {
        list.Add(new LockedFolder { Path = missingFolder, UnlockedAtUtc = DateTime.UtcNow.AddMinutes(-3) });
        return (true, 0);
    });
    using (var timerFolders = new FolderManager(manager, new AgentHub()))
    {
        var operationLock = typeof(FolderManager).GetField("_opLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(timerFolders)!;
        var relock = typeof(FolderManager).GetMethod("RelockExpired", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Exception? timerFailure = null;
        var timerThread = new Thread(() =>
        {
            try { relock.Invoke(timerFolders, null); }
            catch (Exception exception) { timerFailure = exception; }
        });
        Monitor.Enter(operationLock);
        try
        {
            timerThread.Start();
            Check(SpinWait.SpinUntil(() => (timerThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)),
                "relock timer waits for current folder operation");
            manager.UpdateFolders(list => { list[0].UnlockedAtUtc = DateTime.UtcNow; return (true, 0); });
        }
        finally { Monitor.Exit(operationLock); }
        Check(timerThread.Join(TimeSpan.FromSeconds(5)) && timerFailure == null, "concurrent relock timer finishes cleanly");
        Check(!LogStore.ReadPage(0, 500).Entries.Any(entry => entry.FullPath == missingFolder),
            "relock timer rechecks latest unlock time after waiting");
    }
    manager.UpdateFolders(list => { list.Clear(); return (true, 0); });
    timerSettings.FolderRelockMinutes = 37;
    manager.SetSettings(timerSettings);

    if (!runningIdentity.IsSystem && !ServiceInstaller.IsAdministrator())
    {
        var privilegeCache = typeof(FolderLocker).GetField("_privilegesEnabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousCache = privilegeCache.GetValue(null);
        try
        {
            privilegeCache.SetValue(null, true);
            using var folders = new FolderManager(manager, new AgentHub());
            Check(folders.Add(target, null) != null, "kernel rejects unprivileged SYSTEM ownership change");
            var recovery = manager.GetFoldersSnapshot().Single();
            Check(recovery.AclEntries.Count == 2 && !recovery.Locked && !recovery.LockPending,
                "ACL failure rolls back and preserves per-object recovery journal");
            Check(ConfigStore.Load().Folders[0].AclEntries.Count == 2, "recovery journal persisted before rejected ACL operation");
            Check(new DirectoryInfo(target).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access) == baseline,
                "ACL failure restores original root security descriptor");
            manager.UpdateFolders(list =>
            {
                list.Clear();
                list.Add(new LockedFolder { Path = target, OriginalSddl = baseline, Locked = true });
                return (true, 0);
            });
            Check(folders.Lock(target, null) != null, "legacy unlocked folder still respects kernel ownership privileges");
            var legacyRecovery = manager.GetFoldersSnapshot().Single();
            Check(legacyRecovery.AclEntries.Count == 2 && !legacyRecovery.Locked && !legacyRecovery.LockPending,
                "legacy stale lock flag upgrades recovery entries before attempted lock");
        }
        finally { privilegeCache.SetValue(null, previousCache); }
        manager.UpdateFolders(list => { list.Clear(); return (true, 0); });
    }

    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}\r\n{\"id\":2}\n")))
    using (var channel = new JsonLineChannel(stream))
    {
        Check((await channel.ReadAsync(default))!["id"]!.GetValue<int>() == 1, "CRLF message supported");
        Check((await channel.ReadAsync(default))!["id"]!.GetValue<int>() == 2, "buffered messages remain separated");
        Check(await channel.ReadAsync(default) == null, "clean end of stream supported");
    }
    foreach (var payload in new[] { "{broken}\n", "null\n", "[]\n", "{}", new string('x', JsonLineChannel.MaxMessageChars + 1) })
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var channel = new JsonLineChannel(stream);
        await RejectAsync<InvalidDataException>(async () => { await channel.ReadAsync(default); }, "malformed or oversized message rejected");
    }
    var boundaryMessage = "{\"value\":\"" + new string('x', JsonLineChannel.MaxMessageChars - 12) + "\"}";
    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(boundaryMessage + "\r\n")))
    using (var channel = new JsonLineChannel(stream))
        Check(await channel.ReadAsync(default) != null, "maximum-size JSON accepts CRLF framing");
    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(boundaryMessage + " \r\n")))
    using (var channel = new JsonLineChannel(stream))
        await RejectAsync<InvalidDataException>(async () => { await channel.ReadAsync(default); }, "CRLF framing cannot bypass JSON size limit");

    var fakePipeName = "AppLock.Fake." + Guid.NewGuid().ToString("N");
    using (var serverPipe = new NamedPipeServerStream(fakePipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
    {
        using var clientPipe = new NamedPipeClientStream(".", fakePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(2000);
        await connected;
        using var session = new ClientSession(serverPipe);
        var hub = new AgentHub();
        hub.Add(session);
        Check(!AgentHub.IsGenuine(session) && hub.Find(session.SessionId) == null, "impostor tray client never selected");
        Check(!PipeTrust.IsTrustedServer(clientPipe, Path.Combine(AppContext.BaseDirectory, "SysGuardSvc.exe")), "fake server rejected before handshake");
        if (Environment.UserInteractive)
        {
            Environment.SetEnvironmentVariable("APPLOCK_ALLOW_DEBUG_CLIENTS", "1");
            try
            {
                Check(hub.Find(session.SessionId) == session && PipeTrust.IsTrustedServer(clientPipe, Environment.ProcessPath!),
                    "same-user isolated debug clients require explicit opt-in");
            }
            finally { Environment.SetEnvironmentVariable("APPLOCK_ALLOW_DEBUG_CLIENTS", null); }
        }
        using var messages = new JsonLineChannel(clientPipe, leaveOpen: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handled = 0;
        var serving = session.RunAsync((_, _) =>
        {
            handled++;
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }, cancellation.Token);
        for (var requestIndex = 1; requestIndex <= 31; requestIndex++)
        {
            await messages.SendAsync(new JsonObject { ["id"] = requestIndex, ["type"] = "hello" }, cancellation.Token);
            if (requestIndex <= 30) await messages.ReadAsync(cancellation.Token);
        }
        await RejectAsync<InvalidDataException>(() => serving, "request flood disconnects bounded session");
        Check(handled == 30, "only bounded requests reach handler");
    }

    var createPipe = typeof(PipeServer).GetMethod("CreatePipe", BindingFlags.NonPublic | BindingFlags.Static)!;
    using (var reserved = (NamedPipeServerStream)createPipe.Invoke(null, [true])!)
    {
        Reject<TargetInvocationException>(() => createPipe.Invoke(null, [true]), "first pipe instance prevents name squatting");
    }
    using (var engine = new LockEngine(manager, new AgentHub()))
    using (var folders = new FolderManager(manager, new AgentHub()))
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        var server = new PipeServer(manager, new AgentHub(), engine, folders);
        var running = server.RunAsync(cancellation.Token);
        for (var index = 0; index < 20; index++)
        {
            using var client = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, cancellation.Token);
            using var channel = new JsonLineChannel(client, leaveOpen: true);
            Check(await channel.ReadAsync(cancellation.Token) == null, "production server rejects arbitrary executable and reuses instance");
        }
        cancellation.Cancel();
        await RejectAsync<OperationCanceledException>(() => running, "pipe pool shuts down cleanly");
    }

    var scope = typeof(LockEngine).GetMethod("ScopeKey", BindingFlags.NonPublic | BindingFlags.Static)!;
    Check((string)scope.Invoke(null, ["app", 1u])! != (string)scope.Invoke(null, ["app", 2u])!, "grace state isolated by terminal session");
    var processSettings = manager.GetSettings();
    processSettings.LockedApps = [new LockedApp { ExeName = Path.GetFileName(Environment.ProcessPath!).ToLowerInvariant(), FullPath = Environment.ProcessPath! }];
    processSettings.UnlockGraceSeconds = 0;
    manager.SetSettings(processSettings);
    using (var engine = new LockEngine(manager, new AgentHub()))
    {
        var recent = (Dictionary<string, DateTime>)typeof(LockEngine).GetField("_lastUnlock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        using var launcher = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            ArgumentList = { "--launcher" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        var childId = int.Parse((await launcher.StandardOutput.ReadLineAsync())!);
        await launcher.WaitForExitAsync();
        using var child = Process.GetProcessById(childId);
        try
        {
            var sessionId = NativeMethods.GetProcessSessionId((uint)childId)!.Value;
            recent[(string)scope.Invoke(null, [processSettings.LockedApps[0].Key, sessionId])!] = DateTime.UtcNow;
            var info = new ProcessMonitor.ProcessInfo((uint)childId, processSettings.LockedApps[0].ExeName, Environment.ProcessPath!, NativeMethods.GetProcessCreationTime((uint)childId));
            typeof(LockEngine).GetMethod("OnProcessStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, [info]);
            Check(child.WaitForExit(3000), "orphan launcher cannot bypass zero grace period");
        }
        finally { if (!child.HasExited) child.Kill(); }
    }

    using var identity = WindowsIdentity.GetCurrent();
    if (identity.IsSystem)
    {
        File.WriteAllText(Path.Combine(target, "secret.txt"), "fixture");
        var childSecurity = new DirectoryInfo(Path.Combine(target, "child")).GetAccessControl();
        childSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        childSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(Path.Combine(target, "child")).SetAccessControl(childSecurity);
        using var session = FolderLockSession.Capture(target);
        var snapshots = session.Entries;
        try
        {
            session.Lock();
            Check(snapshots.All(entry => FolderLocker.IsLockedOnDisk(Path.Combine(target, entry.RelativePath))), "every file and directory receives protected SYSTEM ACL");
        }
        finally { session.Restore(); }
        using var restored = FolderLockSession.Capture(target);
        Check(restored.Entries.All(entry => snapshots.Any(saved => saved.Identity == entry.Identity && saved.Sddl == entry.Sddl)), "all original ACLs restored, including protected child");
    }
    else
    {
        skipped++;
        Console.WriteLine("SKIP SYSTEM-only full ACL lock/restore test (no elevation or installed service used)");
    }
    Console.WriteLine($"RESULT {passed} passed; {skipped} skipped. Fixtures: {root}");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Console.Error.WriteLine("Isolated fixtures preserved: " + root);
    Environment.ExitCode = 1;
}
