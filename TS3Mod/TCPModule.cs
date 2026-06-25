using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS3Mod.Core;

namespace TS3Mod.Networking
{
    [HarmonyPatch]
    public static class NetworkBufferPatch
    {
        private static readonly HashSet<int> Expanded = new HashSet<int>();

        static IEnumerable<MethodBase> TargetMethods()
        {
            // We use the signature scanner from the Memory module to locate the obfuscated class
            Type managerType = TS3Mod.Memory.PreventFolderWipeCrashPatch.FindNetworkManagerType();
            if (managerType == null) yield break;

            foreach (var m in managerType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!m.IsAbstract && !m.ContainsGenericParameters)
                    yield return m;
            }
        }

        static void Prefix(object __instance)
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

                    Log.I("[TS3Mod] TCP buffer set to 10MB");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.W("[TS3Mod] Network module buffer expansion warning: " + ex.Message);
            }
        }
    }
}