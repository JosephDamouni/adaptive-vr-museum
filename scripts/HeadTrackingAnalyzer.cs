using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Enhanced Head Tracking Analyzer with REAL gaze tracking via raycasting
/// 
/// ✅ VERSION 2 - VR-OPTIMIZED THRESHOLDS
/// 
/// CHANGES FROM ORIGINAL:
/// 1. Increased stableThreshold from 15 to 25 (VR users naturally move heads more)
/// 2. Increased unstableThreshold from 25 to 40
/// 3. Increased slowMovementThreshold from 0.3 to 0.5 m/s
/// 4. Reduced gazeResetGracePeriod from 0.3 to 0.5s (more forgiving)
/// 
/// PROBLEM: Original thresholds were for desktop mouse users. VR users:
/// - Naturally turn their heads to look around
/// - Have higher rotation velocities even when engaged
/// - Move more physically (room-scale VR)
/// </summary>
public class HeadTrackingAnalyzer : MonoBehaviour
{
    public static HeadTrackingAnalyzer Instance { get; private set; }

    [Header("Tracking Settings")]
    [Tooltip("Main camera/head to track")]
    public Camera playerCamera;

    [Tooltip("How often to sample head data (seconds)")]
    public float sampleInterval = 0.5f;

    [Header("Gaze Tracking")]
    [Tooltip("How far to raycast for gaze detection")]
    public float gazeRaycastDistance = 10f;

    [Tooltip("Layer mask for gaze-able objects")]
    public LayerMask gazeLayerMask;

    [Tooltip("Grace period before resetting gaze target (prevents flickering)")]
    public float gazeResetGracePeriod = 0.5f;  // ✅ INCREASED from 0.3f

    [Header("Thresholds - VR OPTIMIZED")]
    [Tooltip("Head rotation variance threshold for 'stable' head - INCREASED for VR")]
    public float stableThreshold = 25f;  // ✅ INCREASED from 15f

    [Tooltip("Head rotation variance threshold for 'unstable' head - INCREASED for VR")]
    public float unstableThreshold = 40f;  // ✅ INCREASED from 25f

    [Tooltip("Movement speed threshold (m/s) for 'slow' movement - INCREASED for VR")]
    public float slowMovementThreshold = 0.5f;  // ✅ INCREASED from 0.3f

    [Tooltip("Movement speed threshold (m/s) for 'fast' movement")]
    public float fastMovementThreshold = 1.2f;  // ✅ INCREASED from 1.0f

    [Header("Debug")]
    public bool showDebugLogs = true;
    public bool showDetailedLogs = false;

    // Tracking data
    private List<float> rotationSamples = new List<float>();
    private Vector3 lastHeadPosition;
    private Quaternion lastHeadRotation;
    private float currentRotationVariance = 0f;
    private float currentMovementSpeed = 0f;
    private float lastSampleTime = 0f;

    // ✅ Real gaze tracking with grace period
    private string currentlyLookingAt = "";
    private float currentDwellTime = 0f;
    private string lastLookedAtObject = "";
    private float timeSinceLastHit = 0f;
    private float totalGazeDuration = 0f;

    // Curiosity detection
    private float currentHeadTilt = 0f;
    private float tiltDuration = 0f;
    private bool isCurious = false;

    [Header("Curiosity Detection")]
    public float minCuriosityTilt = 15f;
    public float maxCuriosityTilt = 45f;
    public float curiosityDuration = 1.5f;

    // State
    private bool isHeadStable = false;
    private bool isEnabled = true;

    // Room tracking
    private string currentRoom = "unknown";

