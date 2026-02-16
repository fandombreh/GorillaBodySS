using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using GorillaLocomotion;
using GorillaNetworking;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Valve.VR;

// ReSharper disable CheckNamespace
// ReSharper disable InconsistentNaming

namespace GorillaBody;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        var go = new GameObject("GorillaBody");
        DontDestroyOnLoad(go);
        go.AddComponent<BodyTrackingClass>();
        HarmonyPatches.ApplyHarmonyPatches();
    }
}

public enum TrackerRole
{
    None,
    Chest,
    LeftElbow,
    RightElbow
}

public struct ElbowResult
{
    public Vector3 ShoulderPos;
    public Vector3 ElbowPos;
    public Vector3 HandPos;
    public Quaternion UpperArmRotation;
    public Quaternion ForearmRotation;
    public Vector3 LastElbowPos;
}

public class RemoteElbowInfo
{
    public int LeftUpperArmPacked;
    public int LeftForearmPacked;
    public int RightUpperArmPacked;
    public int RightForearmPacked;
    public bool HasData;
}

public class TrackerData
{
    public uint DeviceId;
    public TrackerRole Role;
    public ETrackedDeviceClass DeviceClass;
    public string Serial = string.Empty;
    public bool IsConnected;
    public bool IsValid;

    public Vector3 RawPosition;
    public Quaternion RawRotation;

    public Vector3 SmoothedPosition;
    public Quaternion SmoothedRotation;

    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    private Vector3 _lastPosition;
    private Quaternion _lastRotation;
    private bool _initialized;

