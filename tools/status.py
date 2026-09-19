"""查询服务状态（不需要密码）。"""
import json

f = open(r"\\.\pipe\AppLock.v1", "r+b", buffering=0)


def call(t, d=None):
    f.write((json.dumps({"id": 1, "type": t, "data": d}) + "\n").encode())
    return json.loads(f.readline())


print("hello :", call("hello")["data"])
print("status:", call("getStatus")["data"])
