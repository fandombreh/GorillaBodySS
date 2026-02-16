using System.Collections.Generic;
using ExitGames.Client.Photon;
using GorillaExtensions;
using GorillaBody;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace GorillaBody.Patches;

public static class Patches
{
    private static readonly List<int> TargetActorCache = new(10);
    private static readonly int[] ElbowPackedData = new int[4];
    private static readonly Quaternion BoneAlignOffset = Quaternion.Euler(0f, -90f, 0f);

    [HarmonyPatch(typeof(VRRig), nameof(VRRig.PostTick))]
    internal class VRRigPostTickPatch
    {
        private static void Postfix(VRRig __instance)
        {
            if (__instance.isOfflineVRRig)
                HandleLocalRig(__instance);
            else
                HandleRemoteRig(__instance);
        }

        private static void HandleLocalRig(VRRig rig)
        {
            var plugin = BodyTrackingClass.Instance;
            if (plugin is not { enabled: true })
                return;

            if (plugin.chestFollow == null)
                return;

            if (BodyTrackingClass.DisableMod is { Value: true })
                return;

            if (!plugin.trackersInitialized || !plugin.trackerSetUp)
                return;

            rig.transform.rotation = plugin.chestFollow.transform.rotation;

            rig.head.MapMine(rig.scaleFactor, rig.playerOffsetTransform);
            rig.rightHand.MapMine(rig.scaleFactor, rig.playerOffsetTransform);
            rig.leftHand.MapMine(rig.scaleFactor, rig.playerOffsetTransform);

            ApplyLocalElbowTracking(rig, plugin);
        }

        private static void HandleRemoteRig(VRRig rig)
        {
            if (!BodyTrackingClass.TryGetRemoteElbowData(rig, out var elbowInfo))
                return;

            if (!elbowInfo.HasData)
                return;

            if (elbowInfo.LeftUpperArmPacked != 0)
            {
                var upperRot = BitPackUtils.UnpackQuaternionFromNetwork(elbowInfo.LeftUpperArmPacked);
                var foreRot = BitPackUtils.UnpackQuaternionFromNetwork(elbowInfo.LeftForearmPacked);
                ApplyRemoteArm(rig, upperRot, foreRot, true);
            }

            if (elbowInfo.RightUpperArmPacked != 0)
            {
                var upperRot = BitPackUtils.UnpackQuaternionFromNetwork(elbowInfo.RightUpperArmPacked);
                var foreRot = BitPackUtils.UnpackQuaternionFromNetwork(elbowInfo.RightForearmPacked);
                ApplyRemoteArm(rig, upperRot, foreRot, false);
            }
        }

        private static void ApplyLocalElbowTracking(VRRig rig, BodyTrackingClass plugin)
        {
            var gorillaIK = rig.GetComponent<GorillaIK>();
            if (gorillaIK == null || gorillaIK.enabled)
                return;

            if (plugin.HasLeftElbow)
            {
                ref var result = ref plugin.GetLeftElbowResult();
                ApplyArmResult(rig, result, true);
            }

            if (plugin.HasRightElbow)
            {
                ref var result = ref plugin.GetRightElbowResult();
                ApplyArmResult(rig, result, false);
            }
        }

        private static void ApplyArmResult(VRRig rig, ElbowResult result, bool isLeft)
        {
            var rigTransform = rig.transform;

            var upperArm = rigTransform.Find(isLeft
                ? "rig/body/shoulder.L/upper_arm.L"
                : "rig/body/shoulder.R/upper_arm.R");

            if (upperArm == null) return;

            var forearm = upperArm.Find(isLeft ? "forearm.L" : "forearm.R");
            if (forearm == null) return;

            upperArm.rotation = result.UpperArmRotation * BoneAlignOffset;
            forearm.rotation = result.ForearmRotation * BoneAlignOffset;
        }

        private static void ApplyRemoteArm(VRRig rig, Quaternion upperArmRot, Quaternion forearmRot, bool isLeft)
        {
            var rigTransform = rig.transform;

            var upperArm = rigTransform.Find(isLeft
                ? "rig/body/shoulder.L/upper_arm.L"
                : "rig/body/shoulder.R/upper_arm.R");

            if (upperArm == null) return;

            var forearm = upperArm.Find(isLeft ? "forearm.L" : "forearm.R");
            if (forearm == null) return;

            upperArm.rotation = upperArmRot * BoneAlignOffset;
            forearm.rotation = forearmRot * BoneAlignOffset;
        }
    }

