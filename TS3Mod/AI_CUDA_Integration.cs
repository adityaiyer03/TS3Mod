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

        private const string LauncherScript = @"
import os, sys, runpy, traceback, shutil

module_name   = sys.argv[1]
extract_dir   = sys.argv[2]
pyc_path      = sys.argv[3]
stderr_log    = sys.argv[4]
stdout_log    = sys.argv[5]
crash_log     = sys.argv[6]
preflight_log = sys.argv[7]
force_cpu     = sys.argv[8] == '1'
prefer_gpu    = sys.argv[9] == '1'
mirror_a      = sys.argv[10]
mirror_b      = sys.argv[11]
orig_args     = sys.argv[12:]

def _safe_open(p):
    try:
        d = os.path.dirname(p)
        if d: os.makedirs(d, exist_ok=True)
        return open(p, 'w', encoding='utf-8', buffering=1)
    except Exception:
        return None

def _mirror(path):
    for md in (mirror_a, mirror_b):
        if not md:
            continue
        try:
            os.makedirs(md, exist_ok=True)
            shutil.copy2(path, os.path.join(md, os.path.basename(path)))
        except Exception:
            pass

def _pre(msg):
    try:
        with open(preflight_log, 'a', encoding='utf-8') as f:
            f.write(msg + '\n')
        _mirror(preflight_log)
    except Exception:
        pass

errf = _safe_open(stderr_log)
outf = _safe_open(stdout_log)
if errf is not None: sys.stderr = errf
if outf is not None: sys.stdout = outf

try:
    _pre('[TS3Mod] launcher boot')
    _pre('[TS3Mod] module=' + module_name)

    os.environ['PYTHONUNBUFFERED'] = '1'
    os.environ['PYTHONUTF8'] = '1'
    os.environ.pop('_MEIPASS', None)
    os.environ.pop('_MEIPASS2', None)
    os.environ['RM_NO_PHRASE_CAP'] = '1'
    os.environ.setdefault('RM_STREAM_FLUSH_MS', '120')
    os.environ.setdefault('RM_MAX_CHUNK_MS', '1200')
    os.environ.setdefault('RM_RESAMPLE_MODE', 'fast')
    os.environ.setdefault('OMP_NUM_THREADS', '1')
    os.environ.setdefault('MKL_NUM_THREADS', '1')
    os.environ.setdefault('OPENBLAS_NUM_THREADS', '1')
    os.environ.setdefault('NUMEXPR_NUM_THREADS', '1')

    pyc_dir = os.path.dirname(pyc_path)
    if pyc_dir and pyc_dir not in sys.path:
        sys.path.insert(0, pyc_dir)
    if extract_dir and extract_dir not in sys.path:
        sys.path.append(extract_dir)

    if force_cpu:
        os.environ['CUDA_VISIBLE_DEVICES'] = '-1'
        os.environ['PYTORCH_NO_CUDA'] = '1'
    else:
        if os.environ.get('CUDA_VISIBLE_DEVICES') == '-1':
            del os.environ['CUDA_VISIBLE_DEVICES']
        os.environ.pop('PYTORCH_NO_CUDA', None)

    _pre('[TS3Mod] force_cpu=' + str(force_cpu))
    _pre('[TS3Mod] prefer_gpu=' + str(prefer_gpu))
    _pre('[TS3Mod] orig_args=' + ' '.join(orig_args))

    lower_mod = module_name.lower()
    injected = []
    if (not force_cpu) and prefer_gpu and ('recog' in lower_mod or lower_mod == 'rm'):
        injected.extend(['--device', 'cuda'])
        injected.extend(['--compute_type', 'float16'])
        injected.extend(['--fp16', 'True'])

    final_args = list(orig_args) + injected
    sys.argv = [pyc_path] + final_args
    _pre('[TS3Mod] final_argv=' + ' '.join(sys.argv[1:]))

    try:
        import torch
        cuda_ok = False
        try:
            cuda_ok = bool(torch.cuda.is_available())
        except Exception:
            cuda_ok = False
        _pre('[TS3Mod] torch=' + str(getattr(torch, '__file__', 'unknown')))
        _pre('[TS3Mod] torch.version.cuda=' + str(getattr(torch.version, 'cuda', None)))
        _pre('[TS3Mod] torch.cuda.is_available(before)=' + str(cuda_ok))

        if force_cpu:
            _orig_device = torch.device
            def _cpu_device(*args, **kwargs):
                if args and isinstance(args[0], str) and 'cuda' in args[0].lower():
                    return _orig_device('cpu')
                return _orig_device(*args, **kwargs)
            torch.device = _cpu_device
            torch.cuda.is_available = lambda: False
            _pre('[TS3Mod] CPU monkey patch active')
        else:
            if prefer_gpu and not cuda_ok:
                _pre('[TS3Mod][WARN] GPU preferred but unavailable; CPU fallback')

    except Exception as e:
        _pre('[TS3Mod] torch import failed: ' + repr(e))

    runpy.run_path(pyc_path, run_name='__main__')

except BaseException:
    tb = traceback.format_exc()
    try:
        with open(crash_log, 'w', encoding='utf-8') as f:
            f.write(tb)
        _mirror(crash_log)
    except Exception:
        pass
    try:
        print(tb, flush=True)
    except Exception:
        pass
    raise

finally:
    try:
        if errf is not None: errf.flush()
    except Exception:
        pass
    try:
        if outf is not None: outf.flush()
    except Exception:
        pass
    try:
        _mirror(stderr_log)
        _mirror(stdout_log)
        _mirror(preflight_log)
    except Exception:
        pass