    public void UpdateSmoothing(float posSmooth, float rotSmooth)
    {
        if (!_initialized)
        {
            SnapToRaw();
            return;
        }

        if (Time.deltaTime > 0f)
        {
            Velocity = (RawPosition - _lastPosition) / Time.deltaTime;

            var deltaRot = RawRotation * Quaternion.Inverse(_lastRotation);
            deltaRot.ToAngleAxis(out var angleDeg, out var axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            if (axis.sqrMagnitude > 0.001f)
                AngularVelocity = axis.normalized * (angleDeg * Mathf.Deg2Rad / Time.deltaTime);
            else
                AngularVelocity = Vector3.zero;
        }

        _lastPosition = RawPosition;
        _lastRotation = RawRotation;

        SmoothedPosition = Vector3.Lerp(SmoothedPosition, RawPosition, Mathf.Clamp01(posSmooth * Time.deltaTime));
        SmoothedRotation = Quaternion.Slerp(SmoothedRotation, RawRotation, Mathf.Clamp01(rotSmooth * Time.deltaTime));
    }

    public float GetAdaptiveSmoothFactor(float baseFactor, float speedThreshold = 1.5f)
    {
        var speed = Velocity.magnitude;
        var angSpeed = AngularVelocity.magnitude;

        var speedBlend = Mathf.Clamp01(speed / speedThreshold);
        var angBlend = Mathf.Clamp01(angSpeed / (speedThreshold * 3f));
        var motionBlend = Mathf.Max(speedBlend, angBlend);

        return Mathf.Lerp(baseFactor, 1f, motionBlend * 0.7f);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public void SnapToRaw()
    {
        SmoothedPosition = RawPosition;
        SmoothedRotation = RawRotation;
        _lastPosition = RawPosition;
        _lastRotation = RawRotation;
        Velocity = Vector3.zero;
        AngularVelocity = Vector3.zero;
        _initialized = true;
    }
}

public static class ElbowIK
{
    private const float UpperArmRatio = 0.48f;
    private const float ForearmRatio = 0.52f;
    private const float TrackerDirectWeight = 0.85f;
    private const float TwistInfluence = 0.7f;

    public static Vector3 SolveElbow(
        Vector3 shoulder,
        Vector3 hand,
        float totalArmLength,
        Vector3? trackerPosition,
        Quaternion? trackerRotation,
        bool isLeft)
    {
        var shoulderToHand = hand - shoulder;
        var distance = shoulderToHand.magnitude;

        var upperLen = totalArmLength * UpperArmRatio;
        var foreLen = totalArmLength * ForearmRatio;
        var maxReach = upperLen + foreLen;

        if (distance >= maxReach * 0.999f)
            return shoulder + shoulderToHand.normalized * upperLen;

        if (distance < 0.02f)
        {
            var outDir = isLeft ? Vector3.left : Vector3.right;
            return shoulder + (outDir + Vector3.down * 0.3f).normalized * (upperLen * 0.5f);
        }

        var ikElbow = SolveTwoBoneIK(shoulder, hand, upperLen, foreLen, distance, isLeft);

        if (!trackerPosition.HasValue)
            return ikElbow;

        var trackerElbow = ConstrainElbowToArmLengths(
            shoulder, hand, trackerPosition.Value,
            upperLen, foreLen, distance
        );

        if (trackerRotation.HasValue)
        {
            var trackerForward = trackerRotation.Value * Vector3.forward;
            var forearmDir = (hand - trackerElbow).normalized;
            var misalignment = Vector3.Dot(trackerForward, forearmDir);

            if (Mathf.Abs(misalignment) < 0.95f)
            {
                var correctionDir = Vector3.Cross(forearmDir, trackerForward).normalized;
                var correction = correctionDir * (upperLen * 0.15f * (1f - Mathf.Abs(misalignment)));
                trackerElbow += correction;

                trackerElbow = ConstrainElbowToArmLengths(
                    shoulder, hand, trackerElbow,
                    upperLen, foreLen, distance
                );
            }
        }

        return Vector3.Lerp(ikElbow, trackerElbow, TrackerDirectWeight);
    }

    private static Vector3 ConstrainElbowToArmLengths(
        Vector3 shoulder,
        Vector3 hand,
        Vector3 desiredElbow,
        float upperLen,
        float foreLen,
        float shoulderHandDist)
    {
        var axis = (hand - shoulder).normalized;

        var cosAngle = Mathf.Clamp(
            (upperLen * upperLen + shoulderHandDist * shoulderHandDist - foreLen * foreLen)
            / (2f * upperLen * shoulderHandDist),
            -1f, 1f);
        var projDist = upperLen * cosAngle;

        var circleCenter = shoulder + axis * projDist;

        var sinAngle = Mathf.Sqrt(Mathf.Max(0f, 1f - cosAngle * cosAngle));
        var circleRadius = upperLen * sinAngle;

        if (circleRadius < 0.001f)
            return circleCenter;

        var toDesired = desiredElbow - circleCenter;
        var onPlane = toDesired - Vector3.Dot(toDesired, axis) * axis;

        if (onPlane.sqrMagnitude < 0.0001f)
        {
            onPlane = Vector3.Cross(axis, Vector3.up);
            if (onPlane.sqrMagnitude < 0.001f)
                onPlane = Vector3.Cross(axis, Vector3.forward);
        }

        return circleCenter + onPlane.normalized * circleRadius;
    }

    private static Vector3 SolveTwoBoneIK(
        Vector3 shoulder,
        Vector3 hand,
        float upperLen,
        float foreLen,
        float distance,
        bool isLeft)
    {
        var cosAngle = Mathf.Clamp(
            (upperLen * upperLen + distance * distance - foreLen * foreLen)
            / (2f * upperLen * distance),
            -1f, 1f
        );
        var angle = Mathf.Acos(cosAngle);

        var forward = (hand - shoulder).normalized;
        var poleVector = BuildNaturalPoleVector(forward, isLeft);

        var elbowPlaneNormal = Vector3.Cross(forward, poleVector).normalized;

        if (elbowPlaneNormal.sqrMagnitude < 0.001f)
        {
            elbowPlaneNormal = Vector3.Cross(forward, Vector3.up).normalized;
            if (elbowPlaneNormal.sqrMagnitude < 0.001f)
                elbowPlaneNormal = Vector3.Cross(forward, Vector3.forward).normalized;
        }

        var elbowDir = Quaternion.AngleAxis(-angle * Mathf.Rad2Deg, elbowPlaneNormal) * forward;

        return shoulder + elbowDir * upperLen;
    }

    private static Vector3 BuildNaturalPoleVector(Vector3 armForward, bool isLeft)
    {
        var side = isLeft ? -1f : 1f;

        var armUp = Vector3.Dot(armForward, Vector3.up);

        var downBias = Mathf.Lerp(0.3f, 0.8f, Mathf.Clamp01(armUp + 0.5f));
        var backBias = Mathf.Lerp(0.6f, 0.1f, Mathf.Clamp01(armUp + 0.5f));

        var forwardReach = Mathf.Clamp01(Vector3.Dot(armForward, Vector3.forward));
        var sideBias = Mathf.Lerp(0.2f, 0.5f, forwardReach);

        var pole = Vector3.down * downBias
                   + -Vector3.forward * backBias
                   + new Vector3(side * sideBias, 0f, 0f);

        var perpendicular = Vector3.Cross(armForward, pole.normalized);
        if (perpendicular.sqrMagnitude < 0.001f)
            perpendicular = Vector3.Cross(armForward, Vector3.up);

        return Vector3.Cross(perpendicular, armForward).normalized;
    }

    public static Quaternion SolveElbowRotation(
        Vector3 elbow,
        Vector3 hand,
        Vector3 shoulder,
        Quaternion? trackerRotation)
    {
        var forearmDir = hand - elbow;
        if (forearmDir.sqrMagnitude < 0.0001f)
            return Quaternion.identity;

        forearmDir.Normalize();

        var upperArmDir = elbow - shoulder;
        if (upperArmDir.sqrMagnitude < 0.0001f)
            upperArmDir = Vector3.up;
        else
            upperArmDir.Normalize();

        var bendNormal = Vector3.Cross(upperArmDir, forearmDir).normalized;
        var elbowUp = Vector3.Cross(forearmDir, bendNormal).normalized;

        if (elbowUp.sqrMagnitude < 0.001f)
            elbowUp = Vector3.up;

        var baseRotation = Quaternion.LookRotation(forearmDir, elbowUp);

        if (!trackerRotation.HasValue)
            return baseRotation;

        var trackerUp = trackerRotation.Value * Vector3.up;
        var projectedUp = Vector3.ProjectOnPlane(trackerUp, forearmDir);

        if (projectedUp.sqrMagnitude < 0.001f)
            return baseRotation;

        projectedUp.Normalize();

        var twistedRotation = Quaternion.LookRotation(forearmDir, projectedUp);

        return Quaternion.Slerp(baseRotation, twistedRotation, TwistInfluence);
    }

    public static Quaternion SolveUpperArmRotation(
        Vector3 shoulder,
        Vector3 elbow,
        Vector3 hand,
        Quaternion bodyRotation,
        bool isLeft)
    {
        var upperArmDir = elbow - shoulder;
        if (upperArmDir.sqrMagnitude < 0.0001f)
            return bodyRotation;

        upperArmDir.Normalize();

        var forearmDir = (hand - elbow).normalized;
        var bendNormal = Vector3.Cross(upperArmDir, forearmDir).normalized;

        if (bendNormal.sqrMagnitude < 0.001f)
        {
            bendNormal = Vector3.Cross(upperArmDir, bodyRotation * Vector3.up).normalized;
            if (bendNormal.sqrMagnitude < 0.001f)
                bendNormal = Vector3.Cross(upperArmDir, Vector3.up).normalized;
        }

        var upperArmUp = Vector3.Cross(bendNormal, upperArmDir).normalized;

        if (upperArmUp.sqrMagnitude < 0.001f)
            upperArmUp = bodyRotation * Vector3.up;

        return Quaternion.LookRotation(upperArmDir, upperArmUp);
    }
}

public class BodyTrackingClass : MonoBehaviour
{
    private static readonly Quaternion FlipCorrection = Quaternion.Euler(0f, 180f, 0f);

    #region Update Loop

    private void Update()
    {
        if (!XRSettings.isDeviceActive)
            return;

        if (DisableMod is { Value: true })
        {
            _activeDeviceCount = 0;
            return;
        }

        GetTrackedDevices();
        UpdateTrackerSmoothing();

        if (!trackersInitialized && _activeDeviceCount >= 2 && _firstSetup is { Value: true })
            trackersInitialized = true;

        if (!trackerSetUp && trackersInitialized)
            SetUpChestTracker();

        if (!trackersInitialized || !trackerSetUp || _firstSetup is not { Value: true })
            return;

        UpdateChestTracking();

        if (_elbowTrackingSetting is { Value: true })
            UpdateElbowTracking();
    }

    private void UpdateChestTracking()
    {
        var chestTracker = GetTrackerByRole(TrackerRole.Chest);
        if (chestTracker is not { IsConnected: true, IsValid: true })
            return;

        trackerGo.transform.localRotation = chestTracker.SmoothedRotation * FlipCorrection;
        trackerGo.transform.position = chestTracker.SmoothedPosition;
    }

    private void UpdateElbowTracking()
    {
        if (VRRig.LocalRig == null) return;

        var rig = VRRig.LocalRig;
        _trackerParent.transform.position = rig.transform.position;

        var leftTracker = GetTrackerByRole(TrackerRole.LeftElbow);
        var rightTracker = GetTrackerByRole(TrackerRole.RightElbow);

        var scaleFactor = rig.scaleFactor;
        var armLength = BaseArmLength * scaleFactor;

        var bodyRot = chestFollow != null
            ? chestFollow.transform.rotation
            : rig.transform.rotation;

        if (leftTracker is { IsConnected: true, IsValid: true })
        {
            SolveArm(rig, leftTracker, armLength, bodyRot, true, _leftElbowTarget, ref _leftElbowResult);
            _hasLeftElbow = true;
        }
        else
        {
            _hasLeftElbow = false;
        }

        if (rightTracker is { IsConnected: true, IsValid: true })
        {
            SolveArm(rig, rightTracker, armLength, bodyRot, false, _rightElbowTarget, ref _rightElbowResult);
            _hasRightElbow = true;
        }
        else
        {
            _hasRightElbow = false;
        }
    }

    private void SolveArm(
        VRRig rig,
        TrackerData tracker,
        float armLength,
        Quaternion bodyRotation,
        bool isLeft,
        Transform elbowTarget,
        ref ElbowResult result)
    {
        var shoulderPos = GetShoulderPosition(rig, bodyRotation, isLeft);
        var handPos = GetHandWorldPosition(rig, isLeft);

        var baseFactor = _elbowSmoothing?.Value ?? 12f;
        var adaptiveFactor = tracker.GetAdaptiveSmoothFactor(baseFactor * Time.deltaTime);

        var smoothedPos = Vector3.Lerp(
            result.LastElbowPos != Vector3.zero ? result.LastElbowPos : tracker.SmoothedPosition,
            tracker.SmoothedPosition,
            Mathf.Clamp01(adaptiveFactor)
        );

        var elbowPos = ElbowIK.SolveElbow(
            shoulderPos, handPos, armLength,
            smoothedPos,
            tracker.SmoothedRotation,
            isLeft
        );

        var upperArmRot = ElbowIK.SolveUpperArmRotation(
            shoulderPos, elbowPos, handPos, bodyRotation, isLeft
        );

        var elbowRot = ElbowIK.SolveElbowRotation(
            elbowPos, handPos, shoulderPos,
            tracker.SmoothedRotation
        );

        result.ShoulderPos = shoulderPos;
        result.ElbowPos = elbowPos;
        result.HandPos = handPos;
        result.UpperArmRotation = upperArmRot;
        result.ForearmRotation = elbowRot;
        result.LastElbowPos = elbowPos;

        elbowTarget.position = elbowPos;
        elbowTarget.rotation = elbowRot;
    }

    private static Vector3 GetHandWorldPosition(VRRig rig, bool isLeft)
    {
        var vrMap = isLeft ? rig.leftHand : rig.rightHand;

        if (vrMap.overrideTarget != null)
            return vrMap.overrideTarget.position;

        if (vrMap.rigTarget != null)
            return vrMap.rigTarget.position;

        return rig.transform.TransformPoint(vrMap.syncPos);
    }

    private Vector3 GetShoulderPosition(VRRig rig, Quaternion bodyRotation, bool isLeft)
    {
        var chestTracker = GetTrackerByRole(TrackerRole.Chest);
        var shoulderDir = isLeft ? Vector3.left : Vector3.right;
        var scaleFactor = rig.scaleFactor;

        if (chestTracker is not { IsValid: true })
        {
            var headPos = rig.head.rigTarget != null
                ? rig.head.rigTarget.position
                : rig.transform.position + Vector3.up * (0.5f * scaleFactor);

            return headPos
                   + bodyRotation * shoulderDir * (ShoulderWidth * scaleFactor)
                   + Vector3.down * (ShoulderDrop * scaleFactor);
        }

        var chestPos = chestTracker.SmoothedPosition;
        var chestRot = chestTracker.SmoothedRotation * FlipCorrection;

        return chestPos
               + chestRot * shoulderDir * (ShoulderWidth * scaleFactor)
               + Vector3.up * (ShoulderRaise * scaleFactor);
    }

    #endregion

    #region Constants & Caches

    private const int MaxDevices = 64;
    private static readonly Color HeadsetBtnColor = new(1f, 0.76f, 0.25f);
    private static readonly Color ChestBtnColor = new(0.25f, 1f, 0.5f);
    private static readonly Color LeftElbowBtnColor = new(0.5f, 0.7f, 1f);
    private static readonly Color RightElbowBtnColor = new(1f, 0.5f, 0.5f);
    private static readonly Color AssignedColor = new(0.3f, 1f, 0.3f);
    private static readonly WaitForSeconds Wait3Seconds = new(3f);

    private readonly TrackedDevicePose_t[] _poseCache = new TrackedDevicePose_t[MaxDevices];
    private readonly TrackerData[] _trackerData = new TrackerData[MaxDevices];
    private readonly uint[] _activeDeviceIds = new uint[MaxDevices];
    private int _activeDeviceCount;

    private Transform _leftElbowTarget = null!;
    private Transform _rightElbowTarget = null!;
    private bool _hasLeftElbow;
    private bool _hasRightElbow;

    private ElbowResult _leftElbowResult;
    private ElbowResult _rightElbowResult;

    private const float BaseArmLength = 0.65f;
    private const float ShoulderWidth = 0.15f;
    private const float ShoulderDrop = 0.05f;
    private const float ShoulderRaise = 0.1f;

    private static readonly GUIContent GcHeader = new("GORILLA BODY SETTINGS");
    private static readonly GUIContent GcNoTrackers1 = new("<color=Red>NO TRACKERS DETECTED OR IS DISABLED</color>");
    private static readonly GUIContent GcNoTrackers2 = new("<color=Red>CHEST OBJECT NOT MADE-TELL TO GRAZE</color>");
    private static readonly GUIContent YouWillStillSeeOthers = new("YOU WILL STILL SEE OTHERS");
    private static readonly GUIContent WhoHaveTheModColonThree = new("WHO HAVE THE MOD :3");
    private static readonly GUIContent RTTrackerL = new("ROTATE TRACKER LEFT");
    private static readonly GUIContent RTTrackerD = new("ROTATE TRACKER DOWN");
    private static readonly GUIContent RTTrackerU = new("ROTATE TRACKER UP");
    private static readonly GUIContent RTTrackerR = new("ROTATE TRACKER RIGHT");
    private static readonly GUIContent DisableTracking = new("DISABLE TRACKING");
    private static readonly GUIContent ElbowTrackingLabel = new("ENABLE ELBOW TRACKING");
    private static readonly GUIContent ElbowTrackingCoolDown = new("TOGGLING...");
    private static readonly GUIContent PressB = new("PRESS <color=#FFC23F>B KEY 3(*)</color> TIMES");
    private static readonly GUIContent ByGraze = new($"BY GRAZE.({PluginInfo.Version})");
    private static readonly GUIContent SetAsChest = new("SET AS CHEST");
    private static readonly GUIContent SetAsLeftElbow = new("SET AS LEFT ELBOW");
    private static readonly GUIContent SetAsRightElbow = new("SET AS RIGHT ELBOW");
    private static readonly GUIContent ClearRoleLabel = new("CLEAR ASSIGNMENT");
    private static readonly GUIContent HeadsetMode = new("HEADSET(FANGAME MODE)");
    private static readonly GUIContent NotinVr = new("<color=Red>YOU ARE NOT IN VR</color>");
    private static readonly GUIContent SmoothingLabel = new("CHEST SMOOTHING");
    private static readonly GUIContent ElbowSmoothingLabel = new("ELBOW SMOOTHING");
    private static readonly GUIContent AutoAssignLabel = new("AUTO-ASSIGN TRACKERS");

    #endregion

    #region Fields and Properties

    public static BodyTrackingClass? Instance { get; private set; }

    private static GUISkin? _guiSkin;
    private static GUIStyle? _smallLabel;

    public bool trackerSetUp, trackersInitialized, inSetup;
    public GameObject? chestFollow;

    public readonly List<TrackedDevicePose_t> TruePoses = new(MaxDevices);

    private float _repeatTimer;
    private int _settingsRepeat;
    private int _mirrorOnly;
    private bool _toggledThisCycle;
    private static bool _settingToggleCooldown;
    public const string Prop = "GorillaBody " + PluginInfo.Version;
    public const byte BodyEventCode = 189;
    public const byte ElbowEventCode = 190;

    public GameObject trackerGo = null!;
    private GameObject _trackerParent = null!;
    private Vector2 _scrollPos;

    private readonly StringBuilder _serialBuilder = new(128);

    private static ConfigEntry<bool>? _firstSetup;
    private static ConfigEntry<uint>? _chestDeviceId;
    private static ConfigEntry<uint>? _leftElbowDeviceId;
    private static ConfigEntry<uint>? _rightElbowDeviceId;
    private static ConfigEntry<Quaternion>? _trackerOffset;
    public static ConfigEntry<bool>? DisableMod;
    private static ConfigEntry<bool>? _elbowTrackingSetting;
    private static ConfigEntry<float>? _smoothingAmount;
    private static ConfigEntry<float>? _elbowSmoothing;

    private static readonly Dictionary<VRRig, RemoteElbowInfo> RemoteElbowDataMap = new();

    #endregion

    #region Initialization

    private void Awake()
    {
        Instance = this;
        var config = new ConfigFile(Path.Combine(Paths.ConfigPath, "Graze.GorillaBody.cfg"), true);

        _chestDeviceId = config.Bind("Setup", "Chest Tracker Device ID", uint.MaxValue,
            "OpenVR device ID of the chest tracker");
        _leftElbowDeviceId = config.Bind("Setup", "Left Elbow Tracker Device ID", uint.MaxValue,
            "OpenVR device ID of the left elbow tracker");
        _rightElbowDeviceId = config.Bind("Setup", "Right Elbow Tracker Device ID", uint.MaxValue,
            "OpenVR device ID of the right elbow tracker");
        _firstSetup = config.Bind("Setup", "Setup/Tutorial", false,
            "Have you gone through the first Setup/Tutorial?");
        DisableMod = config.Bind("Settings", "Disable Tracking", false,
            "Enable or Disable using the tracking but still see leaning from others");
        _elbowTrackingSetting = config.Bind("Settings", "Elbow Tracking", false,
            "Enable or Disable using Elbow Tracking");
        _trackerOffset = config.Bind("Setup", "Tracker Rotation Offset", Quaternion.Euler(Vector3.zero),
            "Saved rotation set by the user");
        _smoothingAmount = config.Bind("Settings", "Chest Smoothing", 15f,
            "Chest tracker smoothing (higher = smoother but more latency)");
        _elbowSmoothing = config.Bind("Settings", "Elbow Smoothing", 12f,
            "Elbow tracker smoothing (lower = more responsive, motion-adaptive)");

        config.SettingChanged += SettingChange;
        config.SaveOnConfigSet = true;

        for (var i = 0; i < MaxDevices; i++)
            _trackerData[i] = new TrackerData { DeviceId = (uint)i };

        RestoreRoleAssignments();
    }

    private void RestoreRoleAssignments()
    {
        if (_chestDeviceId is { Value: < MaxDevices })
            _trackerData[_chestDeviceId.Value].Role = TrackerRole.Chest;

        if (_leftElbowDeviceId is { Value: < MaxDevices })
            _trackerData[_leftElbowDeviceId.Value].Role = TrackerRole.LeftElbow;

        if (_rightElbowDeviceId is { Value: < MaxDevices })
            _trackerData[_rightElbowDeviceId.Value].Role = TrackerRole.RightElbow;
    }

    private static void SettingChange(object sender, SettingChangedEventArgs e)
    {
        if (e.ChangedSetting.Definition.Key != "Elbow Tracking") return;

        var gorillaIK = VRRig.LocalRig?.GetComponent<GorillaIK>();
        if (gorillaIK != null)
            gorillaIK.enabled = !(bool)e.ChangedSetting.BoxedValue;

        if (GTPlayer.Instance != null)
            GTPlayer.Instance.StartCoroutine(WaitToUpdateProp());
    }

    private void Start()
    {
        GorillaTagger.OnPlayerSpawned(() =>
        {
            _trackerParent = new GameObject("GorillaBody Parent");
            _trackerParent.transform.SetParent(GTPlayer.Instance.turnParent.transform, false);
            _trackerParent.transform.localPosition = Vector3.zero;
            _trackerParent.transform.localRotation = Quaternion.identity;

            trackerGo = new GameObject("ChestTracker");
            trackerGo.transform.SetParent(_trackerParent.transform, false);
            trackerGo.transform.localPosition = Vector3.zero;

            var leftElbowGo = new GameObject("LeftElbowTarget");
            leftElbowGo.transform.SetParent(_trackerParent.transform, false);
            _leftElbowTarget = leftElbowGo.transform;

            var rightElbowGo = new GameObject("RightElbowTarget");
            rightElbowGo.transform.SetParent(_trackerParent.transform, false);
            _rightElbowTarget = rightElbowGo.transform;

            _guiSkin = Resources.FindObjectsOfTypeAll<GUISkin>().FirstOrDefault(s => s.name == "Drone GUISkin");
            if (_guiSkin != null)
            {
                _smallLabel = new GUIStyle(_guiSkin.label)
                {
                    fontSize = Mathf.Max(10, _guiSkin.label.fontSize - 4),
                    wordWrap = false
                };
            }

            _mirrorOnly = LayerMask.NameToLayer("MirrorOnly");
            CosmeticsV2Spawner_Dirty.OnPostInstantiateAllPrefabs2 += LayerChangeCosmetics;

            var gorillaIK = VRRig.LocalRig?.GetComponent<GorillaIK>();
            if (gorillaIK != null && _elbowTrackingSetting != null)
                gorillaIK.enabled = !_elbowTrackingSetting.Value;
        });
    }

    private static void LayerChangeCosmetics()
    {
        if (GorillaTagger.Instance == null) return;

        var headCosmetics = GorillaTagger.Instance.mainCamera?.transform.Find("HeadCosmetics");
        if (headCosmetics != null)
        {
            foreach (Transform cos in headCosmetics)
                LoopLayerSet(cos);
        }

        var offlineRig = GorillaTagger.Instance.offlineVRRig;
        if (offlineRig?.cosmetics != null)
        {
            foreach (var cos in offlineRig.cosmetics)
            {
                if (cos != null && cos.transform.parent is { } parent && parent.name == "head")
                    LoopLayerSet(cos.transform);
            }
        }

        PhotonNetwork.NetworkingClient.EventReceived += EventReceived;

        if (GTPlayer.Instance != null)
            GTPlayer.Instance.StartCoroutine(WaitToUpdateProp());
    }

    private static void LoopLayerSet(Transform target)
    {
        if (Instance == null) return;

        target.gameObject.layer = Instance._mirrorOnly;

        if (target.TryGetComponent(out Collider c))
            Destroy(c);

        foreach (Transform child in target)
            LoopLayerSet(child);
    }

    private static IEnumerator WaitToUpdateProp()
    {
        _settingToggleCooldown = true;
        yield return Wait3Seconds;

        if (PhotonNetwork.LocalPlayer != null && _elbowTrackingSetting != null)
        {
            var table = PhotonNetwork.LocalPlayer.CustomProperties;
            table.AddOrUpdate(Prop, _elbowTrackingSetting.Value);
            PhotonNetwork.LocalPlayer.SetCustomProperties(table);
        }

        _settingToggleCooldown = false;
    }

    private static void EventReceived(EventData data)
    {
        if (data.CustomData == null) return;

        var room = PhotonNetwork.NetworkingClient?.CurrentRoom;
        if (room == null) return;

        var sender = room.GetPlayer(data.Sender);
        if (sender == null || GorillaGameManager.instance == null) return;

        var rig = GorillaGameManager.instance.FindPlayerVRRig(sender);
        if (rig == null) return;

        switch (data.Code)
        {
            case BodyEventCode when data.CustomData is int bodyData:
                rig.syncRotation = BitPackUtils.UnpackQuaternionFromNetwork(bodyData);
                break;

            case ElbowEventCode when data.CustomData is int[] { Length: >= 4 } elbowData:
            {
                if (!RemoteElbowDataMap.TryGetValue(rig, out var info))
                {
                    info = new RemoteElbowInfo();
                    RemoteElbowDataMap[rig] = info;
                }

                info.LeftUpperArmPacked = elbowData[0];
                info.LeftForearmPacked = elbowData[1];
                info.RightUpperArmPacked = elbowData[2];
                info.RightForearmPacked = elbowData[3];
                info.HasData = true;
                break;
            }
        }
    }

    public static bool TryGetRemoteElbowData(VRRig rig, out RemoteElbowInfo info)
    {
        return RemoteElbowDataMap.TryGetValue(rig, out info);
    }

    #endregion

    #region Tracker Management

    private void GetTrackedDevices()
    {
        if (OpenVR.System == null)
            return;

        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding,
            0,
            _poseCache
        );

        _activeDeviceCount = 0;
        TruePoses.Clear();

        for (uint i = 0; i < MaxDevices; i++)
        {
            ref var pose = ref _poseCache[i];
            var tracker = _trackerData[i];
            tracker.IsConnected = false;
            tracker.IsValid = false;

            if (!pose.bDeviceIsConnected)
                continue;

            var dClass = OpenVR.System.GetTrackedDeviceClass(i);
            tracker.DeviceClass = dClass;

            if (dClass == ETrackedDeviceClass.TrackingReference)
                continue;

            tracker.IsConnected = true;
            tracker.IsValid = pose.bPoseIsValid;

            if (pose.bPoseIsValid)
            {
                tracker.RawRotation = ConvertSteamVRMatrixToUnityRotation(pose.mDeviceToAbsoluteTracking);
                tracker.RawPosition = GetUnityPosition(pose.mDeviceToAbsoluteTracking);
            }

            _activeDeviceIds[_activeDeviceCount] = i;
            _activeDeviceCount++;
            TruePoses.Add(pose);

            if (string.IsNullOrEmpty(tracker.Serial))
                tracker.Serial = GetDeviceSerial(i);
        }
    }

    private void UpdateTrackerSmoothing()
    {
        var chestSmooth = _smoothingAmount?.Value ?? 15f;
        var elbowSmooth = _elbowSmoothing?.Value ?? 12f;

        for (var i = 0; i < _activeDeviceCount; i++)
        {
            var tracker = _trackerData[_activeDeviceIds[i]];
            if (!tracker.IsValid) continue;

            var smooth = tracker.Role is TrackerRole.LeftElbow or TrackerRole.RightElbow
                ? elbowSmooth
                : chestSmooth;

            tracker.UpdateSmoothing(smooth, smooth);
        }
    }

    private TrackerData? GetTrackerByRole(TrackerRole role)
    {
        for (var i = 0; i < _activeDeviceCount; i++)
        {
            var tracker = _trackerData[_activeDeviceIds[i]];
            if (tracker.Role == role)
                return tracker;
        }
        return null;
    }

    private void AssignRole(uint deviceId, TrackerRole role)
    {
        for (var i = 0; i < MaxDevices; i++)
        {
            if (_trackerData[i].Role == role)
                _trackerData[i].Role = TrackerRole.None;
        }

        _trackerData[deviceId].Role = role;

        switch (role)
        {
            case TrackerRole.Chest:
                if (_chestDeviceId != null) _chestDeviceId.Value = deviceId;
                break;
            case TrackerRole.LeftElbow:
                if (_leftElbowDeviceId != null) _leftElbowDeviceId.Value = deviceId;
                break;
            case TrackerRole.RightElbow:
                if (_rightElbowDeviceId != null) _rightElbowDeviceId.Value = deviceId;
                break;
            case TrackerRole.None:
                ClearDeviceFromConfig(deviceId);
                break;
        }
    }

    private void ClearRole(uint deviceId)
    {
        _trackerData[deviceId].Role = TrackerRole.None;
        ClearDeviceFromConfig(deviceId);
    }

    private static void ClearDeviceFromConfig(uint deviceId)
    {
        if (_chestDeviceId is not null && _chestDeviceId.Value == deviceId)
            _chestDeviceId.Value = uint.MaxValue;
        if (_leftElbowDeviceId is not null && _leftElbowDeviceId.Value == deviceId)
            _leftElbowDeviceId.Value = uint.MaxValue;
        if (_rightElbowDeviceId is not null && _rightElbowDeviceId.Value == deviceId)
            _rightElbowDeviceId.Value = uint.MaxValue;
    }

    private void AutoAssignTrackers()
    {
        for (var i = 0; i < MaxDevices; i++)
            _trackerData[i].Role = TrackerRole.None;

        var trackers = new List<TrackerData>();
        for (var i = 0; i < _activeDeviceCount; i++)
        {
            var tracker = _trackerData[_activeDeviceIds[i]];
            if (tracker is { DeviceClass: ETrackedDeviceClass.GenericTracker, IsValid: true })
                trackers.Add(tracker);
        }

        if (trackers.Count == 0) return;

        var hmdPos = Vector3.zero;
        var hmdRot = Quaternion.identity;
        for (var i = 0; i < _activeDeviceCount; i++)
        {
            var tracker = _trackerData[_activeDeviceIds[i]];
            if (tracker is { DeviceClass: ETrackedDeviceClass.HMD, IsValid: true })
            {
                hmdPos = tracker.RawPosition;
                hmdRot = tracker.RawRotation;
                break;
            }
        }

        if (trackers.Count >= 1)
        {
            var chestTarget = hmdPos + Vector3.down * 0.3f;
            TrackerData? bestChest = null;
            var bestDist = float.MaxValue;

            foreach (var t in trackers)
            {
                var dist = Vector3.Distance(t.RawPosition, chestTarget);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestChest = t;
                }
            }

            if (bestChest != null)
            {
                AssignRole(bestChest.DeviceId, TrackerRole.Chest);
                trackers.Remove(bestChest);
            }
        }

        if (trackers.Count >= 2)
        {
            var hmdRight = hmdRot * Vector3.right;
            TrackerData? leftMost = null, rightMost = null;
            var leftDot = float.MaxValue;
            var rightDot = float.MinValue;

            foreach (var t in trackers)
            {
                var dot = Vector3.Dot(t.RawPosition - hmdPos, hmdRight);
                if (dot < leftDot) { leftDot = dot; leftMost = t; }
                if (dot > rightDot) { rightDot = dot; rightMost = t; }
            }

            if (leftMost != null && leftMost != rightMost)
                AssignRole(leftMost.DeviceId, TrackerRole.LeftElbow);
            if (rightMost != null && rightMost != leftMost)
                AssignRole(rightMost.DeviceId, TrackerRole.RightElbow);
        }
        else if (trackers.Count == 1)
        {
            var t = trackers[0];
            var dot = Vector3.Dot(t.RawPosition - hmdPos, hmdRot * Vector3.right);
            AssignRole(t.DeviceId, dot < 0 ? TrackerRole.LeftElbow : TrackerRole.RightElbow);
        }
    }

