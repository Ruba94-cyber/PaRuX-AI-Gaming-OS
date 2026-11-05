#!/usr/bin/env python3
"""Quant-ladder and speculative-decoding measurement with the synthetic technical-QA set.

For each --model (optionally with --draft) it starts the REAL llama-server, runs the synthetic QA set greedily
(temperature 0, raw completion), then a 256-token greedy generation for decode speed, draft acceptance and output
identity, and ALWAYS kills the process it started. Reads model files only; downloads nothing.

  python -I tools/quant-ladder-bench.py --server <llama-server.exe> --out result.json \
      --model Q4_K_M=<a.gguf> --model Q8_0=<b.gguf> [--draft <small.gguf>] [--reps 3] [--flags "-c 4096 -ngl 99 -fa on -np 1"]

Per model it reports: QA correct/total (case-insensitive substring), decode tok/s (median of reps), and the
fraction of QA answers byte-identical to the FIRST --model (the reference). With --draft each model is run twice
(plain, speculative) and the speculative run reports speedup, draft acceptance and whether the greedy output of the
256-token generation and of every QA answer is identical to the plain run.
"""
import argparse, json, os, shlex, socket, statistics, subprocess, sys, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
LONG_PROMPT = ("Write a C function that parses a CAN frame buffer of 8 bytes into a struct with fields id (11 bit), "
               "dlc, and data. Then explain briefly how a DBC scale and offset convert raw to physical values.\n")


def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


def post(port, body, timeout=300):
    req = urllib.request.Request(f"http://127.0.0.1:{port}/completion", json.dumps(body).encode(), {"Content-Type": "application/json"})
    return json.load(urllib.request.urlopen(req, timeout=timeout))


def run(server, model, flags, draft, qa, reps):
    port = free_port()
    cmd = [server, "-m", model, "--host", "127.0.0.1", "--port", str(port)] + flags
    if draft:
        cmd += ["--spec-type", "draft-simple", "--spec-draft-n-max", "8", "--spec-draft-model", draft]
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        t0 = time.time()
        while True:
            if proc.poll() is not None:
                return {"error": "server exited (incompatible draft or load failure)"}
            try:
                if urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2).status == 200: break
            except Exception: time.sleep(0.5)
            if time.time() - t0 > 180: return {"error": "load timeout"}
        load_s = time.time() - t0
        answers = [post(port, {"prompt": q, "temperature": 0, "n_predict": 32, "cache_prompt": False})["content"] for q, _ in qa]
        post(port, {"prompt": LONG_PROMPT, "temperature": 0, "n_predict": 32})  # warm-up
        speeds, long_text, dn, da = [], None, 0, 0
        for _ in range(reps):
            r = post(port, {"prompt": LONG_PROMPT, "temperature": 0, "n_predict": 256, "cache_prompt": False})
            t = r.get("timings", {})
            speeds.append(t.get("predicted_per_second", 0.0))
            long_text = r["content"]
            dn += t.get("draft_n", 0); da += t.get("draft_n_accepted", 0)
        return {"answers": answers, "long": long_text, "decode_tps": round(statistics.median(speeds), 1),
                "decode_all": [round(s, 1) for s in speeds], "load_s": round(load_s, 1),
                "draft_tokens": dn, "draft_accepted": da}
    finally:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", required=True)
    ap.add_argument("--model", action="append", required=True, help="LABEL=path (first is the reference)")
    ap.add_argument("--draft")
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--flags", default="-c 4096 -ngl 99 -fa on -np 1")
    ap.add_argument("--qa", default=os.path.join(HERE, "synthetic-technical-qa.json"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    qa = json.load(open(a.qa, encoding="utf-8"))["items"]
    flags = shlex.split(a.flags)
    result, ref = {}, None
    for spec in a.model:
        label, path = spec.split("=", 1)
        row = {"size_bytes": os.path.getsize(path)}
        plain = run(a.server, path, flags, None, qa, a.reps)
        if "error" in plain: row["plain"] = plain; result[label] = row; continue
        ref = ref or plain["answers"]
        row["qa_correct"] = sum(1 for o, (_, e) in zip(plain["answers"], qa) if e in o.lower())
        row["qa_total"] = len(qa)
        row["qa_identical_to_reference"] = sum(1 for o, b in zip(plain["answers"], ref) if o == b)
        row["decode_tps"] = plain["decode_tps"]; row["decode_all"] = plain["decode_all"]; row["load_s"] = plain["load_s"]
        if a.draft:
            spec_run = run(a.server, path, flags, a.draft, qa, a.reps)
            if "error" in spec_run: row["speculative"] = spec_run
            else:
                row["speculative"] = {
                    "decode_tps": spec_run["decode_tps"], "decode_all": spec_run["decode_all"],
                    "speedup": round(spec_run["decode_tps"] / plain["decode_tps"], 3),
                    "draft_acceptance_pct": round(100.0 * spec_run["draft_accepted"] / spec_run["draft_tokens"], 1) if spec_run["draft_tokens"] else None,
                    "long_output_identical": spec_run["long"] == plain["long"],
                    "qa_identical": sum(1 for x, y in zip(spec_run["answers"], plain["answers"]) if x == y)}
        result[label] = row
        print(label, json.dumps(row), flush=True)
    json.dump(result, open(a.out, "w"), indent=1)


if __name__ == "__main__":
    sys.exit(main())