";

        public static void ResetSession()
        {
            SpawnedPids.Clear();
        }

        public static void Apply(ProcessStartInfo startInfo)
        {
            if (startInfo == null) return;
            if (string.IsNullOrWhiteSpace(startInfo.FileName)) return;

            string fileLower = startInfo.FileName.ToLowerInvariant();
            if (!fileLower.EndsWith(".exe")) return;

            string exeName = Path.GetFileName(startInfo.FileName).ToLowerInvariant();
            bool isRecog = exeName == "recog.exe" || exeName == "rm.exe";
            bool isCpm = exeName == "cpm.exe";
            bool isTts = exeName == "tts.exe";

            if (!isRecog && !isCpm && !isTts) return;

            string module = Path.GetFileNameWithoutExtension(startInfo.FileName).ToLowerInvariant();
            Log.I("Intercept module=" + module);

            string python = FindPython(startInfo);
            if (string.IsNullOrEmpty(python))
            {
                Log.E("[TS3Mod] python.exe not found! Native bypass aborted.");
                return;
            }

            string extractDir, pycPath;
            if (!TryResolveExtractedLayout(startInfo.FileName, module, out extractDir, out pycPath))
            {
                Log.E("[TS3Mod] Could not resolve extracted layout for " + module + ". Bypass aborted.");
                return;
            }

            if (startInfo.UseShellExecute)
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
            }

            string tempDir = UnityEngine.Application.temporaryCachePath;
            string launcherPath = Path.Combine(tempDir, module + "_launcher.py");
            string stderrLog = Path.Combine(TS3Plugin.RuntimeLogDir, module + ".stderr.log");
            string stdoutLog = Path.Combine(TS3Plugin.RuntimeLogDir, module + ".stdout.log");
            string crashLog = Path.Combine(TS3Plugin.RuntimeLogDir, module + ".crash.log");
            string preflightLog = Path.Combine(TS3Plugin.RuntimeLogDir, module + ".preflight.log");

            try { File.WriteAllText(launcherPath, LauncherScript, Encoding.UTF8); }
            catch (Exception ex) { Log.E("[TS3Mod] Failed to write script: " + ex.Message); return; }

            SafeDelete(stderrLog); SafeDelete(stdoutLog); SafeDelete(crashLog); SafeDelete(preflightLog);

            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
            startInfo.EnvironmentVariables["RM_NO_PHRASE_CAP"] = "1";

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
                AddCudaPaths(startInfo);
            }

            if (isRecog)
            {
                string args = startInfo.Arguments ?? string.Empty;
                string cfgPath = TryExtractConfigPath(args);
                if (!string.IsNullOrEmpty(cfgPath) && File.Exists(cfgPath))
                {
                    try { PatchRecogConfigFile(cfgPath); } catch { }
                }
            }

            string origArgs = startInfo.Arguments ?? string.Empty;
            string mirrorA = TS3Plugin.MirrorLogDirPrimary ?? "";
            string mirrorB = TS3Plugin.MirrorLogDirFallback ?? "";

            startInfo.FileName = python;
            startInfo.Arguments = $"-u \"{launcherPath}\" \"{module}\" \"{extractDir}\" \"{pycPath}\" \"{stderrLog}\" \"{stdoutLog}\" \"{crashLog}\" \"{preflightLog}\" {(forceCpu ? "1" : "0")} {(preferGpu ? "1" : "0")} \"{mirrorA}\" \"{mirrorB}\" {origArgs}".Trim();

            Log.I("Reroute execution success: " + startInfo.FileName + " " + startInfo.Arguments);
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

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static void PatchRecogConfigFile(string cfgPath)
        {
            string json = File.ReadAllText(cfgPath, Encoding.UTF8);
            if (!string.IsNullOrEmpty(json) && json[0] == '\uFEFF') json = json.TrimStart('\uFEFF');
            json = UpsertJsonString(json, "device", "cuda");
            json = UpsertJsonString(json, "compute_type", "float16");
            json = UpsertJsonNumber(json, "cpu_threads", 4);
            json = UpsertJsonNumber(json, "final_vad_min_silence_ms", 250);
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

        private static void SafeDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        private static bool TryResolveExtractedLayout(string originalExePath, string module, out string extractDir, out string pycPath)
        {
            extractDir = null; pycPath = null;
            try
            {
                string exeDir = Path.GetDirectoryName(originalExePath) ?? "";
                string exeName = Path.GetFileName(originalExePath) ?? "";
                string stem = Path.GetFileNameWithoutExtension(originalExePath) ?? module;
                string[] candDirs = new[] { originalExePath + "_extracted", Path.Combine(exeDir, exeName + "_extracted"), Path.Combine(exeDir, stem + "_extracted"), Path.Combine(exeDir, module + "_extracted"), Path.Combine(exeDir, "_internal") };
                for (int i = 0; i < candDirs.Length; i++)
                {
                    string d = candDirs[i]; if (!Directory.Exists(d)) continue;
                    string[] candPyc = new[] { Path.Combine(d, module + ".pyc"), Path.Combine(d, stem + ".pyc") };
                    for (int j = 0; j < candPyc.Length; j++) { if (File.Exists(candPyc[j])) { extractDir = d; pycPath = candPyc[j]; return true; } }
                    string[] pycs = Directory.GetFiles(d, "*.pyc", SearchOption.TopDirectoryOnly);
                    if (pycs != null && pycs.Length > 0) { extractDir = d; pycPath = pycs[0]; return true; }
                }
            }
            catch { }
            return false;
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