    private void SetUpChestTracker()
    {
        if (trackerGo == null) return;

        chestFollow = new GameObject("ChestFollower");
        chestFollow.transform.SetParent(trackerGo.transform, false);
        chestFollow.transform.localPosition = Vector3.zero;

        if (_firstSetup is { Value: true } && _trackerOffset != null)
            chestFollow.transform.localRotation = _trackerOffset.Value;

        trackerSetUp = true;
    }

    #endregion

    #region GUI & Input

    private void OnGUI()
    {
        HandleSetupToggle();

        if (!XRSettings.isDeviceActive)
            NoVrGUI();
        else
            DrawSetupMenu();
    }

    private void NoVrGUI()
    {
        if (!inSetup || _guiSkin == null || _smallLabel == null) return;

        GUILayout.BeginVertical(_guiSkin.box);
        GUILayout.Label(GcHeader, _guiSkin.label);
        GUILayout.Label(NotinVr, _guiSkin.label);
        GUILayout.Label(YouWillStillSeeOthers, _smallLabel);
        GUILayout.Label(WhoHaveTheModColonThree, _smallLabel);
        GUILayout.Space(10);
        GUILayout.Label(PressB, _guiSkin.label);
        GUILayout.Label(ByGraze, _guiSkin.label);
        GUILayout.EndVertical();
    }

