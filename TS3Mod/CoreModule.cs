using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TS3Mod.Core
{
    [BepInPlugin("com.wzrddoom.ts3.performance", "TS3 Engine Optimiser", "3.1.2")]
    public class TS3Plugin : BaseUnityPlugin
    {
        public static BepInEx.Logging.ManualLogSource SharedLogger;

        internal static string RuntimeLogDir;
        internal static string MirrorLogDirPrimary;
        internal static string MirrorLogDirFallback;

        private Harmony _harmony;

        private void Awake()
        {
            SharedLogger = Logger;
            Log.I("[TS3Mod] Initialising modular core 3.1.2...");

            try
            {
                RuntimeLogDir = Path.Combine(Application.temporaryCachePath, "TS3ModLogs");
                Directory.CreateDirectory(RuntimeLogDir);
                Log.I("[TS3Mod] Runtime log dir: " + RuntimeLogDir);
            }
            catch (Exception ex)
            {
                Log.E("[TS3Mod] Runtime log dir failed: " + ex.Message);
                RuntimeLogDir = Application.temporaryCachePath;
            }

            try
            {
                string gameRoot = Paths.GameRootPath;
                MirrorLogDirPrimary = Path.Combine(gameRoot, "BepInEx", "plugins", "TS3ModLogs");
                Directory.CreateDirectory(MirrorLogDirPrimary);
                Log.I("[TS3Mod] Mirror primary: " + MirrorLogDirPrimary);
            }
            catch (Exception ex)
            {
                Log.W("[TS3Mod] Mirror primary unavailable: " + ex.Message);
                MirrorLogDirPrimary = null;
            }

            try
            {
                MirrorLogDirFallback = Path.Combine(Path.GetTempPath(), "TS3ModLogs");
                Directory.CreateDirectory(MirrorLogDirFallback);
                Log.I("[TS3Mod] Mirror fallback: " + MirrorLogDirFallback);
            }
            catch (Exception ex)
            {
                Log.W("[TS3Mod] Mirror fallback unavailable: " + ex.Message);
                MirrorLogDirFallback = null;
            }

            _harmony = new Harmony("com.lions.ts3.performance.harmony");

            try
            {
                // In a compiled multi-project solution, you can scan all loaded assemblies 
                // that belong to your mod by matching their namespace or prefix.
                // For simplicity, we patch the current AppDomain assemblies that start with TS3Mod.
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in assemblies)
                {
                    if (asm.GetName().Name.StartsWith("TS3Mod"))
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            try { _harmony.CreateClassProcessor(t).Patch(); }
                            catch (Exception exType) { Log.W("[TS3Mod] Patch skip for " + t.FullName + ": " + exType.Message); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.E("[TS3Mod] Patch bootstrap failure: " + ex.Message);
            }

            Log.I("[TS3Mod] Harmony modular patches applied.");
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

    // Shared logger helper for all modules
    public static class Log
    {
        public static void I(string msg) { try { UnityEngine.Debug.Log("[TS3DBG] " + msg); } catch { } try { TS3Plugin.SharedLogger.LogInfo("[TS3DBG] " + msg); } catch { } }
        public static void W(string msg) { try { UnityEngine.Debug.LogWarning("[TS3DBG] " + msg); } catch { } try { TS3Plugin.SharedLogger.LogWarning("[TS3DBG] " + msg); } catch { } }
        public static void E(string msg) { try { UnityEngine.Debug.LogError("[TS3DBG] " + msg); } catch { } try { TS3Plugin.SharedLogger.LogError("[TS3DBG] " + msg); } catch { } }
    }
}
