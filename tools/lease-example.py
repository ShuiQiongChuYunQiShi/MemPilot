"""MemPilot 租约 API 调用示例（AI 任务开跑前申请内存、结束后归还）。

用法：把脚本里的 TOKEN 换成 MemPilot 设置页「租约 API」卡片里显示的令牌，然后：

    python tools/lease-example.py          # 申请 8GB、跑一个占位任务、归还

真实接入点：在你自己的 AI 脚本里，把 `acquire()` 放在加载模型之前，`release(id)` 放在
finally 里——MemPilot 会压缩"安全子集"（绝不碰工作/游戏/前台进程）并把实测达成量返回给你。
"""
import json
import time
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:52717"   # MemPilot 设置页「租约 API」里显示的端口
TOKEN = "把这里换成设置页显示的令牌"


def _call(method: str, path: str, body: dict | None = None) -> tuple[int, dict]:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {TOKEN}")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode())


def main() -> None:
    status, info = _call("GET", "/api/status")
    if status != 200:
        print("MemPilot 不可达或令牌不对：", status, info)
        return
    print(f"当前可用 {info['availableGb']} / {info['totalGb']} GB；活跃租约：{info['leaseActive']}")

    status, lease = _call("POST", "/api/lease", {
        "targetAvailableGb": 8,      # 你的任务需要多少可用内存
        "ttlSeconds": 3600,          # 最长占用 1 小时（到期自动归还）
        "requester": "my-ai-task",
    })
    if status == 409:
        print("已有活跃租约：", lease)
        return
    if lease.get("achievedAvailableGb", 0) < 8:
        print(f"注意：目标 8GB 只凑到了 {lease.get('achievedAvailableGb')} GB（缺口 "
              f"{lease.get('shortfallGb')} GB）——任务可能仍会内存吃紧。")
    lease_id = lease["id"]
    print(f"租约 {lease_id} 生效：可用 {lease['afterAvailableGb']} GB（申请前 {lease['beforeAvailableGb']} GB）")

    try:
        print("（这里跑你的 AI 任务……示例只睡 3 秒）")
        time.sleep(3)
    finally:
        status, released = _call("DELETE", f"/api/lease/{lease_id}")
        print("归还：", status, released.get("state"))


if __name__ == "__main__":
    main()
