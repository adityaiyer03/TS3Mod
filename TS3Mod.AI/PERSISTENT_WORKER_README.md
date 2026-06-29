Persistent Python Worker - README

Overview
- persistent_worker.py: minimal HTTP worker that loads model once and handles /infer requests.

Setup
1. Install Python 3.10+ on the host and ensure `python` is on PATH, or edit the embedded client to point to full python.exe path.
2. Install needed Python packages for your model, e.g. `pip install torch onnxruntime` as required.
3. Place persistent_worker.py in the runtime directory next to the AI assembly under a `worker` folder, or adjust path detection in the embedded client.

Usage
- The mod will attempt to start the worker automatically when intercepting AI helper processes.
- You can run the worker manually: `python persistent_worker.py --host 127.0.0.1 --port 5000`

Benchmarking
- Use `bench_worker.ps1` to send repeated requests and measure latency.

Notes
- The provided worker is intentionally minimal. Replace the placeholder model load and inference code with your real model integration.
- Consider packaging a self-contained Python environment or instructions for end users.