    // Firebase logging interval
    private float nextFirebaseLogTime = 0f;
    private float firebaseLogInterval = 2.0f;

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
        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }

        if (playerCamera == null)
        {
            Debug.LogError("[HeadTrackingAnalyzer] No camera found!");
            enabled = false;
            return;
        }

        lastHeadPosition = playerCamera.transform.position;
        lastHeadRotation = playerCamera.transform.rotation;

        if (gazeLayerMask.value == 0)
        {
            gazeLayerMask = LayerMask.GetMask("Interactable", "Default");
        }

        if (showDebugLogs)
        {
            Debug.Log("✅ [HeadTrackingAnalyzer] VR-OPTIMIZED system initialized");
            Debug.Log($"   Stable threshold: {stableThreshold}°/s (was 15)");
            Debug.Log($"   Slow movement: {slowMovementThreshold} m/s (was 0.3)");
        }
    }

    void Update()
    {
        if (!isEnabled) return;

        // ✅ Always track, even in static condition (needed for analysis!)
        bool isAdaptiveCondition = ExperimentConditionManager.Instance == null ||
                                    ExperimentConditionManager.Instance.IsAdaptiveCondition;

        // Sample head data at intervals
        if (Time.time - lastSampleTime >= sampleInterval)
        {
            SampleHeadData();
            lastSampleTime = Time.time;
        }

        // Track gaze target every frame
        UpdateGazeTarget();

        // Track curiosity continuously
        UpdateCuriosityDetection();

        // Log to Firebase at reduced intervals
        if (Time.time >= nextFirebaseLogTime)
        {
            nextFirebaseLogTime = Time.time + firebaseLogInterval;
            LogHeadAndMovementToFirebase();
        }
    }

    void SampleHeadData()
    {
        Vector3 currentPosition = playerCamera.transform.position;
        Quaternion currentRotation = playerCamera.transform.rotation;

        // Calculate rotation change
        float rotationChange = Quaternion.Angle(lastHeadRotation, currentRotation);
        rotationSamples.Add(rotationChange / sampleInterval);

        // Keep only recent samples (10 seconds worth)
        int maxSamples = Mathf.CeilToInt(10f / sampleInterval);
        if (rotationSamples.Count > maxSamples)
        {
            rotationSamples.RemoveAt(0);
        }

        // Calculate rotation variance
        if (rotationSamples.Count > 0)
        {
            float mean = rotationSamples.Average();
            float variance = rotationSamples.Select(x => Mathf.Pow(x - mean, 2)).Average();
            currentRotationVariance = Mathf.Sqrt(variance);
        }

        // Calculate movement speed
        float distance = Vector3.Distance(lastHeadPosition, currentPosition);
        currentMovementSpeed = distance / sampleInterval;

        // ✅ FIX: Use the VR-appropriate threshold
        isHeadStable = currentRotationVariance < stableThreshold;

        // Update last values
        lastHeadPosition = currentPosition;
        lastHeadRotation = currentRotation;

        if (showDetailedLogs)
        {
            Debug.Log($"[HeadTrackingAnalyzer] Head {(isHeadStable ? "STABLE" : "UNSTABLE")} - Variance: {currentRotationVariance:F2}°/s (threshold: {stableThreshold}), Speed: {currentMovementSpeed:F2}m/s");
        }
    }

    /// <summary>
    /// Track gaze with grace period to prevent flickering
    /// </summary>
    void UpdateGazeTarget()
    {
        if (playerCamera == null) return;

        Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, gazeRaycastDistance, gazeLayerMask, QueryTriggerInteraction.Collide))
        {
            // Hit something - reset miss timer
            timeSinceLastHit = 0f;

            // Try to identify the object
            string targetName = "";

            InteractiveObject io = hit.collider.GetComponentInParent<InteractiveObject>();
            if (io != null)
            {
                targetName = io.gameObject.name;
            }
            else
            {
                HiddenCard card = hit.collider.GetComponentInParent<HiddenCard>();
                if (card != null)
                {
                    targetName = card.cardID;
                }
            }

            if (!string.IsNullOrEmpty(targetName))
            {
                if (targetName == lastLookedAtObject)
                {
                    // Same target - increase dwell time
                    currentDwellTime += Time.deltaTime;
                    totalGazeDuration += Time.deltaTime;
                }
                else
                {
                    // New target - log switch and reset dwell
                    if (showDetailedLogs && !string.IsNullOrEmpty(lastLookedAtObject))
                    {
                        Debug.Log($"👁️ [HeadTrackingAnalyzer] Gaze switched: {lastLookedAtObject} ({currentDwellTime:F1}s) → {targetName}");
                    }

                    lastLookedAtObject = targetName;
                    currentDwellTime = Time.deltaTime;
                }

                currentlyLookingAt = targetName;
            }
        }
        else
        {
            // Grace period before resetting (prevents flicker)
            timeSinceLastHit += Time.deltaTime;

            if (timeSinceLastHit >= gazeResetGracePeriod)
            {
                // Sustained miss - reset gaze
                if (!string.IsNullOrEmpty(currentlyLookingAt))
                {
                    if (showDetailedLogs)
                    {
                        Debug.Log($"👁️ [HeadTrackingAnalyzer] Gaze ended: {currentlyLookingAt} (dwelled {currentDwellTime:F1}s)");
                    }

                    currentlyLookingAt = "";
                    currentDwellTime = 0f;
                    lastLookedAtObject = "";
                }
            }
        }
    }

    /// <summary>
    /// Update curiosity detection based on head tilt
    /// </summary>
    void UpdateCuriosityDetection()
    {
        Vector3 headUp = playerCamera.transform.up;
        Vector3 worldUp = Vector3.up;
        currentHeadTilt = Vector3.Angle(headUp, worldUp);

        bool isHeadTilted = currentHeadTilt >= minCuriosityTilt && currentHeadTilt <= maxCuriosityTilt;

        if (isHeadTilted)
        {
            tiltDuration += Time.deltaTime;

            if (tiltDuration >= curiosityDuration && !isCurious)
            {
                isCurious = true;
                if (showDebugLogs)
                {
                    Debug.Log($"🤔 [HeadTrackingAnalyzer] CURIOSITY DETECTED - Head tilted {currentHeadTilt:F1}° for {tiltDuration:F1}s");
                }
            }
        }
        else
        {
            if (isCurious && showDebugLogs)
            {
                Debug.Log($"[HeadTrackingAnalyzer] Curiosity ended");
            }
            tiltDuration = 0f;
            isCurious = false;
        }
    }

    // ============================================================================
    // PUBLIC API
    // ============================================================================

    public bool IsHeadStable() => isHeadStable;
    public float GetRotationVariance() => currentRotationVariance;
    public float GetMovementSpeed() => currentMovementSpeed;
    public float GetGazeDuration() => currentDwellTime;
    public string GetCurrentGazeTarget() => currentlyLookingAt;
    public bool IsGazingAt(string objectId) => currentlyLookingAt == objectId;
    public bool IsMovingSlowly() => currentMovementSpeed < slowMovementThreshold;
    public bool IsMovingQuickly() => currentMovementSpeed > fastMovementThreshold;

    // Curiosity methods
    public bool IsCurious() => isCurious;
    public float GetHeadTilt() => currentHeadTilt;
    public float GetTiltDuration() => tiltDuration;

    public void SetEnabled(bool enabled)
    {
        isEnabled = enabled;
        if (showDebugLogs)
        {
            Debug.Log($"[HeadTrackingAnalyzer] {(enabled ? "ENABLED" : "DISABLED")}");
        }
    }

    public void SetCurrentRoom(string roomId)
    {
        currentRoom = roomId;
        if (showDebugLogs)
        {
            Debug.Log($"[HeadTrackingAnalyzer] Room changed to: {roomId}");
        }
    }

    // ============================================================================
    // FIREBASE LOGGING
    // ============================================================================

    private async void LogHeadAndMovementToFirebase()
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
            return;

        // Log head tracking with ACTUAL object being looked at
        await FirebaseManager.Instance.LogHeadTrackingAsync(
            playerCamera.transform.position,
            playerCamera.transform.rotation,
            currentRotationVariance,
            currentlyLookingAt
        );

        // Log movement
        await FirebaseManager.Instance.LogMovementAsync(
            playerCamera.transform.position,
            currentMovementSpeed,
            currentRoom
        );

        // Log gaze event if dwelling on something for >3s
        if (!string.IsNullOrEmpty(currentlyLookingAt) && currentDwellTime > 3f)
        {
            await FirebaseManager.Instance.LogGazeEventAsync(
                currentlyLookingAt,
                currentDwellTime,
                false
            );
        }

        if (showDebugLogs)
        {
            string gazeInfo = !string.IsNullOrEmpty(currentlyLookingAt)
                ? $"👁️ {currentlyLookingAt} ({currentDwellTime:F1}s)"
                : "nothing";
            string stableInfo = isHeadStable ? "✅STABLE" : "❌UNSTABLE";
            Debug.Log($"📊 [Firebase] Speed: {currentMovementSpeed:F2}m/s | Head: {currentRotationVariance:F1}°/s ({stableInfo}) | Gaze: {gazeInfo}");
        }
    }

    public HeadTrackingSummary GetTrackingSummary()
    {
        return new HeadTrackingSummary
        {
            rotationVariance = currentRotationVariance,
            movementSpeed = currentMovementSpeed,
            gazeDuration = currentDwellTime,
            gazeTarget = currentlyLookingAt,
            isStable = isHeadStable
        };
    }
}

public struct HeadTrackingSummary
{
    public float rotationVariance;
    public float movementSpeed;
    public float gazeDuration;
    public string gazeTarget;
    public bool isStable;
}