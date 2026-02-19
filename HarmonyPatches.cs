using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace GorillaBodyServer
{
    /// <summary>
    /// Harmony patches to hook player join/leave and inject the BodyTrackingManager.
    /// </summary>
    public static class HarmonyPatches
    {
        private static Harmony _harmony;

        public static void ApplyPatches()
        {
            _harmony = new Harmony(PluginInfo.GUID);
            _harmony.PatchAll(typeof(HarmonyPatches));
            Plugin.Log.LogInfo("[GorillaBodyServer] Harmony patches applied.");
        }

        public static void RemovePatches()
        {
            _harmony?.UnpatchSelf();
        }

        // Patch: when the local player joins a room, spawn the BodyTrackingManager
        [HarmonyPatch(typeof(MonoBehaviourPunCallbacks), nameof(MonoBehaviourPunCallbacks.OnJoinedRoom))]
        [HarmonyPostfix]
        public static void OnJoinedRoom_Postfix()
        {
            if (BodyTrackingManager.Instance != null) return;

            var go = new UnityEngine.GameObject("GorillaBodyServer_Manager");
            UnityEngine.Object.DontDestroyOnLoad(go);

            // Add PhotonView for RPC support
            var pv = go.AddComponent<PhotonView>();
            pv.ViewID = PhotonNetwork.AllocateViewID(false);

            go.AddComponent<BodyTrackingManager>();

            Plugin.Log.LogInfo("[GorillaBodyServer] BodyTrackingManager spawned.");
        }

        // Patch: clean up limb renderers when a player leaves
        [HarmonyPatch(typeof(MonoBehaviourPunCallbacks), nameof(MonoBehaviourPunCallbacks.OnPlayerLeftRoom))]
        [HarmonyPostfix]
        public static void OnPlayerLeftRoom_Postfix(Player otherPlayer)
        {
            BodyTrackingManager.Instance?.RemovePlayer(otherPlayer.ActorNumber);
        }
    }
}
