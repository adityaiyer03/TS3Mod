# TS3Mod

TS3Mod is a high-performance .NET Framework 4.8 solution developed in Visual Studio. This repository contains the TS3Mod solution and source files designed to resolve critical latency, CPU throttling, and thread-locking bottlenecks in Tower! Simulator 3 (TS3) by hijacking its unoptimised artificial intelligence (AI) execution pipeline.

---

## Objectives

The primary objective of this project is to eliminate severe performance degradation during voice command processing in Tower! Simulator 3.

By default, voice transcription introduces a 5 to 7 second hang because the game engine processes AI computations on the CPU. This mod intercepts the execution, forces hardware acceleration onto dedicated NVIDIA graphics processing units (GPUs) (tested on the RTX 4050), and refines the networking pipeline to bring voice processing latency down to approximately 1.8 seconds.

---

## Game Architecture

Tower! Simulator 3 is built on the Unity Engine and utilises an external, Python-based AI framework to handle Voice Recognition (Whisper AI) and Text-to-Speech (TTS).

Rather than spawning a new process for every spoken command, the developers packaged these Python runtimes into monolithic executables (`recog.exe`, `cpm.exe`, `tts.exe`, and `rm.exe`) using PyInstaller. During the initial game load screen, Unity launches these executables as persistent background workers. Communication between Unity and the AI runtimes is maintained throughout the gameplay session using local loopback TCP sockets.

---

## Inherent Developer Architecture Failures

The severe latency spikes and freezing issues in the vanilla game stem from several design oversights:

* **Stripped CUDA Binaries:** To reduce the installer size, the developers omitted the native NVIDIA CUDA and cuDNN libraries from their PyInstaller packages. This prevents PyTorch from binding to local GPUs, forcing a slow fallback to CPU processing.
* **Environment Version Mismatches:** The backend runtimes are highly fragmented. The `recog` and `cpm` executables were built for Python 3.12, whereas the `tts` engine was compiled for Python 3.11. This mismatch causes silent "Bad Magic Number" crashes if a single Python environment attempts to execute all modules.
* **PyInstaller Isolation:** PyInstaller extracts its runtime to a randomised temporary folder (`_MEIxxxx` in `AppData`). This isolation prevents the executable from naturally locating external system-wide DLLs.
* **Fragile Socket Implementation:** The game’s local TCP client lacks timeout configurations. If a background AI executable crashes or encounters a corrupted payload, the main Unity thread locks up indefinitely waiting for a response, causing the entire game to freeze.

---

## Mod Architecture & Component Breakdown

TS3Mod operates as a BepInEx plugin that dynamically intercepts process creation and network sockets using Harmony patching.

### 1. Core Module (`TS3Mod.Core`)
* `CoreModule.cs`: The main entry point. Handles BepInEx bootstrapping, validates sandboxed logging directories, and resolves runtime dependencies.

### 2. Networking Module (`TS3Mod.Networking`)
* `TCPModule.cs` (`NetworkBufferPatch`): Intercepts socket creations in the game’s obfuscated Network Manager. It shrinks the loopback buffer sizes to 1MB to prevent Winsock pool exhaustion, introduces a strict 15-second timeout to prevent permanent thread locks, and keeps Nagle's algorithm active to prevent fragmented JSON payloads.

### 3. Memory Patch Module (`TS3Mod.MemoryPatch`)
* Bypasses internal Unity asset leaks and prevents aggressive temporary directory wipeouts during simulation load cycles.

### 4. AI Integration Module (`TS3Mod.AI`)
* `AI_CUDA_Integration.cs` (`ProcessOptimiser`): The core of the GPU hijack. It intercepts process spawning for the AI runtimes:
    * Detects which module is starting and dynamically routes it to the matching native local interpreter (Python 3.12 for recognition, Python 3.11 for TTS).
    * Injects critical environmental variables directly into the process memory, including thread limiters (OMP, MKL, and OpenBLAS) to prevent PyTorch from starving Unity's main thread.
    * Injects GPU-specific arguments (`--device cuda`, `--compute_type float16`) directly into the CLI arguments.
    * Dynamically registers system CUDA and cuDNN paths via Python’s `os.add_dll_directory()` to allow direct VRAM allocation with zero disk-copy overhead.

---

## Prerequisites & Dependencies

To build, extend, or run this mod, the following environment requirements must be met:

### Developer Environment
* Windows Operating System
* .NET Framework 4.8 Developer Pack
* Visual Studio (2019, 2022, or newer) or any IDE that supports .NET Framework 4.8

### End-User Host Environment
* **Mod Loader:** BepInEx 5.x (x64) installed in the Tower! Simulator 3 root directory.
* **System Libraries:**
    * NVIDIA CUDA Toolkit (v12.3 or v12.4)
    * NVIDIA cuDNN (v9.0 or newer)
    * FFmpeg installed and globally added to your Windows system PATH (essential for Whisper audio decoding).
* **Local Python Runtimes:**
    * Python 3.12.x: Installed on the system to host the `recog`, `cpm`, and `rm` modules.
    * Python 3.11.x: Installed on the system to host the `tts` module.

Both Python environments must have their dependencies installed. Open a Command Prompt and run the following commands:

```cmd
:: Install dependencies for Python 3.12
"C:\Users\ <YourUsername>\AppData\Local\Programs\Python\Python312\python.exe" -m pip install torch openai-whisper numpy

:: Install dependencies for Python 3.11
"C:\Users\ <YourUsername>\AppData\Local\Programs\Python\Python311\python.exe" -m pip install torch openai-whisper numpy