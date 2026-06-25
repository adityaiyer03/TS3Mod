using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TS3PerformanceMod
{
    [BepInPlugin("com.lions.ts3.performance", "TS3 Engine Optimiser", "3.1.2")]
    public class TS3Plugin : BaseUnityPlugin
    {
        internal static BepInEx.Logging.ManualLogSource Log;

        // Guaranteed writable runtime folder
        internal static string RuntimeLogDir;

        // Optional mirrors
        internal static string MirrorLogDirPrimary;
        internal static string MirrorLogDirFallback;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("[TS3Mod] Initialising 3.1.2...");

            try
            {
                RuntimeLogDir = Path.Combine(Application.temporaryCachePath, "TS3ModLogs");
                Directory.CreateDirectory(RuntimeLogDir);
                Log.LogInfo("[TS3Mod] Runtime log dir: " + RuntimeLogDir);
            }
            catch (Exception ex)
            {
                Log.LogError("[TS3Mod] Runtime log dir failed: " + ex.Message);
                RuntimeLogDir = Application.temporaryCachePath;
            }

            try
            {
                string gameRoot = Paths.GameRootPath;
                MirrorLogDirPrimary = Path.Combine(gameRoot, "BepInEx", "plugins", "TS3ModLogs");
                Directory.CreateDirectory(MirrorLogDirPrimary);
                Log.LogInfo("[TS3Mod] Mirror primary: " + MirrorLogDirPrimary);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[TS3Mod] Mirror primary unavailable: " + ex.Message);
                MirrorLogDirPrimary = null;
            }

            try
            {
                MirrorLogDirFallback = Path.Combine(Path.GetTempPath(), "TS3ModLogs");
                Directory.CreateDirectory(MirrorLogDirFallback);
                Log.LogInfo("[TS3Mod] Mirror fallback: " + MirrorLogDirFallback);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[TS3Mod] Mirror fallback unavailable: " + ex.Message);
                MirrorLogDirFallback = null;
            }

            _harmony = new Harmony("com.lions.ts3.performance.harmony");
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var t in asm.GetTypes())
                {
                    try
                    {
                        _harmony.CreateClassProcessor(t).Patch();
                    }
                    catch (Exception exType)
                    {
                        Log.LogWarning("[TS3Mod] Patch skip for " + t.FullName + ": " + exType.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[TS3Mod] Patch bootstrap failure: " + ex.Message);
            }

            ProcessOptimiser.ResetSession();

            Log.LogInfo("[TS3Mod] Harmony patches applied.");
            StartCoroutine(LogMonitor());
        }

        private IEnumerator LogMonitor()
        {
            yield return new WaitForSeconds(2f);

            while (true)
            {
                try
                {
                    if (Directory.Exists(RuntimeLogDir))
                    {
                        string[] files = Directory.GetFiles(RuntimeLogDir, "*.log", SearchOption.TopDirectoryOnly);
                        for (int i = 0; i < files.Length; i++)
                        {
                            SafeMirror(files[i]);

                            // lightweight read snapshot
                            try
                            {
                                string readPath = files[i] + ".read";
                                File.Copy(files[i], readPath, true);
                                SafeMirror(readPath);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                yield return new WaitForSeconds(2f);
            }
        }

        internal static void SafeMirror(string sourcePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;
                string name = Path.GetFileName(sourcePath);

                if (!string.IsNullOrWhiteSpace(MirrorLogDirFallback))
                {
                    try
                    {
                        Directory.CreateDirectory(MirrorLogDirFallback);
                        File.Copy(sourcePath, Path.Combine(MirrorLogDirFallback, name), true);
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(MirrorLogDirPrimary))
                {
                    try
                    {
                        Directory.CreateDirectory(MirrorLogDirPrimary);
                        File.Copy(sourcePath, Path.Combine(MirrorLogDirPrimary, name), true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    public static class ProcessOptimiser
    {
        private static string _pythonExe;
        private static readonly Dictionary<string, int> SpawnedPids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] PYTHON_CANDIDATES = new string[]
        {
            @"C:\Users\adity\AppData\Local\Programs\Python\Python311\python.exe",
            @"C:\Users\adity\AppData\Local\Programs\Python\Python312\python.exe",
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
    # immediate boot write (proves logging works)
    _pre('[TS3Mod] launcher boot')
    _pre('[TS3Mod] module=' + module_name)

    os.environ['PYTHONUNBUFFERED'] = '1'
    os.environ['PYTHONUTF8'] = '1'
    os.environ.pop('_MEIPASS', None)
    os.environ.pop('_MEIPASS2', None)

    # keep no phrase cap
    os.environ['RM_NO_PHRASE_CAP'] = '1'

    # reduce resample spikes hints (if runtime supports)
    os.environ.setdefault('RM_STREAM_FLUSH_MS', '120')
    os.environ.setdefault('RM_MAX_CHUNK_MS', '1200')
    os.environ.setdefault('RM_RESAMPLE_MODE', 'fast')

    # thread caps
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

    # Explicit RECOG/Whisper style GPU args injection (non-breaking best effort)
    lower_mod = module_name.lower()
    injected = []
    if (not force_cpu) and prefer_gpu and ('recog' in lower_mod or lower_mod == 'rm'):
        # common whisper/faster-whisper flags
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
            if (startInfo == null)
            {
                PL.W("Apply called with null startInfo");
                return;
            }

            PL.I("Apply enter. FileName=" + (startInfo.FileName ?? "<null>"));
            PL.I("Apply enter. Args=" + (startInfo.Arguments ?? "<null>"));

            if (string.IsNullOrWhiteSpace(startInfo.FileName)) return;

            string fileLower = startInfo.FileName.ToLowerInvariant();
            if (!fileLower.EndsWith(".exe"))
            {
                PL.I("Skip: not exe -> " + fileLower);
                return;
            }

            string exeName = Path.GetFileName(startInfo.FileName).ToLowerInvariant();

            // STRICT detection
            bool isRecog = exeName == "recog.exe" || exeName == "rm.exe";
            bool isCpm = exeName == "cpm.exe";
            bool isTts = exeName == "tts.exe";

            PL.I("Detected module flags: isRecog=" + isRecog + " isCpm=" + isCpm + " isTts=" + isTts);

            if (!isRecog && !isCpm && !isTts)
            {
                PL.I("Skip: not target module");
                return;
            }

            string module = Path.GetFileNameWithoutExtension(startInfo.FileName).ToLowerInvariant();
            PL.I("Intercept module=" + module);

            try
            {
                if (startInfo.UseShellExecute)
                {
                    startInfo.UseShellExecute = false;
                    startInfo.CreateNoWindow = true;
                    PL.I("UseShellExecute disabled");
                }
            }
            catch (Exception ex)
            {
                PL.W("UseShellExecute adjust failed: " + ex.Message);
            }

            // Baseline env
            try
            {
                startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
                startInfo.EnvironmentVariables["RM_NO_PHRASE_CAP"] = "1";
                startInfo.EnvironmentVariables["OMP_NUM_THREADS"] = "1";
                startInfo.EnvironmentVariables["MKL_NUM_THREADS"] = "1";
                startInfo.EnvironmentVariables["OPENBLAS_NUM_THREADS"] = "1";
                startInfo.EnvironmentVariables["NUMEXPR_NUM_THREADS"] = "1";
            }
            catch (Exception ex)
            {
                PL.W("Env baseline set failed: " + ex.Message);
            }

            // CPU/GPU policy
            if (isTts)
            {
                try
                {
                    startInfo.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = "-1";
                    startInfo.EnvironmentVariables["PYTORCH_NO_CUDA"] = "1";
                }
                catch { }
                PL.I("Module tts forced CPU");
            }
            else
            {
                try
                {
                    if (startInfo.EnvironmentVariables.ContainsKey("CUDA_VISIBLE_DEVICES") &&
                        startInfo.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] == "-1")
                        startInfo.EnvironmentVariables.Remove("CUDA_VISIBLE_DEVICES");

                    if (startInfo.EnvironmentVariables.ContainsKey("PYTORCH_NO_CUDA"))
                        startInfo.EnvironmentVariables.Remove("PYTORCH_NO_CUDA");

                    // hints only
                    startInfo.EnvironmentVariables["WHISPER_DEVICE"] = "cuda";
                    startInfo.EnvironmentVariables["FASTER_WHISPER_DEVICE"] = "cuda";
                    startInfo.EnvironmentVariables["CT2_CUDA_ALLOW_FP16"] = "1";
                }
                catch { }

                PL.I("Module " + module + " prefers GPU (env only)");
            }

            // RECOG ONLY: patch config file if --config is used
            if (isRecog)
            {
                string args = startInfo.Arguments ?? string.Empty;
                string cfgPath = TryExtractConfigPath(args);

                if (!string.IsNullOrEmpty(cfgPath) && File.Exists(cfgPath))
                {
                    try
                    {
                        PatchRecogConfigFile(cfgPath);
                        PL.I("RECOG config patched for GPU preference: " + cfgPath);
                    }
                    catch (Exception ex)
                    {
                        PL.W("RECOG config patch failed: " + ex.Message);
                    }
                }
                else
                {
                    // no --config path found => safe fallback CLI injection
                    string lower = args.ToLowerInvariant();
                    if (!lower.Contains("--config"))
                    {
                        if (!lower.Contains("--device")) args += " --device cuda";
                        if (!lower.Contains("--compute_type")) args += " --compute_type float16";
                        if (!lower.Contains("--cpu_threads")) args += " --cpu_threads 4";
                        startInfo.Arguments = args.Trim();
                        PL.I("RECOG args injected (no --config mode): " + startInfo.Arguments);
                    }
                    else
                    {
                        PL.W("RECOG --config found but path missing/unreadable; skipped injection.");
                    }
                }
            }

            // CPM/TTS remain unchanged args
            if (isCpm) PL.I("CPM args unchanged: " + (startInfo.Arguments ?? "<null>"));
            if (isTts) PL.I("TTS args unchanged: " + (startInfo.Arguments ?? "<null>"));

            PL.I("Apply exit. Final FileName=" + (startInfo.FileName ?? "<null>"));
            PL.I("Apply exit. Final Args=" + (startInfo.Arguments ?? "<null>"));
        }

        public static void TrackStarted(Process proc, ProcessStartInfo si)
        {
            try
            {
                if (proc == null || si == null) return;
                string a = (si.Arguments ?? string.Empty).ToLowerInvariant();
                string module = null;
                if (a.Contains(" \"recog\" ")) module = "recog";
                else if (a.Contains(" \"cpm\" ")) module = "cpm";
                else if (a.Contains(" \"tts\" ")) module = "tts";
                else if (a.Contains(" \"rm\" ")) module = "rm";
                if (module != null) SpawnedPids[module] = proc.Id;
            }
            catch { }
        }

        private static void KillTracked(string module)
        {
            try
            {
                int pid;
                if (!SpawnedPids.TryGetValue(module, out pid)) return;
                Process p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(500);
                }
            }
            catch { }
            finally
            {
                SpawnedPids.Remove(module);
            }
        }
        private static string TryExtractConfigPath(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return null;

            // supports: --config "path with spaces"
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
            if (!string.IsNullOrEmpty(json) && json[0] == '\uFEFF')
                json = json.TrimStart('\uFEFF');

            // Patch known keys safely
            json = UpsertJsonString(json, "device", "cuda");
            json = UpsertJsonString(json, "compute_type", "float16");
            json = UpsertJsonNumber(json, "cpu_threads", 4);

            // keep no hard phrase cap (your requirement)
            json = UpsertJsonNumber(json, "final_vad_min_silence_ms", 250);

            // Sanity check before write
            string trimmed = (json ?? "").TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.StartsWith("{"))
                throw new Exception("Patched config invalid (not JSON object).");

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
                    if (vEnd > vStart)
                        return json.Substring(0, vStart) + "\"" + value + "\"" + json.Substring(vEnd);
                }
            }

            int insert = json.LastIndexOf('}');
            if (insert > 0)
            {
                string prefix = json.Substring(0, insert).TrimEnd();
                bool hasAny = prefix.EndsWith("{") == false;
                string add = (hasAny ? "," : "") + "\n  \"" + key + "\": \"" + value + "\"\n";
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
                    if (vEnd > vStart)
                        return json.Substring(0, vStart) + value.ToString() + json.Substring(vEnd);
                }
            }

            int insert = json.LastIndexOf('}');
            if (insert > 0)
            {
                string prefix = json.Substring(0, insert).TrimEnd();
                bool hasAny = prefix.EndsWith("{") == false;
                string add = (hasAny ? "," : "") + "\n  \"" + key + "\": " + value.ToString() + "\n";
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
                try
                {
                    if (File.Exists(PYTHON_CANDIDATES[i]))
                    {
                        _pythonExe = PYTHON_CANDIDATES[i];
                        return _pythonExe;
                    }
                }
                catch { }
            }

            string path = startInfo.EnvironmentVariables.ContainsKey("PATH")
                ? startInfo.EnvironmentVariables["PATH"]
                : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

            string[] segs = path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length; i++)
            {
                try
                {
                    string p = Path.Combine(segs[i].Trim(), "python.exe");
                    if (File.Exists(p))
                    {
                        _pythonExe = p;
                        return _pythonExe;
                    }
                }
                catch { }
            }
            return null;
        }

        private static void SafeDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        private static void TouchFile(string p)
        {
            try
            {
                if (!File.Exists(p))
                {
                    using (var fs = File.Create(p)) { }
                }
            }
            catch { }
        }
        private static bool TryResolveExtractedLayout(string originalExePath, string module, out string extractDir, out string pycPath)
        {
            extractDir = null;
            pycPath = null;

            try
            {
                string exeDir = Path.GetDirectoryName(originalExePath) ?? "";
                string exeName = Path.GetFileName(originalExePath) ?? "";
                string stem = Path.GetFileNameWithoutExtension(originalExePath) ?? module;

                // Candidate extracted dirs in priority order
                string[] candDirs = new[]
                {
            originalExePath + "_extracted",                 // old logic
            Path.Combine(exeDir, exeName + "_extracted"),   // explicit
            Path.Combine(exeDir, stem + "_extracted"),      // likely real
            Path.Combine(exeDir, module + "_extracted"),    // fallback
            Path.Combine(exeDir, "_internal"),              // pyinstaller-style
        };

                for (int i = 0; i < candDirs.Length; i++)
                {
                    string d = candDirs[i];
                    if (!Directory.Exists(d)) continue;

                    // candidate pyc names
                    string[] candPyc = new[]
                    {
                Path.Combine(d, module + ".pyc"),
                Path.Combine(d, stem + ".pyc"),
            };

                    for (int j = 0; j < candPyc.Length; j++)
                    {
                        if (File.Exists(candPyc[j]))
                        {
                            extractDir = d;
                            pycPath = candPyc[j];
                            return true;
                        }
                    }

                    // last resort: first pyc in folder
                    string[] pycs = Directory.GetFiles(d, "*.pyc", SearchOption.TopDirectoryOnly);
                    if (pycs != null && pycs.Length > 0)
                    {
                        extractDir = d;
                        pycPath = pycs[0];
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }
    }

    [HarmonyPatch(typeof(Process), nameof(Process.Start), new Type[0])]
    public static class ProcessStartInstancePatch
    {
        static void Prefix(Process __instance)
        {
            PL.I("Process.Start() instance prefix hit");
            ProcessOptimiser.Apply(__instance.StartInfo);
        }

        static void Postfix(Process __instance, bool __result)
        {
            PL.I("Process.Start() instance postfix result=" + __result);
            if (!__result) return;
            try { ProcessOptimiser.TrackStarted(__instance, __instance.StartInfo); } catch (Exception ex) { PL.W("TrackStarted fail: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Process), nameof(Process.Start), new Type[] { typeof(ProcessStartInfo) })]
    public static class ProcessStartStaticPatch
    {
        static void Prefix(ProcessStartInfo startInfo)
        {
            PL.I("Process.Start(ProcessStartInfo) static prefix hit");
            ProcessOptimiser.Apply(startInfo);
        }
    }
    [HarmonyPatch(typeof(CCCAGBLOPPL), "ALEHCMBEGIK")]
    public static class PreventFolderWipeCrashPatch
    {
        static bool Prefix(string AMLONMABCGA, ref string __result)
        {
            string target = Path.Combine(Application.temporaryCachePath, AMLONMABCGA);
            __result = target;

            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
            }
            catch { }

            return false;
        }
    }

    [HarmonyPatch(typeof(TowerSpeak), "KEKENJGANAE")]
    public static class AsyncSpeechExportPatch
    {
        static bool Prefix(string OIKKDKCHIJI, string AAGJEMCLALC)
        {
            string inputData = OIKKDKCHIJI;
            string outputPath = AAGJEMCLALC;

            Task.Run(delegate
            {
                try
                {
                    string[] entries = inputData.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    StringBuilder sb = new StringBuilder(entries.Length * 16);

                    for (int i = 0; i < entries.Length; i++)
                    {
                        string e = entries[i];
                        int first = e.IndexOf(';');
                        if (first < 0) continue;
                        string word = e.Substring(first + 1).Trim();
                        int second = word.IndexOf(';');
                        if (second >= 0) word = word.Substring(0, second).Trim();
                        if (word.Length > 0 && seen.Add(word)) sb.AppendLine(word);
                    }

                    string tmp = outputPath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Move(tmp, outputPath);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[TS3Mod] Dictionary pruner failed: " + ex.Message);
                }
            });

            return false;
        }
    }

    [HarmonyPatch(typeof(CCCAGBLOPPL), "LIFOFLBGNML")]
    public static class NetworkBufferPatch
    {
        private static readonly HashSet<int> Expanded = new HashSet<int>();

        static void Prefix(CCCAGBLOPPL __instance)
        {
            try
            {
                FieldInfo[] fields = __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f.FieldType != typeof(System.Net.Sockets.TcpClient)) continue;

                    System.Net.Sockets.TcpClient tcp = f.GetValue(__instance) as System.Net.Sockets.TcpClient;
                    if (tcp == null) continue;

                    int id = tcp.GetHashCode();
                    if (Expanded.Contains(id)) return;

                    tcp.ReceiveBufferSize = 10 * 1024 * 1024;
                    tcp.SendBufferSize = 10 * 1024 * 1024;
                    Expanded.Add(id);

                    UnityEngine.Debug.Log("[TS3Mod] TCP buffer set to 10MB");
                    return;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[TS3Mod] TCP buffer patch failed: " + ex.Message);
            }
        }
    }
    internal static class PL
    {
        public static void I(string msg)
        {
            try { UnityEngine.Debug.Log("[TS3DBG] " + msg); } catch { }
            try { TS3Plugin.Log.LogInfo("[TS3DBG] " + msg); } catch { }
        }

        public static void W(string msg)
        {
            try { UnityEngine.Debug.LogWarning("[TS3DBG] " + msg); } catch { }
            try { TS3Plugin.Log.LogWarning("[TS3DBG] " + msg); } catch { }
        }

        public static void E(string msg)
        {
            try { UnityEngine.Debug.LogError("[TS3DBG] " + msg); } catch { }
            try { TS3Plugin.Log.LogError("[TS3DBG] " + msg); } catch { }
        }
    }
}