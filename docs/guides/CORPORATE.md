# Corporate Edition (Corporate Lite)

Corporate Lite is a restricted, CPU-only, offline edition of RBG – PaRuX for local chat and document search.
**Status: PARTIAL.** It is not enterprise-certified.

## What it is

- AI chat and **Document search** only. The sidebar shows Chat, Document search, Settings and Help. Gaming, Create, cloud
  and internet features are not available.
- Packaged as a ZIP with a `parux.corporate` marker. All data stays in `PaRuXData` beside the executable. No installation and no
  administrator rights are needed by the application.
- Inference runs locally on the CPU through the bundled llama.cpp runtime. Network features are off; the model-download
  and runtime-installer endpoints answer 403.

## Packages

| Variant | Contains | Note |
|---|---|---|
| Corporate Lite with model | CPU runtime, Qwen3-4B Q4_K_M, ONNX embedding and reranker | Complete offline chat. The 4.2.0 test package was 2,668 MB. |
| NoModel | CPU runtime, no chat model | Not a complete chat setup on its own. |

The 4.1.0 release record lists the NoModel ZIP only (280,483,402 bytes). Review runtime and model licences before distribution.

## Use

1. Extract the ZIP and run `Start PaRuX Corporate Lite.cmd` (offline start).
2. Import a GGUF model in **Model Hub** (it is copied into `PaRuXData/models`) if the package has none.
3. Open **Document search**, use **Add folder...**, wait for indexing, then ask questions in Chat. Answers cite sources.
4. Only with IT approval: `Start PaRuX Corporate Model Downloads.cmd` enables GGUF downloads. Inference stays local.

## What was measured

Reference PC: Ryzen 7 8840U, CPU engine (see [PARUX_EXECUTION_PLAN.md](../../PARUX_EXECUTION_PLAN.md) and
[QA_EVIDENCE.md](../QA_EVIDENCE.md)):

- Strict gate on a synthetic corpus of 319 files: grounded answers 4/4, abstention OK, first token about 13.5 s, about 13 tokens/s.
- Core plus llama-server peak about 3.7 GB; the run used about **7 GB** of RAM in total on that PC.
- 30,000-page synthetic PDF indexed to completion (about 41 minutes); 0 non-loopback connections during the gate.

## Not established

- A clean non-developer PC with only 8 GB RAM, firewall-blocked network, OCR on scanned PDFs and disk-full behaviour: **NOT RUN**.
- Roles, per-user policies and an audit log **do not exist**.
- LAN access and the browser passphrase portal were tested by unit tests only.
- NTFS ACL support and IT approval may be required; FAT/exFAT is not certified.

Related: [PORTABLE.md](PORTABLE.md), [PRIVACY.md](PRIVACY.md), [SECURITY_REVIEW.md](../SECURITY_REVIEW.md),
[CORPORATE_TECHNICAL_RAG_ACCEPTANCE.md](../CORPORATE_TECHNICAL_RAG_ACCEPTANCE.md).
