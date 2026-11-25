"""MemPilot 租约 API 一键演示 / 自检（零依赖，只用标准库）。

和 lease-example.py 的区别：那个是"接进你自己脚本"的模板（要手动填 token），
这个是"装完就能跑"的验收脚本——自动读令牌与端口，走完整链路并逐项核对：

    python tools/lease-demo.py              # 申请 4GB（soft），持有 3 秒，归还
    python tools/lease-demo.py --gb 12 --policy hard
    python tools/lease-demo.py --stress     # 真的分配内存（默认只分配实测达成量的 1/3）

它会验证四件事：
  1. 令牌/端口/服务可达        → 不能连通时给出可执行的修法，而不是堆栈
  2. 返回的是实测达成量        → achievedAvailableGb / shortfallGb
  3. requesterPid 不处置自己   → 自身进程及其子树不在挂起列表里
  4. 归还是干净的              → hard 策略下挂起数 == 恢复数，且 state=released
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DEFAULT_PORT = 52717


def data_dir() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
    return Path(base) / "MemPilot"


def load_token() -> str:
    path = data_dir() / "lease-token.txt"
    if not path.exists():
        return ""
    return path.read_text(encoding="utf-8", errors="replace").strip()


def load_port(cli_port: int | None) -> int:
    if cli_port:
        return cli_port
    cfg = data_dir() / "config.json"
    try:
        raw = json.loads(cfg.read_text(encoding="utf-8"))
        # 配置落盘用 camelCase；兼容 PascalCase 以防历史版本
        for key in ("leaseApiPort", "LeaseApiPort"):
            if isinstance(raw.get(key), int) and raw[key] > 0:
                return raw[key]
    except Exception:
        pass
    return DEFAULT_PORT


class Api:
    def __init__(self, base: str, token: str):
        self.base = base
        self.token = token

    def call(self, method: str, path: str, body: dict | None = None) -> tuple[int, dict]:
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, method=method)
        req.add_header("Authorization", f"Bearer {self.token}")
        if data:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return resp.status, json.loads(resp.read().decode() or "{}")
        except urllib.error.HTTPError as e:
            try:
                return e.code, json.loads(e.read().decode() or "{}")
            except Exception:
                return e.code, {}
        except urllib.error.URLError as e:
            return 0, {"error": f"unreachable: {e.reason}"}
        except TimeoutError:
            return 0, {"error": "timeout"}


def die_hint(status: int, info: dict) -> None:
    """把失败翻译成"你该点哪里"，这是首装体验里最容易丢人的一步。"""
    if status == 0:
        print(f"\n连不上 MemPilot（{info.get('error')}）。三件事按顺序查：", file=sys.stderr)
        print("  1. MemPilot 正在运行吗（托盘有图标）？", file=sys.stderr)
        print("  2. 设置 →「租约 API」勾选启用了吗？", file=sys.stderr)
        print("  3. 端口与设置页显示的一致吗？可用 --port 覆盖。", file=sys.stderr)
    elif status == 401:
        print("\n令牌不对。打开 MemPilot 设置页 →「租约 API」，点「重新生成」后重跑"
              "（令牌在 %APPDATA%\\MemPilot\\lease-token.txt）。", file=sys.stderr)
    elif status == 409:
        print(f"\n已有活跃租约：{info.get('holder') or info}。它归还或 TTL 到期后再试。", file=sys.stderr)
    else:
        print(f"\n请求失败：{status} {info}", file=sys.stderr)


def main() -> int:
    ap = argparse.ArgumentParser(description="MemPilot lease API demo / self-check")
    ap.add_argument("--gb", type=float, default=4.0, help="目标可用内存（GB），默认 4")
    ap.add_argument("--ttl", type=int, default=120, help="租约 TTL 秒，默认 120")
    ap.add_argument("--policy", choices=("soft", "hard"), default="soft")
    ap.add_argument("--port", type=int, help="覆盖端口（默认读配置/52717）")
    ap.add_argument("--requester", default="lease-demo", help="调用方标识，写入决策日志")
    ap.add_argument("--stress", action="store_true",
                    help="真的分配内存（默认只报告，不压机器）")
    args = ap.parse_args()

    token = load_token()
    if not token:
        print("找不到租约令牌。先在 MemPilot 设置 →「租约 API」启用并显示令牌。", file=sys.stderr)
        return 2

    port = load_port(args.port)
    api = Api(f"http://127.0.0.1:{port}", token)

    status, info = api.call("GET", "/api/status")
    if status != 200:
        die_hint(status, info)
        return 1
    print(f"MemPilot {info.get('version', '?')} · 可用 {info['availableGb']} / {info['totalGb']} GB"
          f" · 提交余量 {info.get('commitFreePct', '?')}%")

    status, lease = api.call("POST", "/api/lease", {
        "targetAvailableGb": args.gb,
        "ttlSeconds": args.ttl,
        "requester": args.requester,
        "policy": args.policy,
        "requesterPid": os.getpid(),     # 自己和子进程永不被处置（v0.23.0）
    })
    if status != 200:
        die_hint(status, lease)
        return 1

    lease_id = lease["id"]
    achieved = lease.get("achievedAvailableGb", 0)
    shortfall = lease.get("shortfallGb", 0)
    suspended = lease.get("suspended") or []

    print(f"租约 {lease_id}（{lease.get('policy')}）· 实测 {achieved} GB / 目标 {args.gb} GB"
          f" · 缺口 {shortfall} GB")
    print(f"  腾挪明细：{lease.get('details', '')}")

    # 核对 1：挂起列表里绝不能有自己（requesterPid 硬不变式）
    me = os.getpid()
    if any(p.get("pid") == me for p in suspended):
        print(f"  ✗ 缺陷：租约挂起了调用方自己（pid={me}）", file=sys.stderr)
        api.call("DELETE", f"/api/lease/{lease_id}")
        return 3
    print(f"  ✓ 调用方自身（pid={me}）不在处置范围 · 挂起 {len(suspended)} 个")

    held = 0
    try:
        if args.stress:
            # 真的吃内存：按实测达成量的 1/3 分配，最多 2 GB——演示链路，不是压力测试
            target = int(min(achieved * 0.33, 2.0) * 1024 ** 3)
            block = 64 * 1024 * 1024
            chunks = []
            while held < target:
                chunks.append(bytearray(block))
                held += block
                print(f"\r  持有 {held / 1024 ** 3:.2f} GB …", end="", flush=True)
            print()
        else:
            print("  （未加 --stress：不实际分配内存，只验证协议）")
        time.sleep(3)
    except MemoryError:
        print("\n  分配时 OOM —— 这正是租约要避免的情况（说明达成量低于真实需求）", file=sys.stderr)
    finally:
        status, released = api.call("DELETE", f"/api/lease/{lease_id}")
        restored = [p for p in (released.get("suspended") or []) if p.get("restored")]
        ok = status == 200 and released.get("state") == "released"
        # 核对 2：hard 策略下挂起数必须等于恢复数
        if args.policy == "hard" and len(restored) != len(suspended):
            ok = False
            print(f"  ✗ 恢复不完整：挂起 {len(suspended)}，恢复 {len(restored)}", file=sys.stderr)
        print(f"  {'✓' if ok else '✗'} 归还状态 {released.get('state', status)}"
              f" · 恢复 {len(restored)}/{len(suspended)} 个挂起进程")
        if held:
            print(f"  演示期间持有 {held / 1024 ** 3:.2f} GB（已随进程释放）")

    return 0 if ok else 4


if __name__ == "__main__":
    raise SystemExit(main())
