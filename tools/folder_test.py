"""文件夹锁协议测试。需要服务已初始化密码（先跑 pipe_test.py）。
以 SYSTEM 运行的服务会真正改 ACL；非管理员前台跑的开发实例会在 add 时返回“锁定失败：没有特权”，也覆盖到错误路径。"""
import json, os, sys, tempfile

PIPE = r"\\.\pipe\\" + (os.environ.get("APPLOCK_PIPE") or "AppLock.v1")
PASSWORD = os.environ.get("APPLOCK_TEST_PASSWORD", "newpass1")

f = open(PIPE, "r+b", buffering=0)
nid = 0


def call(t, d=None, token=None):
    global nid
    nid += 1
    req = {"id": nid, "type": t}
    if token: req["token"] = token
    if d is not None: req["data"] = d
    f.write((json.dumps(req) + "\n").encode())
    while True:
        m = json.loads(f.readline())
        if m.get("id") == nid: return m


def check(c, msg):
    print(("PASS " if c else "FAIL ") + msg)
    if not c: sys.exit(1)


tmp = os.path.join(tempfile.gettempdir(), "applock_folder_test")
os.makedirs(tmp, exist_ok=True)
open(os.path.join(tmp, "secret.txt"), "w").write("hi")

r = call("login", {"password": PASSWORD}); check(r["ok"], "login"); token = r["data"]["token"]

r = call("folderStatus", {"path": tmp})
check(r["ok"] and not r["data"]["managed"] and r["data"]["forbiddenReason"] is None, f"status unmanaged, lockable: {r['data']}")
r = call("folderStatus", {"path": "C:\\Windows"})
check(r["ok"] and r["data"]["forbiddenReason"], "C:\\Windows forbidden: " + r["data"]["forbiddenReason"])

r = call("folderAdd", {"path": tmp})
check(not r["ok"] or True, "folderAdd without token rejected? -> " + str(r.get("error")))
check(r.get("code") == "unauthorized", "  ...rejected as unauthorized")

r = call("folderAdd", {"path": "C:\\Windows"}, token)
check(not r["ok"], "folderAdd C:\\Windows rejected: " + r["error"])

r = call("folderAdd", {"path": tmp}, token)
if r["ok"]:
    print("PASS folderAdd ok (service has privileges)")
    r = call("folderStatus", {"path": tmp})
    check(r["data"]["managed"] and r["data"]["folder"]["locked"], "status: managed & locked")
    try:
        os.listdir(tmp); check(False, "listing should be denied while locked")
    except PermissionError:
        check(True, "listing denied while locked")
    r = call("folderUnlock", {"path": tmp, "password": "wrong"}); check(not r["ok"], "unlock with wrong password rejected")
    r = call("folderUnlock", {"path": tmp, "password": PASSWORD}); check(r["ok"], "unlock with password ok")
    check(os.path.exists(os.path.join(tmp, "secret.txt")), "file accessible after unlock")
    r = call("folderLock", {"path": tmp}); check(r["ok"], "lock again ok")
    r = call("folderUnlock", {"path": tmp}, token); check(r["ok"], "unlock with token ok")
    r = call("folderList", None, token); check(r["ok"] and any(x["path"].lower() == tmp.lower() for x in r["data"]["folders"]), "folderList contains it")
    r = call("folderRemove", {"path": tmp}, token); check(r["ok"], "remove ok")
    r = call("folderStatus", {"path": tmp}); check(not r["data"]["managed"], "unmanaged after remove")
    check(os.path.exists(os.path.join(tmp, "secret.txt")), "file accessible after remove")
else:
    check("特权" in r["error"] or "锁定失败" in r["error"], "folderAdd fails cleanly without privileges: " + r["error"])
    r = call("folderStatus", {"path": tmp}); check(not r["data"]["managed"], "not left in list after failed lock")

print("ALL PASSED")
