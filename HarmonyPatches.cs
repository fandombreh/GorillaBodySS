using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GorillaBody;

public static class HarmonyPatches
{
    private const string InstanceId = PluginInfo.Guid;
    private static Harmony? _instance;

    private static bool IsPatched { get; set; }

    internal static void ApplyHarmonyPatches()
    {
        if (IsPatched) return;

        try
        {
            _instance ??= new Harmony(InstanceId);
            _instance.PatchAll(Assembly.GetExecutingAssembly());
            IsPatched = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[GorillaBody] Failed to apply Harmony patches: {e}");
        }
    }

    internal static void RemoveHarmonyPatches()
    {
        if (!IsPatched || _instance == null) return;

        try
        {
            _instance.UnpatchSelf();
            IsPatched = false;
        }
        catch (Exception e)
        {
            Debug.LogError($"[GorillaBody] Failed to remove Harmony patches: {e}");
        }
    }
}