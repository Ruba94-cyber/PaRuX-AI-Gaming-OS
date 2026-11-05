#!/usr/bin/env python3
"""KL-divergence ladder with llama-perplexity: the reference model's logits are saved once, then every other quantisation
is scored against them on the same synthetic text. Lower mean KLD / higher 'same top token' = closer to the reference.

  python -I tools/quant-ladder-kld.py --perplexity <llama-perplexity.exe> --text <file.txt> --workdir <dir> --out kld.json \
      --ref Q8_0=<ref.gguf> --model Q6_K=<a.gguf> --model Q4_K_M=<b.gguf> [--chunks 40] [--ctx 512]

The text is synthetic (see tools/synthetic-technical-qa.json). Reads model files only; the logits file is written to --workdir
(it can be several GB: delete it afterwards).
"""
import argparse, json, os, re, subprocess, sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--perplexity", required=True); ap.add_argument("--text", required=True)
    ap.add_argument("--workdir", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--ref", required=True); ap.add_argument("--model", action="append", required=True)
    ap.add_argument("--chunks", type=int, default=40); ap.add_argument("--ctx", type=int, default=512)
    a = ap.parse_args()
    os.makedirs(a.workdir, exist_ok=True)
    base = os.path.join(a.workdir, "ref.kld")
    common = ["-f", a.text, "-c", str(a.ctx), "--chunks", str(a.chunks), "-ngl", "99", "-fa", "on"]
    rl, rp = a.ref.split("=", 1)
    r = subprocess.run([a.perplexity, "-m", rp, "--kl-divergence-base", base] + common, capture_output=True, text=True)
    if r.returncode: sys.exit("reference run failed: " + (r.stdout + r.stderr)[-600:])
    result = {rl: {"size_bytes": os.path.getsize(rp), "reference": True}}
    for spec in a.model:
        label, path = spec.split("=", 1)
        r = subprocess.run([a.perplexity, "-m", path, "--kl-divergence-base", base, "--kl-divergence"] + common, capture_output=True, text=True)
        text = r.stdout + r.stderr
        def grab(pattern):
            m = re.search(pattern, text); return float(m.group(1)) if m else None
        result[label] = {"size_bytes": os.path.getsize(path),
                         "mean_kld": grab(r"Mean\s+KLD:\s+([0-9.eE+-]+)"),
                         "p99_kld": grab(r"99\.0%\s+KLD:\s+([0-9.eE+-]+)"),
                         "same_top_p_pct": grab(r"Same top p:\s+([0-9.]+)"),
                         "rc": r.returncode}
        print(label, result[label], flush=True)
    json.dump(result, open(a.out, "w"), indent=1)


if __name__ == "__main__":
    main()
