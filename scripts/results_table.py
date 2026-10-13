#!/usr/bin/env python3
"""Experiment table for RESULTS.md, computed from evaluation/runs/<run>/results.jsonl (source of truth,
includes regrades) + config.json. Also archives a per-run summary into evaluation/baselines/<run>.json so the
numbers stay reproducible after the gitignored raw traces are deleted.

Usage: scripts/results_table.py RUN_DIR [RUN_DIR ...]
"""
import json, math, os, statistics, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def wilson(k, n, z=1.96):
    if n == 0: return (0, 0)
    p = k / n; d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d; h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return max(0, c - h), min(1, c + h)

def pct(xs, q):
    s = sorted(xs); pos = (len(s) - 1) * q; lo, hi = math.floor(pos), math.ceil(pos)
    return s[lo] + (s[hi] - s[lo]) * (pos - lo)

def summarize(d):
    rs = [json.loads(l) for l in open(os.path.join(d, "results.jsonl")) if l.strip()]
    cfg = json.load(open(os.path.join(d, "config.json")))
    n = len(rs); k = sum(r["Success"] for r in rs)
    cost = sum(r["Usage"]["CostUsd"] for r in rs)
    prompt = sum(r["Usage"]["PromptTokens"] for r in rs); hit = sum(r["Usage"]["CacheHitTokens"] for r in rs)
    walls = [r["WallMs"] / 1000 for r in rs]
    lo, hi = wilson(k, n)
    return {
        "run": os.path.basename(d.rstrip("/")), "name": cfg["cfg"]["Name"], "split": cfg["split"], "git": cfg.get("git"),
        "config": cfg["cfg"], "n": n, "successes": k, "successRate": k / n, "ci95": [lo, hi],
        "costUsd": cost, "costPerSuccess": cost / k if k else None,
        "meanTotalTokens": statistics.mean(r["Usage"]["PromptTokens"] + r["Usage"]["CompletionTokens"] for r in rs),
        "meanCompletionTokens": statistics.mean(r["Usage"]["CompletionTokens"] for r in rs),
        "meanToolCalls": statistics.mean(r["ToolCalls"] for r in rs), "meanToolErrors": statistics.mean(r["ToolErrors"] for r in rs),
        "meanToolResultChars": statistics.mean(r["ToolResultChars"] for r in rs),
        "medianWallSec": pct(walls, .5), "p95WallSec": pct(walls, .95), "cacheHitRatio": hit / prompt if prompt else 0,
        "unintendedChangeTasks": sum(1 for r in rs if r["UnintendedChanges"] > 0),
        "failures": sorted({r["TaskId"] for r in rs if not r["Success"]}),
    }

os.makedirs(os.path.join(root, "evaluation", "baselines"), exist_ok=True)
print("| Run | Split | n | Success (95% CI) | Cost / success | Tokens / task | Tool calls | Tool errors | Tool-result chars | Median / p95 wall s | Cache hit |")
print("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
for d in sys.argv[1:]:
    s = summarize(d)
    json.dump(s, open(os.path.join(root, "evaluation", "baselines", s["run"] + ".json"), "w"), indent=1)
    cps = f"${s['costPerSuccess']:.5f}" if s["costPerSuccess"] else "–"
    print(f"| {s['name']} | {s['split']} | {s['n']} | {100*s['successRate']:.1f}% ({100*s['ci95'][0]:.1f}–{100*s['ci95'][1]:.1f}) | {cps} | {s['meanTotalTokens']:,.0f} | {s['meanToolCalls']:.2f} | {s['meanToolErrors']:.2f} | {s['meanToolResultChars']:,.0f} | {s['medianWallSec']:.2f} / {s['p95WallSec']:.2f} | {100*s['cacheHitRatio']:.0f}% |")
