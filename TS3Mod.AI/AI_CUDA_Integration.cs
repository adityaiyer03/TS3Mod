using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using HarmonyLib;
using TS3Mod.Core;

namespace TS3Mod.AI
{
    public static class ProcessOptimiser
    {
        private static string _pythonExe;
        private static readonly Dictionary<string, int> SpawnedPids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] PYTHON_CANDIDATES = new string[]
        {
            @"C:\Users\adity\AppData\Local\Programs\Python\Python312\python.exe",
            @"C:\Users\adity\AppData\Local\Programs\Python\Python311\python.exe",
            @"C:\Program Files\Python312\python.exe",
            @"C:\Program Files\Python311\python.exe"
        };

        public static void ResetSession()
        {
            SpawnedPids.Clear();
        }

        public static void Apply(ProcessStartInfo startInfo)
        {
            if (startInfo == null || string.IsNullOrWhiteSpace(startInfo.FileName)) return;

            string fileLower = startInfo.FileName.ToLowerInvariant();
            if (!fileLower.EndsWith(".exe")) return;

            string exeName = Path.GetFileName(startInfo.FileName).ToLowerInvariant();
            bool isRecog = exeName == "recog.exe" || exeName == "rm.exe";
            bool isCpm = exeName == "cpm.exe";
            bool isTts = exeName == "tts.exe";

            if (!isRecog && !isCpm && !isTts) return;

            string module = Path.GetFileNameWithoutExtension(startInfo.FileName).ToLowerInvariant();
            Log.I("[TS3Mod] Intercepting and Optimising native module=" + module);

            if (startInfo.UseShellExecute)
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
            }

            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
            startInfo.EnvironmentVariables["RM_NO_PHRASE_CAP"] = "1";

            startInfo.EnvironmentVariables["RM_STREAM_FLUSH_MS"] = "400";
            startInfo.EnvironmentVariables["RM_MAX_CHUNK_MS"] = "1200";
            startInfo.EnvironmentVariables["RM_RESAMPLE_MODE"] = "cpu";

            startInfo.EnvironmentVariables["OMP_NUM_THREADS"] = "4";
            startInfo.EnvironmentVariables["MKL_NUM_THREADS"] = "4";
            startInfo.EnvironmentVariables["OPENBLAS_NUM_THREADS"] = "4";
            startInfo.EnvironmentVariables["NUMEXPR_NUM_THREADS"] = "4";

            bool forceCpu = isTts;
            bool preferGpu = !isTts;

            if (forceCpu)
            {
                startInfo.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = "-1";
                startInfo.EnvironmentVariables["PYTORCH_NO_CUDA"] = "1";
            }
            else
            {
                startInfo.EnvironmentVariables["WHISPER_DEVICE"] = "cuda";
                startInfo.EnvironmentVariables["FASTER_WHISPER_DEVICE"] = "cuda";
                startInfo.EnvironmentVariables["CT2_CUDA_ALLOW_FP16"] = "1";

                string py = FindPython(startInfo);
                if (!string.IsNullOrEmpty(py))
                {
                    string sitePkgs = Path.Combine(Path.GetDirectoryName(py), "Lib", "site-packages");
                    if (Directory.Exists(sitePkgs))
                    {
                        string cudnnBin = Path.Combine(sitePkgs, "nvidia", "cudnn", "bin");
                        string torchLib = Path.Combine(sitePkgs, "torch", "lib");
                        string existingPath = startInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH");

                        string newPath = $"{cudnnBin};{torchLib};{existingPath}";
                        startInfo.EnvironmentVariables["PATH"] = newPath;
                        Log.I($"[TS3Mod] Injected Python CUDA PATHs for vanilla fallback: {newPath.Substring(0, Math.Min(newPath.Length, 100))}...");
                    }
                }
                AddCudaPaths(startInfo);
                StageMissingCudaDlls(startInfo.FileName); // HOT-DROP DLL FIX
            }