    private void HandleSetupToggle()
    {
        if (Keyboard.current is { bKey.wasPressedThisFrame: true })
            _settingsRepeat = Mathf.Min(_settingsRepeat + 1, 3);

        if (_settingsRepeat > 0)
        {
            _repeatTimer += Time.deltaTime;
            if (_repeatTimer >= 1f)
            {
                _settingsRepeat--;
                _repeatTimer = 0f;
                _toggledThisCycle = false;
            }
        }

        if (_settingsRepeat != 3 || _toggledThisCycle) return;

        inSetup = !inSetup;
        _toggledThisCycle = true;

        if (_firstSetup != null)
            _firstSetup.Value = true;
    }

    private void DrawSetupMenu()
    {
        if (_guiSkin == null || _smallLabel == null) return;

        if (_firstSetup is { Value: false })
        {
            GUILayout.BeginHorizontal(_guiSkin.box);
            GUILayout.Label(PressB, _guiSkin.label);
            GUILayout.EndHorizontal();
        }

        if (!inSetup) return;

        GUILayout.BeginVertical(_guiSkin.box);
        GUILayout.Label(GcHeader, _guiSkin.label);

        if (_activeDeviceCount <= 1)
        {
            GUILayout.Label(GcNoTrackers1, _smallLabel);
            GUILayout.Label(YouWillStillSeeOthers, _smallLabel);
            GUILayout.Label(WhoHaveTheModColonThree, _smallLabel);
        }
        else if (trackerSetUp)
        {
            if (GUILayout.Button(AutoAssignLabel, _guiSkin.button, GUILayout.Height(35)))
                AutoAssignTrackers();

            GUILayout.Space(5);
            DrawTrackerGrid();
            DrawRotationButtons();

            GUILayout.Space(5);
            GUILayout.Label(SmoothingLabel, _smallLabel);
            if (_smoothingAmount != null)
            {
                _smoothingAmount.Value = GUILayout.HorizontalSlider(
                    _smoothingAmount.Value, 5f, 30f,
                    _guiSkin.horizontalSlider,
                    _guiSkin.horizontalSliderThumb
                );
            }

            GUILayout.Label(ElbowSmoothingLabel, _smallLabel);
            if (_elbowSmoothing != null)
            {
                _elbowSmoothing.Value = GUILayout.HorizontalSlider(
                    _elbowSmoothing.Value, 3f, 25f,
                    _guiSkin.horizontalSlider,
                    _guiSkin.horizontalSliderThumb
                );
            }

            GUILayout.Space(10);
        }
        else
        {
            GUILayout.Label(GcNoTrackers2, _smallLabel);
        }

        if (DisableMod != null)
            DisableMod.Value = GUILayout.Toggle(DisableMod.Value, DisableTracking, _guiSkin.toggle);

        if (_activeDeviceCount >= 4 && _elbowTrackingSetting != null)
        {
            if (!_settingToggleCooldown)
            {
                _elbowTrackingSetting.Value =
                    GUILayout.Toggle(_elbowTrackingSetting.Value, ElbowTrackingLabel, _guiSkin.toggle);
            }
            else
            {
                GUILayout.Label(ElbowTrackingCoolDown, _guiSkin.toggle);
            }
        }

        DrawAssignmentSummary();

        GUILayout.Space(10);
        GUILayout.Label(PressB, _guiSkin.label);
        GUILayout.Label(ByGraze, _guiSkin.label);
        GUILayout.EndVertical();
    }

