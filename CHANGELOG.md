# Changelog

### 2026-11-05 — Inference launch-flag A/B (measured locally on RTX 5080)

- Measured locally on RTX 5080, one ~2.6 GB model, llama.cpp b12109 CUDA: `--ubatch-size` 256 / 512 vs the shipped 1024 (prefill -24% / -8.5%), KV f16 vs q8_0 (decode +2.2%, below the 5% bar, prose output diverges), `--cache-reuse 256` (no TTFT change), `--load-mode mlock` (load 2.61 -> 2.10 s, nothing else moves). Greedy guard plus a 12-question synthetic technical-QA set. **No defaults changed**; full table in `PARUX_EXECUTION_PLAN.md` XLVIII (D). Quant ladder, speculative decoding and larger models not yet measured. Local measurements, not general claims.