            if (isRecog)
            {
                string args = startInfo.Arguments ?? string.Empty;
                string cfgPath = TryExtractConfigPath(args);
                if (!string.IsNullOrEmpty(cfgPath) && File.Exists(cfgPath))
                {
                    try { PatchRecogConfigFile(cfgPath); } catch { }
                }
                else
                {
                    string lower = args.ToLowerInvariant();
                    if (!lower.Contains("--config"))
                    {
                        if (!lower.Contains("--device")) args += " --device cuda";
                        if (!lower.Contains("--compute_type")) args += " --compute_type float16";
                        if (!lower.Contains("--cpu_threads")) args += " --cpu_threads 4";
                        startInfo.Arguments = args.Trim();
                        Log.I("[TS3Mod] RECOG args injected directly into CLI for vanilla fallback: " + startInfo.Arguments);
                    }
                }
            }

            // Notice we do NOT override startInfo.FileName. 
            // We allow the game to run its native PyInstaller .exe safely!
            Log.I("[TS3Mod] Native Execution allowed for: " + startInfo.FileName);
        }

        private static string TryExtractConfigPath(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return null;
            int idx = args.IndexOf("--config", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string tail = args.Substring(idx + "--config".Length).TrimStart();
            if (tail.Length == 0) return null;
            if (tail[0] == '"')
            {
                int end = tail.IndexOf('"', 1);
                if (end > 1) return tail.Substring(1, end - 1);
                return null;
            }
            int sp = tail.IndexOf(' ');
            return sp > 0 ? tail.Substring(0, sp) : tail;
        }

        private static string FindPython(ProcessStartInfo startInfo)
        {
            if (!string.IsNullOrWhiteSpace(_pythonExe) && File.Exists(_pythonExe)) return _pythonExe;
            for (int i = 0; i < PYTHON_CANDIDATES.Length; i++)
            {
                try { if (File.Exists(PYTHON_CANDIDATES[i])) { _pythonExe = PYTHON_CANDIDATES[i]; return _pythonExe; } } catch { }
            }
            string path = startInfo.EnvironmentVariables.ContainsKey("PATH") ? startInfo.EnvironmentVariables["PATH"] : (Environment.GetEnvironmentVariable("PATH") ?? "");
            foreach (string segment in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try { string p = Path.Combine(segment.Trim(), "python.exe"); if (File.Exists(p)) { _pythonExe = p; return _pythonExe; } } catch { }
            }
            return null;
        }

        private static void AddCudaPaths(ProcessStartInfo psi)
        {
            string[] candidates = new[]
            {
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.4\bin",
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.3\bin",
                @"C:\Program Files\NVIDIA\CUDNN\v9.0\bin",
                @"C:\tools\cudnn\bin"
            };

            string existing = psi.EnvironmentVariables.ContainsKey("PATH")
                ? psi.EnvironmentVariables["PATH"]
                : (Environment.GetEnvironmentVariable("PATH") ?? "");

            var parts = new List<string>();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (Directory.Exists(candidates[i]))
                    parts.Add(candidates[i]);
            }

            string prepend = string.Join(";", parts);
            if (!string.IsNullOrEmpty(prepend))
                psi.EnvironmentVariables["PATH"] = prepend + ";" + existing;
        }

        public static void TrackStarted(Process proc, ProcessStartInfo si)
        {
            try
            {
                if (proc == null || si == null) return;
                string siArgs = si.Arguments ?? string.Empty;
                string module = null;
                if (siArgs.Contains(" \"recog\" ")) module = "recog";
                else if (siArgs.Contains(" \"cpm\" ")) module = "cpm";
                else if (siArgs.Contains(" \"tts\" ")) module = "tts";
                else if (siArgs.Contains(" \"rm\" ")) module = "rm";
                if (module != null) SpawnedPids[module] = proc.Id;
            }
            catch { }
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static void PatchRecogConfigFile(string cfgPath)
        {
            string json = File.ReadAllText(cfgPath, Encoding.UTF8);
            if (!string.IsNullOrEmpty(json) && json[0] == '\uFEFF') json = json.TrimStart('\uFEFF');
            json = UpsertJsonString(json, "device", "cuda");
            json = UpsertJsonString(json, "compute_type", "float16");
            json = UpsertJsonNumber(json, "cpu_threads", 4);
            json = UpsertJsonNumber(json, "vad_min_silence_ms", 500);
            SafeWriteTextNoBom(cfgPath, json);
        }

        private static void SafeWriteTextNoBom(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static string UpsertJsonString(string json, string key, string value)
        {
            string pattern = "\"" + key + "\"";
            int k = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (k >= 0)
            {
                int c = json.IndexOf(':', k);
                if (c > 0)
                {
                    int vStart = c + 1;
                    while (vStart < json.Length && char.IsWhiteSpace(json[vStart])) vStart++;
                    int vEnd = FindJsonValueEnd(json, vStart);
                    if (vEnd > vStart) return json.Substring(0, vStart) + "\"" + value + "\"" + json.Substring(vEnd);
                }
            }
            int insert = json.LastIndexOf('}');
            if (insert > 0)
            {
                string prefix = json.Substring(0, insert).TrimEnd();
                string add = (prefix.EndsWith("{") ? "" : ",") + "\n  \"" + key + "\": \"" + value + "\"\n";
                return json.Substring(0, insert) + add + "}";
            }
            return json;
        }

        private static string UpsertJsonNumber(string json, string key, int value)
        {
            string pattern = "\"" + key + "\"";
            int k = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (k >= 0)
            {
                int c = json.IndexOf(':', k);
                if (c > 0)
                {
                    int vStart = c + 1;
                    while (vStart < json.Length && char.IsWhiteSpace(json[vStart])) vStart++;
                    int vEnd = FindJsonValueEnd(json, vStart);
                    if (vEnd > vStart) return json.Substring(0, vStart) + value.ToString() + json.Substring(vEnd);
                }
            }
            int insert = json.LastIndexOf('}');
            if (insert > 0)
            {
                string prefix = json.Substring(0, insert).TrimEnd();
                string add = (prefix.EndsWith("{") ? "" : ",") + "\n  \"" + key + "\": " + value.ToString() + "\n";
                return json.Substring(0, insert) + add + "}";
            }
            return json;
        }

        private static int FindJsonValueEnd(string json, int start)
        {
            if (start >= json.Length) return start;
            char ch = json[start];
            if (ch == '"')
            {
                int i = start + 1;
                while (i < json.Length)
                {
                    if (json[i] == '"' && json[i - 1] != '\\') return i + 1;
                    i++;
                }
                return json.Length;
            }
            int p = start;
            while (p < json.Length && json[p] != ',' && json[p] != '}' && json[p] != '\n' && json[p] != '\r') p++;
            return p;
        }

        private static void SafeDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        private static void StageMissingCudaDlls(string exePath)
        {
            try
            {
                string targetDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(targetDir)) return;

                string python = FindPython(new ProcessStartInfo());
                if (string.IsNullOrEmpty(python)) return;

                string sitePkgs = Path.Combine(Path.GetDirectoryName(python), "Lib", "site-packages");
                string[] pathsToCheck = new[]
                {
                    Path.Combine(sitePkgs, "nvidia", "cudnn", "bin"),
                    Path.Combine(sitePkgs, "torch", "lib")
                };

                foreach (string path in pathsToCheck)
                {
                    if (!Directory.Exists(path)) continue;

                    string[] dlls = Directory.GetFiles(path, "cu*.dll");
                    foreach (string dll in dlls)
                    {
                        string destFile = Path.Combine(targetDir, Path.GetFileName(dll));
                        if (!File.Exists(destFile))
                        {
                            File.Copy(dll, destFile, true);
                            UnityEngine.Debug.Log($"[TS3Mod] Staged missing CUDA DLL for PyInstaller: {Path.GetFileName(dll)}");
                        }
                    }

                    // Zlib is a hard requirement for cuDNN 9
                    string[] zlibs = Directory.GetFiles(path, "zlibwapi.dll");
                    foreach (string z in zlibs)
                    {
                        string destFile = Path.Combine(targetDir, Path.GetFileName(z));
                        if (!File.Exists(destFile)) File.Copy(z, destFile, true);
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[TS3Mod] Failed to stage CUDA DLLs: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Process), nameof(Process.Start), new Type[0])]
    public static class ProcessStartInstancePatch
    {
        static void Prefix(Process __instance) { ProcessOptimiser.Apply(__instance.StartInfo); }
        static void Postfix(Process __instance, bool __result)
        {
            if (!__result) return;
            try { ProcessOptimiser.TrackStarted(__instance, __instance.StartInfo); } catch { }
        }
    }

    [HarmonyPatch(typeof(Process), nameof(Process.Start), new Type[] { typeof(ProcessStartInfo) })]
    public static class ProcessStartStaticPatch
    {
        static void Prefix(ProcessStartInfo startInfo) { ProcessOptimiser.Apply(startInfo); }
    }
}