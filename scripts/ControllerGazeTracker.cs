using UnityEngine;
using System.Collections.Generic;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// CONTROLLER-BASED GAZE TRACKER
/// 
/// Problem: HeadTrackingAnalyzer tracks where the HEAD looks, but users 
/// interact with CONTROLLER laser pointer. So "gaze" detection fails.
/// 
/// Solution: Track what the CONTROLLER is pointing at as the "gaze" target.
/// This is what users actually focus on in laser-pointer VR interaction.
/// 
/// This component provides gaze data to EngagementClassifier based on 
/// controller pointing behavior, not head direction.
/// </summary>
public class ControllerGazeTracker : MonoBehaviour
{
    public static ControllerGazeTracker Instance { get; private set; }

    [Header("Controller References")]
    [Tooltip("Right controller transform (drag Near-Far Interactor or controller here)")]
    public Transform rightControllerTransform;

    [Tooltip("Left controller transform (optional backup)")]
    public Transform leftControllerTransform;

    // XR Ray Interactor reference (auto-found)
    private XRRayInteractor rightRayInteractor;
    private XRRayInteractor leftRayInteractor;

    [Header("Raycast Settings")]
    [Tooltip("How far to raycast for gaze detection")]
    public float raycastDistance = 15f;

    [Tooltip("Layer mask for gaze-able objects")]
    public LayerMask gazeLayerMask;

    [Header("Gaze Thresholds")]
    [Tooltip("Seconds pointing at same object to count as 'focused gaze'")]
    public float focusedGazeThreshold = 2.0f; // Lower than head gaze - controller pointing is intentional

    [Tooltip("Grace period before resetting target (prevents flicker)")]
    public float gazeResetGracePeriod = 0.3f;

    [Header("Debug")]
    public bool showDebugLogs = true;
    public bool showDebugRay = true;

    // Current state
    private string currentTarget = "";
    private string lastTarget = "";
    private float currentDwellTime = 0f;
    private float timeSinceLastHit = 0f;
    private float totalGazeDuration = 0f;

    // Statistics
    private Dictionary<string, float> objectGazeTimes = new Dictionary<string, float>();
    private int gazeEventCount = 0;

    // Firebase logging
    private float lastFirebaseLogTime = 0f;
    private float firebaseLogInterval = 2f;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        // Auto-find controller if not assigned
        if (rightControllerTransform == null)
        {
            rightControllerTransform = FindControllerTransform("Right");
        }

        if (leftControllerTransform == null)
        {
            leftControllerTransform = FindControllerTransform("Left");
        }

        // Setup layer mask
        if (gazeLayerMask.value == 0)
        {
            gazeLayerMask = LayerMask.GetMask("Interactable", "Default");
        }

