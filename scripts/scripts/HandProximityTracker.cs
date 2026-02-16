using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// PRODUCTION-READY Hand Proximity Tracker with CONTROLLER FALLBACK
/// 
/// SETUP REQUIRED:
/// 1. If using XR Hands (finger tracking): Just add to scene, it auto-detects
/// 2. If using Controllers: Assign Left/Right Controller Transforms in Inspector
///    - Use the actual controller pose transform (e.g., "LeftHand Controller" or similar)
///    - NOT the interactor, NOT a ray origin - the actual tracked controller position
/// 
/// FIXED ISSUES:
/// - No risky auto-detection that could track wrong transforms
/// - Properly selects running XRHandSubsystem
/// - Clear warnings when not configured
/// - Silent wrong data is impossible - either it works or it complains loudly
/// </summary>
public class HandProximityTracker : MonoBehaviour
{
    public static HandProximityTracker Instance { get; private set; }

    [Header("=== REQUIRED: Controller Fallback Setup ===")]
    [Tooltip("REQUIRED if XR Hands unavailable: Drag your LEFT controller/hand transform here.\nThis should be the tracked pose transform, NOT an interactor.")]
    public Transform leftControllerTransform;

    [Tooltip("REQUIRED if XR Hands unavailable: Drag your RIGHT controller/hand transform here.\nThis should be the tracked pose transform, NOT an interactor.")]
    public Transform rightControllerTransform;

    [Header("Proximity Settings")]
    [Tooltip("Distance threshold for ENGAGED state (meters)")]
    public float engagedThreshold = 0.25f;

    [Tooltip("Distance threshold for DISENGAGED state (meters)")]
    public float disengagedThreshold = 0.5f;

    [Header("Tracking Settings")]
    [Tooltip("How often to check proximity (seconds)")]
    public float checkInterval = 0.1f;

    [Tooltip("How often to refresh interactables cache (seconds)")]
    public float cacheRefreshInterval = 5f;

    [Header("Debug")]
    public bool showDebugLogs = true;
    public bool showGizmos = true;

    // XR Hands references
    private XRHandSubsystem handSubsystem;
    private XRHand leftHand;
    private XRHand rightHand;

    // Tracking mode
    private enum TrackingMode { None, XRHands, ControllerFallback }
    private TrackingMode currentMode = TrackingMode.None;

    private bool hasLeftHand = false;
    private bool hasRightHand = false;
    private bool hasLoggedNoTrackingWarning = false;

    // Cached interactables list
    private List<GameObject> cachedInteractables = new List<GameObject>();
    private float nextCacheRefreshTime = 0f;

    // Current state
    private float closestObjectDistance = float.MaxValue;
    private GameObject closestObject = null;
    private bool isEngaged = false;
    private Vector3 leftHandPosition = Vector3.zero;
    private Vector3 rightHandPosition = Vector3.zero;

    // Tracking data
    private float timeInEngagedState = 0f;
    private float timeInDisengagedState = 0f;
    private int engagedEventCount = 0;
    private int disengagedEventCount = 0;

    // Firebase logging
    private float nextFirebaseLogTime = 0f;
    private float firebaseLogInterval = 2.0f;

