"""MemPilot 租约 API —— 模拟 AI 负载验证。

三轮复盘发现：租约上线后 3 次调用全是自测，从未被真实负载用过。
本脚本用一个受控的内存分配模拟 AI 任务的三阶段（申请 → 占用 → 释放），
验证租约整条链路：达成量是否可信、会话期间是否误伤进程、归还是否 100% 恢复。

用法（需先启动 MemPilot，令牌见设置页「租约 API」）：

    python tools/lease-load-test.py                 # soft 策略，4GB
    python tools/lease-load-test.py --hard --gb 8   # hard 策略，8GB（会挂起闲置进程）

退出码 0 = 全部检查通过；非 0 = 有断言失败（详情打印）。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:52717"
DATA_DIR = os.path.join(os.environ.get("APPDATA", ""), "MemPilot")


def call(method: str, path: str, body: dict | None = None, token: str = "") -> tuple[int, dict]:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode() or "{}")


def load_token() -> str:
    path = os.path.join(DATA_DIR, "lease-token.txt")
    if not os.path.exists(path):
        sys.exit(f"找不到令牌文件：{path}（先启动 MemPilot 并启用租约 API）")
    return open(path, encoding="utf-8").read().strip()


def suspended_count() -> int:
    """suspended.json 里还挂着多少条（归还后应为 0）。"""
    path = os.path.join(DATA_DIR, "suspended.json")
    if not os.path.exists(path):
        return 0
    try:
        return len(json.load(open(path, encoding="utf-8")))
    except Exception:
        return -1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--gb", type=float, default=4, help="申请的目标可用内存（GB）")
    ap.add_argument("--hard", action="store_true", help="使用 hard 策略（会挂起闲置进程）")
    ap.add_argument("--hold", type=int, default=20, help="占用秒数")
    args = ap.parse_args()

    token = load_token()
    failures: list[str] = []

    # ---- 0. 前置状态 ----
    st, status = call("GET", "/api/status", token=token)
    if st != 200:
        sys.exit(f"MemPilot 不可达或令牌不对：{st} {status}")
    if status.get("leaseActive"):
        sys.exit("已有活跃租约，先归还再测")
    print(f"[0] 初始可用 {status['availableGb']} GB，提交余量 {status.get('commitFreePct')}%")

    # ---- 1. 模拟 AI 任务开始：申请租约 ----
    policy = "hard" if args.hard else "soft"
    t0 = time.time()
    st, lease = call("POST", "/api/lease", {
        "targetAvailableGb": args.gb,
        "ttlSeconds": args.hold + 120,
        "requester": "lease-load-test",
        "policy": policy,
    }, token=token)
    grant_ms = (time.time() - t0) * 1000
    if st != 200:
        sys.exit(f"申请失败：{st} {lease}")
    print(f"[1] 租约 {lease['id']} 生效（{policy}，{grant_ms:.0f} ms）："
          f"{lease['beforeAvailableGb']} → {lease['afterAvailableGb']} GB，"
          f"达成 {lease['achievedAvailableGb']} GB，挂起 {len(lease['suspended'])} 个")

    if lease["achievedAvailableGb"] <= 0:
        failures.append("达成量为 0")
    if lease["achievedAvailableGb"] + 1e-6 < args.gb:
        print(f"    注意：未达成目标（缺口 {lease.get('shortfallGb')} GB）——不算失败，但记录")
    if policy == "hard" and lease["suspended"] and lease["achievedAvailableGb"] < args.gb:
        print("    hard 策略挂起了进程但仍未达标，说明机器压力很大")

    # ---- 2. 模拟负载：真正占住内存，看守护是否触发 ----
    hold_start = time.time()
    hog: list[bytearray] = []
    hog_gb = max(1.0, args.gb * 0.4)   # 只占 40%，避免真的把机器打爆
    print(f"[2] 占用 {hog_gb:.1f} GB 模拟 AI 负载，持续 {args.hold}s…")
    try:
        for _ in range(int(hog_gb * 1024 / 64)):
            hog.append(bytearray(64 * 1024 * 1024))
        while time.time() - hold_start < args.hold:
            time.sleep(1)
    finally:
        hog.clear()

    # 占用期检查：租约仍应 active
    st, mid = call("GET", "/api/lease", token=token)
    if st != 200 or mid.get("state") != "active":
        failures.append(f"占用期租约状态异常：{st} {mid.get('state')}")
    else:
        print(f"[2] 占用期租约仍 active（策略 {mid['policy']}）")

    # ---- 3. 归还 ----
    st, released = call("DELETE", f"/api/lease/{mid.get('id', lease['id'])}", token=token)
    if st != 200:
        failures.append(f"归还失败：{st} {released}")
    else:
        susp = released.get("suspended") or []
        restored = [p for p in susp if p.get("restored")]
        print(f"[3] 归还成功：{released['state']}，挂起 {len(susp)} 个，恢复 {len(restored)} 个")
        if len(restored) != len(susp):
            failures.append(f"挂起进程未全部恢复：{len(restored)}/{len(susp)}")

    time.sleep(2)

    # ---- 4. 归还后无活跃租约、无残留挂起 ----
    st, after = call("GET", "/api/lease", token=token)
    if st != 404:
        failures.append(f"归还后仍能查到租约：{st}")
    else:
        print("[4] 归还后 GET /api/lease 返回 404（正确）")

    left = suspended_count()
    if left != 0:
        failures.append(f"suspended.json 仍有 {left} 条记录")
    else:
        print("[4] suspended.json 已清空")

    # ---- 5. 决策日志应记完整链路 ----
    dec_path = os.path.join(DATA_DIR, "decisions.json")
    try:
        decs = json.load(open(dec_path, encoding="utf-8"))
        mine = [d for d in decs if d.get("Kind") == "Lease" and d.get("Target") == lease["id"]]
        kinds = {d.get("Decision") for d in mine}
        print(f"[5] 决策日志：该租约 {len(mine)} 条，动作 {sorted(kinds)}")
        if "开始" not in kinds or "归还" not in kinds:
            failures.append(f"决策日志缺少开始/归还：{sorted(kinds)}")
    except Exception as e:
        failures.append(f"读取决策日志失败：{e}")

    # ---- 结论 ----
    print()
    if failures:
        print("✗ 失败 " + str(len(failures)) + " 项：")
        for f in failures:
            print("  -", f)
        return 1
    print("✓ 租约链路全部检查通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
