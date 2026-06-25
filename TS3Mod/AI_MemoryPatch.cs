using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using TS3Mod.Core;
using UnityEngine;

namespace TS3Mod.Memory
{
    // Scans dynamically to avoid obfuscation breaking the patch
    [HarmonyPatch]
    public static class PreventFolderWipeCrashPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            Type managerType = FindNetworkManagerType();
            if (managerType == null) yield break;

            foreach (var m in managerType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var parameters = m.GetParameters();
                if (m.ReturnType == typeof(string) && parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    yield return m;
                }
            }
        }

        static bool Prefix(string __0, ref string __result)
        {
            if (__0 != "rm" && __0 != "tts" && __0 != "cpm") return true;

            string target = Path.Combine(Application.temporaryCachePath, __0);
            __result = target;
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
            }
            catch { }

            return false;
        }

        public static Type FindNetworkManagerType()
        {
            // TowerSpeak remains unobfuscated, making it an excellent anchor
            foreach (var t in typeof(TowerSpeak).Assembly.GetTypes())
            {
                int tcpClientCount = 0;
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.FieldType == typeof(System.Net.Sockets.TcpClient)) tcpClientCount++;
                }
                if (tcpClientCount >= 2) return t;
            }
            return null;
        }
    }

    // Reduces severe RAM bloat by cleaning dictionary exports
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
                    Log.E("[TS3Mod] Dictionary pruner failed: " + ex.Message);
                }
            });

            return false;
        }
    }
}