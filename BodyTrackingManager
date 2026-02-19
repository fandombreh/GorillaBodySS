using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Valve.VR;

namespace GorillaBodyServer
{
    /// <summary>
    /// Attached to the local player. Reads SteamVR tracker data and broadcasts
    /// chest/elbow positions to ALL players via Photon RPC (server-sided).
    /// Other players render ghost limbs on their end using BodyLimbRenderer.
    /// </summary>
    public class BodyTrackingManager : MonoBehaviourPun
    {
        public static BodyTrackingManager Instance { get; private set; }

        // How often to send tracking data (times per second)
        private const float SendRate = 20f;
        private float _sendTimer;

        // SteamVR tracker references
        private SteamVR_TrackedObject _chestTracker;
        private SteamVR_TrackedObject _leftElbowTracker;
        private SteamVR_TrackedObject _rightElbowTracker;

        // Remote players' limb renderers
        private readonly Dictionary<int, BodyLimbRenderer> _remotePlayers = new Dictionary<int, BodyLimbRenderer>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            InitTrackers();
        }

        private void InitTrackers()
        {
            // Find SteamVR tracked objects assigned to chest/elbow roles
            // Adjust indices to match your SteamVR tracker assignment
            var trackers = FindObjectsByType<SteamVR_TrackedObject>(FindObjectsSortMode.None);
            foreach (var t in trackers)
            {
                // You can refine this logic based on tracker serial/role
                string name = t.gameObject.name.ToLower();
                if (name.Contains("chest"))
                    _chestTracker = t;
                else if (name.Contains("leftelbow") || name.Contains("left elbow"))
                    _leftElbowTracker = t;
                else if (name.Contains("rightelbow") || name.Contains("right elbow"))
                    _rightElbowTracker = t;
            }

            if (_chestTracker == null || _leftElbowTracker == null || _rightElbowTracker == null)
            {
                // Fallback: assign by index (chest=0, leftElbow=1, rightElbow=2)
                Plugin.Log.LogWarning("[GorillaBodyServer] Could not find named trackers. Falling back to index-based assignment.");
                var list = new List<SteamVR_TrackedObject>(trackers);
                if (list.Count > 0) _chestTracker = list[0];
                if (list.Count > 1) _leftElbowTracker = list[1];
                if (list.Count > 2) _rightElbowTracker = list[2];
            }
        }

        private void Update()
        {
            if (!PhotonNetwork.InRoom) return;
            if (!photonView.IsMine) return;

            _sendTimer += Time.deltaTime;
            if (_sendTimer >= 1f / SendRate)
            {
                _sendTimer = 0f;
                SendTrackingData();
            }
        }

        private void SendTrackingData()
        {
            if (_chestTracker == null || _leftElbowTracker == null || _rightElbowTracker == null)
                return;

            var data = new BodyTrackingData
            {
                ChestPosition = _chestTracker.transform.position,
                ChestRotation = _chestTracker.transform.rotation,

                LeftElbowPosition = _leftElbowTracker.transform.position,
                LeftElbowRotation = _leftElbowTracker.transform.rotation,

                RightElbowPosition = _rightElbowTracker.transform.position,
                RightElbowRotation = _rightElbowTracker.transform.rotation
            };

            // Send to ALL players including non-mod users
            photonView.RPC(nameof(RPC_ReceiveBodyTracking), RpcTarget.Others, data.ToRPCData());
        }

        [PunRPC]
        public void RPC_ReceiveBodyTracking(object[] rawData, PhotonMessageInfo info)
        {
            var data = BodyTrackingData.FromRPCData(rawData);
            if (data == null) return;

            int actorNumber = info.Sender.ActorNumber;

            // Get or create limb renderer for this remote player
            if (!_remotePlayers.TryGetValue(actorNumber, out var renderer))
            {
                renderer = CreateLimbRenderer(info.Sender.ActorNumber);
                _remotePlayers[actorNumber] = renderer;
            }

            renderer.UpdateLimbs(data);
        }

        private BodyLimbRenderer CreateLimbRenderer(int actorNumber)
        {
            var go = new GameObject($"BodyLimbs_Player{actorNumber}");
            DontDestroyOnLoad(go);
            return go.AddComponent<BodyLimbRenderer>();
        }

        public void RemovePlayer(int actorNumber)
        {
            if (_remotePlayers.TryGetValue(actorNumber, out var renderer))
            {
                if (renderer != null) Destroy(renderer.gameObject);
                _remotePlayers.Remove(actorNumber);
            }
        }
    }
}
