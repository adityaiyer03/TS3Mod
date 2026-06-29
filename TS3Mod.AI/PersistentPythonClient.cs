using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Net;
using System.Threading;

namespace TS3Mod.AI
{
    public static class PersistentPythonClient
    {
        private static Process _proc;
        private static readonly object _lock = new object();
        private const int Port = 5000;
        private const string Host = "127.0.0.1";
        private static string _scriptPath;

        public static void EnsureStarted(string pythonExe = "python")
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return;

                // find script path relative to this assembly
                if (string.IsNullOrEmpty(_scriptPath))
                {
                    try
                    {
                        var asmLoc = Assembly.GetExecutingAssembly().Location;
                        var baseDir = Path.GetDirectoryName(asmLoc) ?? AppDomain.CurrentDomain.BaseDirectory;
                        _scriptPath = Path.Combine(baseDir, "worker", "persistent_worker.py");
                    }
                    catch
                    {
                        _scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker", "persistent_worker.py");
                    }
                }

                if (!File.Exists(_scriptPath))
                {
                    TS3Mod.Core.Log.W("[TS3Mod] persistent_worker.py not found at " + _scriptPath);
                    return;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = $"\"{_scriptPath}\" --host {Host} --port {Port}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    _proc = Process.Start(psi);
                    if (_proc != null)
                    {
                        _proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) TS3Mod.Core.Log.I("[worker] " + e.Data); };
                        _proc.BeginOutputReadLine();
                        _proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) TS3Mod.Core.Log.W("[worker-err] " + e.Data); };
                        _proc.BeginErrorReadLine();

                        // wait for health
                        bool ok = false;
                        int attempts = 0;
                        while (attempts < 10 && !ok)
                        {
                            attempts++;
                            try
                            {
                                var url = $"http://{Host}:{Port}/health";
                                var req = WebRequest.Create(url);
                                req.Timeout = 1000;
                                using (var resp = req.GetResponse())
                                using (var rs = new StreamReader(resp.GetResponseStream()))
                                {
                                    var txt = rs.ReadToEnd();
                                    if (!string.IsNullOrEmpty(txt) && txt.Contains("ok")) ok = true;
                                }
                            }
                            catch
                            {
                                Thread.Sleep(200);
                            }
                        }

                        if (!ok)
                        {
                            TS3Mod.Core.Log.W("[TS3Mod] Worker did not respond to health checks");
                        }
                        else
                        {
                            TS3Mod.Core.Log.I("[TS3Mod] Persistent Python worker started on port " + Port);
                        }
                    }
                }
                catch (Exception ex)
                {
                    TS3Mod.Core.Log.W("[TS3Mod] Failed to start persistent Python worker: " + ex.Message);
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try
                {
                    if (_proc != null && !_proc.HasExited)
                    {
                        try { _proc.Kill(); } catch { }
                        try { _proc.Dispose(); } catch { }
                    }
                }
                finally
                {
                    _proc = null;
                }
            }
        }

        public static string InferFromFile(string audioPath, int timeoutMs = 30000)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) throw new FileNotFoundException("audio file not found", audioPath);
            var url = $"http://{Host}:{Port}/infer";
            try
            {
                var payload = "{\"audio_path\": \"" + audioPath.Replace("\\", "\\\\") + "\"}";
                var contentBytes = Encoding.UTF8.GetBytes(payload);
                                    var req = (HttpWebRequest)WebRequest.Create(url);
                                    req.Method = "POST";
                                    req.ContentType = "application/json";
                                    req.Timeout = timeoutMs;
                                    req.ContentLength = contentBytes.Length;
                                    using (var rs = req.GetRequestStream()) { rs.Write(contentBytes, 0, contentBytes.Length); }
                                    using (var resp = (HttpWebResponse)req.GetResponse())
                                    using (var rs2 = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                                    {
                                        var respText = rs2.ReadToEnd();
                                        if (string.IsNullOrEmpty(respText)) return null;
                                        try
                                        {
                                            int idx = respText.IndexOf("\"text\":");
                                            if (idx >= 0)
                                            {
                                                int s = respText.IndexOf('"', idx + 8);
                                                if (s >= 0)
                                                {
                                                    int e = respText.IndexOf('"', s + 1);
                                                    if (e > s) return respText.Substring(s + 1, e - s - 1);
                                                }
                                            }
                                            return respText;
                                        }
                                        catch
                                        {
                                            return respText;
                                        }
                                    }
            }
            catch (Exception ex)
            {
                TS3Mod.Core.Log.W("[TS3Mod] Inference request failed: " + ex.Message);
                return null;
            }
        }
    }
}