    private void DrawAssignmentSummary()
    {
        GUILayout.Space(5);

        var chest = GetTrackerByRole(TrackerRole.Chest);
        var leftElbow = GetTrackerByRole(TrackerRole.LeftElbow);
        var rightElbow = GetTrackerByRole(TrackerRole.RightElbow);

        var origColor = GUI.color;

        GUI.color = ChestBtnColor;
        GUILayout.Label(
            chest != null
                ? $"CHEST: [{chest.DeviceId}] {chest.Serial.ToUpper()}"
                : "CHEST: NOT ASSIGNED",
            _smallLabel);

        GUI.color = LeftElbowBtnColor;
        GUILayout.Label(
            leftElbow != null
                ? $"LEFT ELBOW: [{leftElbow.DeviceId}] {leftElbow.Serial.ToUpper()}"
                : "LEFT ELBOW: NOT ASSIGNED",
            _smallLabel);

        GUI.color = RightElbowBtnColor;
        GUILayout.Label(
            rightElbow != null
                ? $"RIGHT ELBOW: [{rightElbow.DeviceId}] {rightElbow.Serial.ToUpper()}"
                : "RIGHT ELBOW: NOT ASSIGNED",
            _smallLabel);

        GUI.color = origColor;
    }

    private void DrawTrackerGrid()
    {
        if (_guiSkin == null || _smallLabel == null) return;

        const float boxWidth = 220f;
        const int columns = 3;
        var rows = (_activeDeviceCount + columns - 1) / columns;

        _scrollPos = GUILayout.BeginScrollView(
            _scrollPos, false, true,
            _guiSkin.horizontalScrollbar,
            _guiSkin.verticalScrollbar,
            GUILayout.Height(Screen.height * 0.5f)
        );

        for (var row = 0; row < rows; row++)
        {
            GUILayout.BeginHorizontal();

            for (var col = 0; col < columns; col++)
            {
                var listIndex = row * columns + col;
                GUILayout.BeginVertical(GUILayout.Width(boxWidth));

                if (listIndex < _activeDeviceCount)
                {
                    var deviceId = _activeDeviceIds[listIndex];
                    var tracker = _trackerData[deviceId];
                    var originalColor = GUI.color;

                    var roleLabel = tracker.Role switch
                    {
                        TrackerRole.Chest => " [CHEST]",
                        TrackerRole.LeftElbow => " [L.ELBOW]",
                        TrackerRole.RightElbow => " [R.ELBOW]",
                        _ => ""
                    };

                    if (tracker.Role != TrackerRole.None)
                        GUI.color = AssignedColor;

                    GUILayout.Label($"[({deviceId}) {tracker.Serial.ToUpper()}{roleLabel}]",
                        _smallLabel, GUILayout.Height(20));

                    GUI.color = originalColor;

                    if (tracker.DeviceClass == ETrackedDeviceClass.HMD)
                    {
                        GUI.color = HeadsetBtnColor;
                        if (GUILayout.Button(HeadsetMode, _guiSkin.button, GUILayout.Height(25)))
                            AssignRole(deviceId, TrackerRole.Chest);
                        GUI.color = originalColor;
                    }
                    else
                    {
                        GUI.color = ChestBtnColor;
                        if (GUILayout.Button(SetAsChest, _guiSkin.button, GUILayout.Height(22)))
                            AssignRole(deviceId, TrackerRole.Chest);

                        GUI.color = LeftElbowBtnColor;
                        if (GUILayout.Button(SetAsLeftElbow, _guiSkin.button, GUILayout.Height(22)))
                            AssignRole(deviceId, TrackerRole.LeftElbow);

                        GUI.color = RightElbowBtnColor;
                        if (GUILayout.Button(SetAsRightElbow, _guiSkin.button, GUILayout.Height(22)))
                            AssignRole(deviceId, TrackerRole.RightElbow);

                        GUI.color = originalColor;

                        if (tracker.Role != TrackerRole.None)
                        {
                            if (GUILayout.Button(ClearRoleLabel, _guiSkin.button, GUILayout.Height(20)))
                                ClearRole(deviceId);
                        }
                    }

                    GUILayout.Space(8);
                }
                else
                {
                    GUILayout.FlexibleSpace();
                }

                GUILayout.EndVertical();
            }

            GUILayout.EndHorizontal();
        }

        GUILayout.EndScrollView();
    }