    [HarmonyPatch(typeof(VRRig), nameof(VRRig.SerializeWriteShared))]
    internal class VRRigSerializeWriteSharedPatches
    {
        private static void Postfix(VRRig __instance)
        {
            if (!__instance.isOfflineVRRig)
                return;

            var plugin = BodyTrackingClass.Instance;
            if (plugin is not { trackerSetUp: true, trackersInitialized: true })
                return;

            if (BodyTrackingClass.DisableMod is { Value: true })
                return;

            if (!PhotonNetwork.InRoom)
                return;

            TargetActorCache.Clear();
            foreach (var player in PhotonNetwork.PlayerList)
            {
                if (player.IsLocal) continue;

                if (player.CustomProperties is { } props &&
                    props.ContainsKey(BodyTrackingClass.Prop))
                {
                    TargetActorCache.Add(player.ActorNumber);
                }
            }

            if (TargetActorCache.Count == 0)
                return;

            var targetActors = TargetActorCache.ToArray();

            var packedRotation = BitPackUtils.PackQuaternionForNetwork(VRRig.LocalRig.transform.rotation);
            PhotonNetwork.RaiseEvent(
                BodyTrackingClass.BodyEventCode,
                packedRotation,
                new RaiseEventOptions { TargetActors = targetActors },
                SendOptions.SendUnreliable
            );

            if (plugin.HasLeftElbow || plugin.HasRightElbow)
            {
                ref var leftResult = ref plugin.GetLeftElbowResult();
                ref var rightResult = ref plugin.GetRightElbowResult();

                ElbowPackedData[0] = plugin.HasLeftElbow
                    ? BitPackUtils.PackQuaternionForNetwork(leftResult.UpperArmRotation)
                    : 0;
                ElbowPackedData[1] = plugin.HasLeftElbow
                    ? BitPackUtils.PackQuaternionForNetwork(leftResult.ForearmRotation)
                    : 0;
                ElbowPackedData[2] = plugin.HasRightElbow
                    ? BitPackUtils.PackQuaternionForNetwork(rightResult.UpperArmRotation)
                    : 0;
                ElbowPackedData[3] = plugin.HasRightElbow
                    ? BitPackUtils.PackQuaternionForNetwork(rightResult.ForearmRotation)
                    : 0;

                PhotonNetwork.RaiseEvent(
                    BodyTrackingClass.ElbowEventCode,
                    ElbowPackedData,
                    new RaiseEventOptions { TargetActors = targetActors },
                    SendOptions.SendUnreliable
                );
            }
        }
    }