        if (showDebugLogs)
        {
            Debug.Log("✅ [ControllerGazeTracker] Initialized - Tracking CONTROLLER pointing as gaze");
            Debug.Log($"   Right Controller: {(rightControllerTransform != null ? rightControllerTransform.name : "NOT FOUND")}");
            Debug.Log($"   Focus threshold: {focusedGazeThreshold}s");
            Debug.Log($"   XRRayInteractor: {(rightRayInteractor != null ? "FOUND" : "NOT FOUND")} (uses actual laser hit)");
            if (rightControllerTransform == null && leftControllerTransform == null)
                Debug.LogError("[ControllerGazeTracker] NO CONTROLLERS FOUND - Gaze tracking will not work!");
        }
    }

    Transform FindControllerTransform(string hand)
    {
        // Search paths for XRI 3.0+ Near-Far Interactor
        string[] searchNames = {
            $"{hand} Controller/Near-Far Interactor",
            $"Camera Offset/{hand} Controller/Near-Far Interactor",
            $"XR Origin (XR Rig)/Camera Offset/{hand} Controller/Near-Far Interactor",
            $"{hand} Controller",
            $"{hand}Hand Controller",
            $"Camera Offset/{hand} Controller",
            $"XR Origin (XR Rig)/Camera Offset/{hand} Controller"
        };

        foreach (string name in searchNames)
        {
            GameObject obj = GameObject.Find(name);
            if (obj != null)
            {
                // Try to get XRRayInteractor component
                XRRayInteractor rayInteractor = obj.GetComponent<XRRayInteractor>();
                if (rayInteractor == null)
                    rayInteractor = obj.GetComponentInChildren<XRRayInteractor>();

                if (rayInteractor != null)
                {
                    if (hand == "Right") rightRayInteractor = rayInteractor;
                    else leftRayInteractor = rayInteractor;
                    Debug.Log($"[ControllerGazeTracker] Found {hand} XRRayInteractor: {obj.name}");
                }
                else
                {
                    Debug.Log($"[ControllerGazeTracker] Found {hand} controller: {obj.name} (no XRRayInteractor)");
                }
                return obj.transform;
            }
        }

        // Last resort: search all XRRayInteractors
        var allRayInteractors = FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None);
        foreach (var interactor in allRayInteractors)
        {
            if (interactor.name.ToLower().Contains(hand.ToLower()))
            {
                if (hand == "Right") rightRayInteractor = interactor;
                else leftRayInteractor = interactor;
                Debug.Log($"[ControllerGazeTracker] Found {hand} via XRRayInteractor search: {interactor.name}");
                return interactor.transform;
            }
        }

        Debug.LogWarning($"[ControllerGazeTracker] Could not find {hand} controller automatically");
        return null;
    }

    void Update()
    {
        UpdateControllerGaze();

        // Periodic Firebase logging
        if (Time.time - lastFirebaseLogTime >= firebaseLogInterval)
        {
            lastFirebaseLogTime = Time.time;
            LogGazeToFirebase();
        }
    }

    void UpdateControllerGaze()
    {
        Transform activeController = GetActiveController();
        if (activeController == null) return;

        RaycastHit hit;
        bool hasHit = false;

        // Try XRRayInteractor's actual hit first (more accurate for Near-Far Interactor)
        XRRayInteractor activeRay = rightRayInteractor ?? leftRayInteractor;
        if (activeRay != null && activeRay.TryGetCurrent3DRaycastHit(out hit))
        {
            hasHit = true;
        }
        else
        {
            // Fallback: manual raycast from controller
            Ray ray = new Ray(activeController.position, activeController.forward);
            if (showDebugRay)
            {
                Debug.DrawRay(ray.origin, ray.direction * raycastDistance,
                    !string.IsNullOrEmpty(currentTarget) ? Color.green : Color.yellow);
            }
            hasHit = Physics.Raycast(ray, out hit, raycastDistance, gazeLayerMask, QueryTriggerInteraction.Collide);
        }

        if (hasHit)
        {
            // Hit something - reset miss timer
            timeSinceLastHit = 0f;

            // Identify the object
            string targetName = IdentifyTarget(hit);

            if (!string.IsNullOrEmpty(targetName))
            {
                if (targetName == lastTarget)
                {
                    // Same target - accumulate dwell time
                    currentDwellTime += Time.deltaTime;
                    totalGazeDuration += Time.deltaTime;

                    // Track per-object gaze time
                    if (!objectGazeTimes.ContainsKey(targetName))
                        objectGazeTimes[targetName] = 0f;
                    objectGazeTimes[targetName] += Time.deltaTime;
                }
                else
                {
                    // New target
                    if (showDebugLogs && !string.IsNullOrEmpty(lastTarget))
                    {
                        Debug.Log($"🎯 [ControllerGazeTracker] Target changed: {lastTarget} ({currentDwellTime:F1}s) → {targetName}");
                    }

                    lastTarget = targetName;
                    currentDwellTime = Time.deltaTime;
                    gazeEventCount++;
                }

                currentTarget = targetName;
            }
        }
        else
        {
            // No hit - grace period before clearing
            timeSinceLastHit += Time.deltaTime;

            if (timeSinceLastHit >= gazeResetGracePeriod)
            {
                if (!string.IsNullOrEmpty(currentTarget) && showDebugLogs)
                {
                    Debug.Log($"🎯 [ControllerGazeTracker] Gaze ended: {currentTarget} (dwelled {currentDwellTime:F1}s)");
                }

                currentTarget = "";
                currentDwellTime = 0f;
                lastTarget = "";
            }
        }
    }

    string IdentifyTarget(RaycastHit hit)
    {
        // Try to find InteractiveObject
        InteractiveObject io = hit.collider.GetComponentInParent<InteractiveObject>();
        if (io != null)
        {
            return io.gameObject.name;
        }

        // Try to find HiddenCard
        HiddenCard card = hit.collider.GetComponentInParent<HiddenCard>();
        if (card != null)
        {
            return card.cardID;
        }

        // Generic object
        return hit.collider.gameObject.name;
    }

    Transform GetActiveController()
    {
        // Prefer right controller
        if (rightControllerTransform != null && rightControllerTransform.gameObject.activeInHierarchy)
            return rightControllerTransform;

        if (leftControllerTransform != null && leftControllerTransform.gameObject.activeInHierarchy)
            return leftControllerTransform;

        return null;
    }

    // ============================================================================
    // PUBLIC API - Used by EngagementClassifier
    // ============================================================================

    /// <summary>
    /// Check if user has focused gaze on an object (controller pointing)
    /// This replaces HeadTrackingAnalyzer.GetGazeDuration() for engagement
    /// </summary>
    public bool IsGazeFocused()
    {
        return currentDwellTime >= focusedGazeThreshold;
    }

    /// <summary>
    /// Get current gaze/pointing duration on target
    /// </summary>
    public float GetGazeDuration()
    {
        return currentDwellTime;
    }

    /// <summary>
    /// Get what controller is currently pointing at
    /// </summary>
    public string GetCurrentTarget()
    {
        return currentTarget;
    }

    /// <summary>
    /// Check if controller is pointing at specific object
    /// </summary>
    public bool IsPointingAt(string objectId)
    {
        return currentTarget == objectId;
    }

    /// <summary>
    /// Get total time spent pointing at objects this session
    /// </summary>
    public float GetTotalGazeTime()
    {
        return totalGazeDuration;
    }

    /// <summary>
    /// Get gaze time for specific object
    /// </summary>
    public float GetObjectGazeTime(string objectId)
    {
        return objectGazeTimes.ContainsKey(objectId) ? objectGazeTimes[objectId] : 0f;
    }

    /// <summary>
    /// Get number of distinct gaze events (target changes)
    /// </summary>
    public int GetGazeEventCount()
    {
        return gazeEventCount;
    }

    // ============================================================================
    // FIREBASE LOGGING
    // ============================================================================

    async void LogGazeToFirebase()
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
            return;

        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
            return;

        // Only log if we're pointing at something
        if (string.IsNullOrEmpty(currentTarget))
            return;

        try
        {
            var data = new Dictionary<string, object>
            {
                { "target", currentTarget },
                { "dwellTime", currentDwellTime },
                { "isFocused", IsGazeFocused() },
                { "totalGazeTime", totalGazeDuration },
                { "gazeEventCount", gazeEventCount },
                { "timestamp", Firebase.Firestore.FieldValue.ServerTimestamp },
                { "sessionTime", Time.time }
            };

            string userId = PlayerManager.Instance.userId;
            string docId = $"controller_gaze_{System.DateTime.UtcNow.Ticks}";

            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .Collection("controllerGaze").Document(docId)
                .SetAsync(data);

            if (showDebugLogs)
            {
                Debug.Log($"📊 [ControllerGazeTracker] Logged: {currentTarget} ({currentDwellTime:F1}s, focused: {IsGazeFocused()})");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ControllerGazeTracker] Firebase error: {e.Message}");
        }
    }
}