    private void DrawRotationButtons()
    {
        if (chestFollow == null || _trackerOffset == null || _guiSkin == null) return;

        var chestTransform = chestFollow.transform;

        GUILayout.BeginHorizontal();

        if (GUILayout.Button(RTTrackerL, _guiSkin.button))
        {
            chestTransform.Rotate(Vector3.up, 90f, Space.Self);
            _trackerOffset.Value = chestTransform.localRotation;
        }

        if (GUILayout.Button(RTTrackerD, _guiSkin.button))
        {
            chestTransform.Rotate(Vector3.right, 90f, Space.Self);
            _trackerOffset.Value = chestTransform.localRotation;
        }

        if (GUILayout.Button(RTTrackerU, _guiSkin.button))
        {
            chestTransform.Rotate(Vector3.right, -90f, Space.Self);
            _trackerOffset.Value = chestTransform.localRotation;
        }

        if (GUILayout.Button(RTTrackerR, _guiSkin.button))
        {
            chestTransform.Rotate(Vector3.up, -90f, Space.Self);
            _trackerOffset.Value = chestTransform.localRotation;
        }

        GUILayout.EndHorizontal();
    }

    #endregion

    #region Utils

    private static Vector3 GetUnityPosition(HmdMatrix34_t pose)
    {
        return new Vector3(pose.m3, pose.m7, -pose.m11);
    }

