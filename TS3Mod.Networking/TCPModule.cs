using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;
using TS3Mod.Core;

namespace TS3Mod.Networking
{
    [HarmonyPatch]
    public static class NetworkBufferPatch
    {
        private static readonly HashSet<int> Expanded = new HashSet<int>();
        private static FieldInfo _tcpClientField = null;
        private static bool _fieldSearched = false;

        static IEnumerable<MethodBase> TargetMethods()
        {
            // We use the signature scanner from the Memory module to locate the obfuscated class
            Type managerType = TS3Mod.Memory.PreventFolderWipeCrashPatch.FindNetworkManagerType();
            if (managerType == null) yield break;

            foreach (var m in managerType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                // CRITICAL FIX: Do not hook noisy methods like Update to prevent severe Unity freezing
                if (!m.IsAbstract && !m.ContainsGenericParameters && !m.Name.Contains("Update"))
                {
                    yield return m;
                }
            }
        }

        static void Prefix(object __instance)
        {
            try
            {
                // CRITICAL FIX: Cache the reflection field. 
                // Calling GetFields() on every hooked method call tanks the framerate.
                if (!_fieldSearched)
                {
                    FieldInfo[] fields = __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (fields[i].FieldType == typeof(TcpClient))
                        {
                            _tcpClientField = fields[i];
                            break;
                        }
                    }
                    _fieldSearched = true;
                }

                if (_tcpClientField == null) return;

                TcpClient tcp = _tcpClientField.GetValue(__instance) as TcpClient;
                if (tcp == null) return;

                int id = tcp.GetHashCode();
                if (Expanded.Contains(id)) return;

                // 1. Lower buffer size. 10MB can cause Winsock exhaustion on loopback. 1MB is more than enough.
                tcp.ReceiveBufferSize = 1024 * 1024;
                tcp.SendBufferSize = 1024 * 1024;

                // 2. Prevent infinite hangs! If Python crashes, Unity won't freeze forever waiting for data.
                tcp.ReceiveTimeout = 15000;
                tcp.SendTimeout = 15000;

                // 3. Keep Nagle's enabled (NoDelay = false) to prevent fragmented JSON payloads crashing the Python server.
                tcp.NoDelay = false;

                Expanded.Add(id);

                Log.I("[TS3Mod] TCP limits applied (1MB buffers, 15s timeout, Nagle ON)");
            }
            catch (Exception ex)
            {
                Log.W("[TS3Mod] Network module buffer expansion warning: " + ex.Message);
            }
        }
    }
}