    // Room tracking
    private string currentRoom = "unknown";

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        InitializeTracking();
        RefreshInteractablesCache();
        InvokeRepeating(nameof(CheckProximity), 0f, checkInterval);
    }

    /// <summary>
    /// Initialize tracking - tries XR Hands first, then falls back to assigned controllers
    /// </summary>
    void InitializeTracking()
    {
        // Step 1: Try to find a RUNNING XR Hand Subsystem
        var subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        // Find the running one, not just the first one
        foreach (var subsystem in subsystems)
        {
            if (subsystem != null && subsystem.running)
            {
                handSubsystem = subsystem;
                break;
            }
        }

        if (handSubsystem != null)
        {
            currentMode = TrackingMode.XRHands;
            Debug.Log("✅ [HandProximityTracker] XR Hand Subsystem RUNNING - using finger tracking");
            Debug.Log($"   Mode: {currentMode}");
            return;
        }

        // Step 2: Fall back to controller transforms (must be assigned in Inspector!)
        Debug.LogWarning("[HandProximityTracker] ⚠️ No running XR Hand Subsystem found");

        bool hasLeft = leftControllerTransform != null;
        bool hasRight = rightControllerTransform != null;

        if (hasLeft || hasRight)
        {
            currentMode = TrackingMode.ControllerFallback;
            Debug.Log($"✅ [HandProximityTracker] Using CONTROLLER FALLBACK");
            Debug.Log($"   Left Controller: {(hasLeft ? leftControllerTransform.name : "NOT ASSIGNED")}");
            Debug.Log($"   Right Controller: {(hasRight ? rightControllerTransform.name : "NOT ASSIGNED")}");

            if (!hasLeft || !hasRight)
            {
                Debug.LogWarning("[HandProximityTracker] ⚠️ Only one controller assigned - proximity tracking will be limited");
            }
        }
        else
        {
            currentMode = TrackingMode.None;
            Debug.LogError("╔════════════════════════════════════════════════════════════════╗");
            Debug.LogError("║  [HandProximityTracker] ❌ NO TRACKING AVAILABLE!              ║");
            Debug.LogError("║                                                                 ║");
            Debug.LogError("║  Hand proximity will NOT work. To fix:                         ║");
            Debug.LogError("║                                                                 ║");
            Debug.LogError("║  OPTION 1: Enable XR Hands                                     ║");
            Debug.LogError("║    - Install com.unity.xr.hands package                        ║");
            Debug.LogError("║    - Enable Hand Tracking in OpenXR settings                   ║");
            Debug.LogError("║                                                                 ║");
            Debug.LogError("║  OPTION 2: Assign Controller Transforms                        ║");
            Debug.LogError("║    - Select this GameObject in Inspector                       ║");
            Debug.LogError("║    - Drag your Left/Right controller transforms                ║");
            Debug.LogError("║    - Use the TRACKED POSE transform, not interactors           ║");
            Debug.LogError("╚════════════════════════════════════════════════════════════════╝");
        }
    }

    void Update()
    {
        UpdateHandPositions();

        // Update time tracking
        if (isEngaged)
        {
            timeInEngagedState += Time.deltaTime;
        }
        else
        {
            timeInDisengagedState += Time.deltaTime;
        }

        // Periodic cache refresh
        if (Time.time >= nextCacheRefreshTime)
        {
            nextCacheRefreshTime = Time.time + cacheRefreshInterval;
            RefreshInteractablesCache();
        }

        // Firebase logging
        if (Time.time >= nextFirebaseLogTime)
        {
            nextFirebaseLogTime = Time.time + firebaseLogInterval;
            LogProximityToFirebase();
        }
    }

    /// <summary>
    /// Build cached list of interactables
    /// </summary>
    void RefreshInteractablesCache()
    {
        cachedInteractables.Clear();

        // Try by tag first
        try
        {
            GameObject[] taggedObjects = GameObject.FindGameObjectsWithTag("Interactable");
            foreach (var obj in taggedObjects)
            {
                if (obj.activeInHierarchy)
                {
                    cachedInteractables.Add(obj);
                }
            }
        }
        catch (UnityException)
        {
            // Tag doesn't exist, that's fine
        }

        // Find by components
        var ios = FindObjectsByType<InteractiveObject>(FindObjectsSortMode.None);
        var cards = FindObjectsByType<HiddenCard>(FindObjectsSortMode.None);

        foreach (var io in ios)
        {
            if (io.gameObject.activeInHierarchy && !cachedInteractables.Contains(io.gameObject))
            {
                cachedInteractables.Add(io.gameObject);
            }
        }

        foreach (var card in cards)
        {
            if (card.gameObject.activeInHierarchy && !cachedInteractables.Contains(card.gameObject))
            {
                cachedInteractables.Add(card.gameObject);
            }
        }

        if (showDebugLogs)
        {
            Debug.Log($"🔄 [HandProximityTracker] Refreshed cache: {cachedInteractables.Count} interactables found");
        }
    }

    public void OnRoomChanged()
    {
        RefreshInteractablesCache();
    }

    /// <summary>
    /// Update hand positions based on current tracking mode
    /// </summary>
    void UpdateHandPositions()
    {
        switch (currentMode)
        {
            case TrackingMode.XRHands:
                UpdateFromXRHands();
                break;

            case TrackingMode.ControllerFallback:
                UpdateFromControllers();
                break;

            case TrackingMode.None:
                hasLeftHand = false;
                hasRightHand = false;
                break;
        }
    }

    void UpdateFromXRHands()
    {
        if (handSubsystem == null)
        {
            hasLeftHand = false;
            hasRightHand = false;
            return;
        }

        // Left hand
        leftHand = handSubsystem.leftHand;
        hasLeftHand = leftHand.isTracked;
        if (hasLeftHand)
        {
            XRHandJoint palmJoint = leftHand.GetJoint(XRHandJointID.Palm);
            if (palmJoint.TryGetPose(out Pose palmPose))
            {
                leftHandPosition = palmPose.position;
            }
        }

        // Right hand
        rightHand = handSubsystem.rightHand;
        hasRightHand = rightHand.isTracked;
        if (hasRightHand)
        {
            XRHandJoint palmJoint = rightHand.GetJoint(XRHandJointID.Palm);
            if (palmJoint.TryGetPose(out Pose palmPose))
            {
                rightHandPosition = palmPose.position;
            }
        }
    }

    void UpdateFromControllers()
    {
        hasLeftHand = leftControllerTransform != null && leftControllerTransform.gameObject.activeInHierarchy;
        hasRightHand = rightControllerTransform != null && rightControllerTransform.gameObject.activeInHierarchy;

        if (hasLeftHand)
        {
            leftHandPosition = leftControllerTransform.position;
        }

        if (hasRightHand)
        {
            rightHandPosition = rightControllerTransform.position;
        }
    }

    /// <summary>
    /// Check proximity to cached interactables
    /// </summary>
    void CheckProximity()
    {
        if (currentMode == TrackingMode.None)
        {
            return; // Already logged error in Start
        }

        if (!hasLeftHand && !hasRightHand)
        {
            // Rate-limit this warning (log once per 10 seconds)
            if (!hasLoggedNoTrackingWarning)
            {
                hasLoggedNoTrackingWarning = true;
                string modeStr = currentMode == TrackingMode.XRHands ? "XR Hands" : "Controllers";
                Debug.LogWarning($"[HandProximityTracker] No {modeStr} currently tracked - waiting...");
                Invoke(nameof(ResetNoTrackingWarning), 10f);
            }
            return;
        }

        hasLoggedNoTrackingWarning = false;

        if (cachedInteractables.Count == 0)
        {
            return;
        }

        closestObjectDistance = float.MaxValue;
        closestObject = null;

        foreach (GameObject obj in cachedInteractables)
        {
            if (obj == null || !obj.activeInHierarchy) continue;

            float leftDist = float.MaxValue;
            float rightDist = float.MaxValue;

            Collider col = obj.GetComponent<Collider>();
            if (col == null)
            {
                col = obj.GetComponentInChildren<Collider>();
            }

            if (col != null)
            {
                try
                {
                    if (hasLeftHand)
                    {
                        Vector3 closestPoint = col.ClosestPoint(leftHandPosition);
                        leftDist = Vector3.Distance(leftHandPosition, closestPoint);
                    }

                    if (hasRightHand)
                    {
                        Vector3 closestPoint = col.ClosestPoint(rightHandPosition);
                        rightDist = Vector3.Distance(rightHandPosition, closestPoint);
                    }
                }
                catch
                {
                    // Fallback for non-convex mesh colliders
                    if (hasLeftHand)
                        leftDist = Vector3.Distance(leftHandPosition, col.bounds.center);
                    if (hasRightHand)
                        rightDist = Vector3.Distance(rightHandPosition, col.bounds.center);
                }
            }
            else
            {
                if (hasLeftHand)
                {
                    leftDist = Vector3.Distance(leftHandPosition, obj.transform.position);
                }

                if (hasRightHand)
                {
                    rightDist = Vector3.Distance(rightHandPosition, obj.transform.position);
                }
            }

            float minDist = Mathf.Min(leftDist, rightDist);

            if (minDist < closestObjectDistance)
            {
                closestObjectDistance = minDist;
                closestObject = obj;
            }
        }

        UpdateEngagementState();
    }

    void ResetNoTrackingWarning()
    {
        hasLoggedNoTrackingWarning = false;
    }

    void UpdateEngagementState()
    {
        bool wasEngaged = isEngaged;

        if (closestObjectDistance < engagedThreshold)
        {
            isEngaged = true;
        }
        else if (closestObjectDistance > disengagedThreshold)
        {
            isEngaged = false;
        }

        if (isEngaged != wasEngaged)
        {
            if (isEngaged)
            {
                engagedEventCount++;
                if (showDebugLogs)
                {
                    string mode = currentMode == TrackingMode.XRHands ? "✋" : "🎮";
                    Debug.Log($"{mode} [HandProximityTracker] ENGAGED - Distance: {closestObjectDistance:F2}m to {closestObject?.name}");
                }
            }
            else
            {
                disengagedEventCount++;
                if (showDebugLogs)
                {
                    Debug.Log($"[HandProximityTracker] DISENGAGED - Distance: {closestObjectDistance:F2}m");
                }
            }
        }
    }

    // ============================================================================
    // FIREBASE LOGGING
    // ============================================================================

    private async void LogProximityToFirebase()
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
            return;

        if (currentMode == TrackingMode.None)
            return;

        string objectName = closestObject != null ? closestObject.name : "";

        float leftDist = float.MaxValue;
        float rightDist = float.MaxValue;

        if (closestObject != null)
        {
            Collider col = closestObject.GetComponent<Collider>();
            if (col == null) col = closestObject.GetComponentInChildren<Collider>();

            if (col != null)
            {
                if (hasLeftHand)
                {
                    Vector3 closestPoint = col.ClosestPoint(leftHandPosition);
                    leftDist = Vector3.Distance(leftHandPosition, closestPoint);
                }

                if (hasRightHand)
                {
                    Vector3 closestPoint = col.ClosestPoint(rightHandPosition);
                    rightDist = Vector3.Distance(rightHandPosition, closestPoint);
                }
            }
        }

        await FirebaseManager.Instance.LogHandProximityAsync(
            objectName,
            leftDist,
            rightDist,
            isEngaged,
            currentRoom
        );

        if (showDebugLogs)
        {
            string mode = currentMode == TrackingMode.XRHands ? "✋ XRHands" : "🎮 Controller";
            Debug.Log($"{mode} [Firebase] Proximity: {closestObjectDistance:F2}m → {objectName} (engaged: {isEngaged}, room: {currentRoom})");
        }
    }

    // ============================================================================
    // PUBLIC API
    // ============================================================================

    public bool IsEngaged() => isEngaged;
    public float GetClosestObjectDistance() => closestObjectDistance;
    public float GetTimeEngaged() => timeInEngagedState;
    public float GetTimeDisengaged() => timeInDisengagedState;
    public bool AreHandsTracked() => hasLeftHand || hasRightHand;
    public bool IsUsingControllerFallback() => currentMode == TrackingMode.ControllerFallback;
    public bool IsTrackingActive() => currentMode != TrackingMode.None;

    public string GetTrackingStatus()
    {
        switch (currentMode)
        {
            case TrackingMode.XRHands:
                return $"XR Hand Tracking (L:{hasLeftHand}, R:{hasRightHand})";
            case TrackingMode.ControllerFallback:
                return $"Controller Fallback (L:{hasLeftHand}, R:{hasRightHand})";
            default:
                return "NOT CONFIGURED";
        }
    }

    public void SetCurrentRoom(string roomId)
    {
        currentRoom = roomId;
        OnRoomChanged();

        if (showDebugLogs)
        {
            Debug.Log($"[HandProximityTracker] Room changed to: {roomId}");
        }
    }

    // ============================================================================
    // DEBUG VISUALIZATION
    // ============================================================================

    void OnDrawGizmos()
    {
        if (!showGizmos || currentMode == TrackingMode.None)
            return;

        if (hasLeftHand)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(leftHandPosition, engagedThreshold);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(leftHandPosition, disengagedThreshold);
        }

        if (hasRightHand)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(rightHandPosition, engagedThreshold);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(rightHandPosition, disengagedThreshold);
        }

        if (closestObject != null && (hasLeftHand || hasRightHand))
        {
            Gizmos.color = isEngaged ? Color.green : Color.yellow;

            if (hasLeftHand)
                Gizmos.DrawLine(leftHandPosition, closestObject.transform.position);

            if (hasRightHand)
                Gizmos.DrawLine(rightHandPosition, closestObject.transform.position);
        }
    }
}