    private static Quaternion ConvertSteamVRMatrixToUnityRotation(HmdMatrix34_t pose)
    {
        var matrix = new Matrix4x4
        {
            m00 = pose.m0, m01 = pose.m1, m02 = pose.m2, m03 = pose.m3,
            m10 = pose.m4, m11 = pose.m5, m12 = pose.m6, m13 = pose.m7,
            m20 = pose.m8, m21 = pose.m9, m22 = pose.m10, m23 = pose.m11,
            m30 = 0f, m31 = 0f, m32 = 0f, m33 = 1f
        };

        var forward = matrix.GetColumn(2);
        var up = matrix.GetColumn(1);

        forward.z = -forward.z;
        up.z = -up.z;

        return Quaternion.LookRotation(forward, up);
    }

    private string GetDeviceSerial(uint i)
    {
        if (OpenVR.System == null) return string.Empty;

        var error = ETrackedPropertyError.TrackedProp_Success;
        var capacity = OpenVR.System.GetStringTrackedDeviceProperty(
            i, ETrackedDeviceProperty.Prop_SerialNumber_String,
            null, 0, ref error);

        if (capacity <= 1) return string.Empty;

        _serialBuilder.Clear();
        _serialBuilder.EnsureCapacity((int)capacity);

        OpenVR.System.GetStringTrackedDeviceProperty(
            i, ETrackedDeviceProperty.Prop_SerialNumber_String,
            _serialBuilder, capacity, ref error);

        return _serialBuilder.ToString();
    }

    public Transform GetLeftElbowTarget() => _leftElbowTarget;
    public Transform GetRightElbowTarget() => _rightElbowTarget;
    public bool HasLeftElbow => _hasLeftElbow;
    public bool HasRightElbow => _hasRightElbow;
    public ref ElbowResult GetLeftElbowResult() => ref _leftElbowResult;
    public ref ElbowResult GetRightElbowResult() => ref _rightElbowResult;

    #endregion
}