    [HarmonyPatch(typeof(VRRig), nameof(VRRig.SerializeReadShared))]
    internal class VRRigSerializeReadSharedPatches
    {
        private static bool Prefix(VRRig __instance, InputStruct data)
        {
            if (!__instance.creator.GetPlayerRef().CustomProperties
                    .ContainsKey(BodyTrackingClass.Prop)) return true;

            __instance.head.syncRotation.SetValueSafe(BitPackUtils.UnpackQuaternionFromNetwork(data.headRotation));
            BitPackUtils.UnpackHandPosRotFromNetwork(data.rightHandLong, out __instance.tempVec, out __instance.tempQuat);
            __instance.rightHand.syncPos = __instance.tempVec;
            __instance.rightHand.syncRotation.SetValueSafe(in __instance.tempQuat);
            BitPackUtils.UnpackHandPosRotFromNetwork(data.leftHandLong, out __instance.tempVec, out __instance.tempQuat);
            __instance.leftHand.syncPos = __instance.tempVec;
            __instance.leftHand.syncRotation.SetValueSafe(in __instance.tempQuat);
            __instance.syncPos = BitPackUtils.UnpackWorldPosFromNetwork(data.position);
            __instance.handSync = data.handPosition;
            int packedFields = data.packedFields;
            __instance.remoteUseReplacementVoice = (packedFields & 512) != 0;
            __instance.SpeakingLoudness = (float)(packedFields >> 24 & byte.MaxValue) / byte.MaxValue;
            __instance.UpdateReplacementVoice();
            __instance.UnpackCompetitiveData(data.packedCompetitiveData);
            __instance.taggedById = data.taggedById;
            int num1 = (packedFields & 1024) != 0 ? 1 : 0;
            __instance.grabbedRopeIsPhotonView = (packedFields & 2048) != 0;
            if (num1 != 0)
            {
                __instance.grabbedRopeIndex = data.grabbedRopeIndex;
                __instance.grabbedRopeBoneIndex = data.ropeBoneIndex;
                __instance.grabbedRopeIsLeft = data.ropeGrabIsLeft;
                __instance.grabbedRopeIsBody = data.ropeGrabIsBody;
                __instance.grabbedRopeOffset.SetValueSafe(in data.ropeGrabOffset);
            }
            else
                __instance.grabbedRopeIndex = -1;
            if (num1 == 0 & (packedFields & 32768) != 0)
            {
                __instance.mountedMovingSurfaceId = data.grabbedRopeIndex;
                __instance.mountedMovingSurfaceIsLeft = data.ropeGrabIsLeft;
                __instance.mountedMovingSurfaceIsBody = data.ropeGrabIsBody;
                __instance.mountedMonkeBlockOffset.SetValueSafe(in data.ropeGrabOffset);
                __instance.movingSurfaceIsMonkeBlock = data.movingSurfaceIsMonkeBlock;
            }
            else
                __instance.mountedMovingSurfaceId = -1;
            int num2 = (packedFields & 8192) != 0 ? 1 : 0;
            bool isHeldLeftHanded = (packedFields & 16384) != 0;
            if (num2 != 0)
            {
                Vector3 localPos;
                Quaternion q;
                BitPackUtils.UnpackHandPosRotFromNetwork(data.hoverboardPosRot, out localPos, out q);
                Color boardColor = BitPackUtils.UnpackColorFromNetwork(data.hoverboardColor);
                if (q.IsValid())
                    __instance.hoverboardVisual.SetIsHeld(isHeldLeftHanded, localPos.ClampMagnitudeSafe(1f), q, boardColor);
            }
            else if (__instance.hoverboardVisual.gameObject.activeSelf)
                __instance.hoverboardVisual.SetNotHeld();
            if ((packedFields & 65536) != 0)
            {
                bool isLeftHand = (packedFields & 131072) != 0;
                Vector3 localPos;
                Quaternion handRot;
                BitPackUtils.UnpackHandPosRotFromNetwork(data.propHuntPosRot, out localPos, out handRot);
                __instance.propHuntHandFollower.SetProp(isLeftHand, localPos, handRot);
            }
            if (__instance.grabbedRopeIsPhotonView)
                __instance.localGrabOverrideBlend = -1f;
            Vector3 position = __instance.transform.position;
            __instance.leftHandLink.Read(__instance.leftHand.syncPos, __instance.syncRotation, position, data.isGroundedHand, data.isGroundedButt, (packedFields & 262144) != 0, (packedFields & 1048576) != 0, data.leftHandGrabbedActorNumber, data.leftGrabbedHandIsLeft);
            __instance.rightHandLink.Read(__instance.rightHand.syncPos, __instance.syncRotation, position, data.isGroundedHand, data.isGroundedButt, (packedFields & 524288) != 0, (packedFields & 2097152) != 0, data.rightHandGrabbedActorNumber, data.rightGrabbedHandIsLeft);
            __instance.LastTouchedGroundAtNetworkTime = data.lastTouchedGroundAtTime;
            __instance.LastHandTouchedGroundAtNetworkTime = data.lastHandTouchedGroundAtTime;
            __instance.UpdateRopeData();
            __instance.UpdateMovingMonkeBlockData();
            __instance.AddVelocityToQueue(__instance.syncPos, data.serverTimeStamp);

            return false;
        }
    }

    [HarmonyPatch(typeof(GorillaIKMgr), nameof(GorillaIK.OnEnable))]
    internal static class PatchGorillaIKMgrOnEnable
    {
        private static bool Prefix(GorillaIK __instance)
        {
            var rig = __instance.GetComponent<VRRig>();
            if (!rig) return true;

            if (rig.isOfflineVRRig) return true;

            var props = rig.OwningNetPlayer.GetPlayerRef().CustomProperties;
            if (!props.ContainsKey(BodyTrackingClass.Prop)) return true;
            return !(bool)props[BodyTrackingClass.Prop];
        }
    }
}