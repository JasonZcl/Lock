"""端到端协议测试：模拟托盘程序与服务对话，验证锁定记事本的完整流程。"""
import json
import subprocess
import sys
import time

import os
PIPE = r"\\.\pipe\\" + (os.environ.get("APPLOCK_PIPE") or "AppLock.v1")
# Win11 的 notepad.exe 是指向商店版的“应用执行别名”，真实进程路径在 WindowsApps 下，所以用传统程序测试
NOTEPAD = r"C:\Windows\System32\charmap.exe"

# 同步句柄上读写不能并发（Windows 会串行化同一句柄的 I/O），所以全部在主线程顺序收发
f = open(PIPE, "r+b", buffering=0)
events = []
next_id = 0


def recv():
    line = f.readline()
    if not line:
        raise EOFError("pipe closed")
    return json.loads(line)


def call(type_, data=None, token=None):
    global next_id
    next_id += 1
    req = {"id": next_id, "type": type_}
    if token:
        req["token"] = token
    if data is not None:
        req["data"] = data
    f.write((json.dumps(req) + "\n").encode())
    while True:
        msg = recv()
        if msg.get("id") == next_id:
            return msg
        events.append(msg)


def next_event():
    if events:
        return events.pop(0)
    while True:
        msg = recv()
        if "event" in msg:
            return msg


def check(cond, msg):
    print(("PASS " if cond else "FAIL ") + msg)
    if not cond:
        sys.exit(1)


def suspended_threads(pid):
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue; if($p){{($p.Threads|?{{$_.WaitReason -eq 'Suspended'}}).Count}}else{{-1}}"],
        capture_output=True, text=True).stdout.strip()
    return int(out or -1)


hello = call("hello")
check(hello["ok"] and hello["data"]["version"] == 1, f"hello: {hello['data']}")

if not hello["data"]["hasPassword"]:
    r = call("setup", {"password": "test1234"})
    check(r["ok"] and "-" in r["data"]["recoveryKey"], f"setup, recovery key = {r['data']['recoveryKey']}")
    RECOVERY = r["data"]["recoveryKey"]
else:
    RECOVERY = None

r = call("setup", {"password": "again"})
check(not r["ok"], "second setup rejected: " + r["error"])

r = call("getSettings")
check(not r["ok"] and r.get("code") == "unauthorized", "getSettings without token rejected")

r = call("login", {"password": "wrong"})
check(not r["ok"], "wrong login rejected")

r = call("login", {"password": "test1234"})
check(r["ok"], "login ok")
token = r["data"]["token"]

settings = {
    "lockedApps": [{"exeName": "charmap.exe", "fullPath": NOTEPAD, "displayName": "字符映射表", "enabled": True, "matchMode": "FullPath"}],
    "unlockGraceSeconds": 0, "maxAttempts": 3, "paused": False,
}
r = call("setSettings", settings, token)
check(r["ok"], "setSettings ok")
r = call("getSettings", None, token)
check(r["ok"] and r["data"]["lockedApps"][0]["matchMode"] == "FullPath", "getSettings roundtrip: " + json.dumps(r["data"]["lockedApps"][0], ensure_ascii=False))

# --- 启动记事本，期待 unlockRequest ---
proc = subprocess.Popen([NOTEPAD])
ev = next_event()
check(ev.get("event") == "unlockRequest", f"received event: {ev}")
req = ev["data"]
time.sleep(1)
susp = suspended_threads(proc.pid)
check(susp > 0, f"launcher pid {proc.pid} suspended threads = {susp}")

r = call("unlockAttempt", {"requestId": req["requestId"], "password": "nope"})
check(r["ok"] and not r["data"]["success"] and r["data"]["remaining"] == 2, f"wrong password -> {r['data']}")

r = call("unlockAttempt", {"requestId": req["requestId"], "password": "test1234"})
check(r["ok"] and r["data"]["success"], f"correct password -> {r['data']}")
time.sleep(1)
susp = suspended_threads(proc.pid)
check(susp == 0 or susp == -1, f"after unlock suspended threads = {susp} (-1 = launcher exited, normal for Win11 notepad)")

# --- 第二次启动：应该因为已有解锁实例而放行（子进程放行）or 再次询问 ---
subprocess.run(["taskkill", "/IM", "charmap.exe", "/F"], capture_output=True)
time.sleep(1)
proc2 = subprocess.Popen([NOTEPAD])
ev = next_event()
check(ev.get("event") == "unlockRequest", "second launch asks again after all instances closed")
r = call("unlockCancel", {"requestId": ev["data"]["requestId"]})
time.sleep(1)
check(suspended_threads(proc2.pid) == -1, "cancel -> process killed")

# --- 超出尝试次数 ---
proc3 = subprocess.Popen([NOTEPAD])
ev = next_event()
rid = ev["data"]["requestId"]
for i in range(3):
    r = call("unlockAttempt", {"requestId": rid, "password": "bad"})
check(r["data"]["remaining"] == 0, "attempts exhausted")
closed = next_event()
check(closed.get("event") == "unlockClosed", f"unlockClosed event: {closed['data']}")
time.sleep(1)
check(suspended_threads(proc3.pid) == -1, "process killed after attempts exhausted")

# --- 恢复密钥重置密码 ---
if RECOVERY:
    r = call("resetPassword", {"recoveryKey": "AAAAA-BBBBB-CCCCC-DDDDD", "newPassword": "newpass1"})
    check(not r["ok"], "bad recovery key rejected")
    r = call("resetPassword", {"recoveryKey": RECOVERY.lower().replace("-", " "), "newPassword": "newpass1"})
    check(r["ok"], "recovery key accepted (case/dash-insensitive), new key issued")
    r = call("getSettings", None, token)
    check(not r["ok"], "old token invalidated after reset")
    r = call("login", {"password": "newpass1"})
    check(r["ok"], "login with new password")
    token = r["data"]["token"]

# --- 日志 ---
r = call("getLog", {"offset": 0, "count": 50}, token)
check(r["ok"] and len(r["data"]["entries"]) > 0 and r["data"]["total"] >= len(r["data"]["entries"]), f"log has {len(r['data']['entries'])} entries, total {r['data']['total']}")
for e in r["data"]["entries"][:12]:
    print("   ", e["time"][11:19], e["event"], e.get("displayName", ""), e.get("user", ""), e.get("detail") or "")

r = call("getStatus")
check(r["ok"] and r["data"]["enabledLockCount"] == 1, f"status: {r['data']}")

# --- 暴力破解防护：连续错误后进入锁定，正确密码也被拒 ---
cur = "newpass1" if RECOVERY else "test1234"
locked = None
for i in range(12):
    r = call("login", {"password": "brute-%d" % i})
    if r.get("code") == "lockout" or "锁定" in (r.get("error") or ""):
        locked = r; break
check(locked is not None, f"lockout kicks in after repeated failures: {locked and locked['error']}")
r = call("login", {"password": cur})
check(not r["ok"] and r.get("code") == "lockout", "even the correct password is rejected during lockout: " + r["error"])
print("ALL PASSED")
