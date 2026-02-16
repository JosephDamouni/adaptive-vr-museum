using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// ENGAGEMENT CLASSIFIER - WITH UI OCCLUSION FIX
/// 
/// KEY FIX: When user is reading content for 2+ seconds, the UI text canvas 
/// occludes the object collider, causing gazeFocused=false even though user
/// IS looking at content. This fix treats sustained reading as a valid proxy
/// for gaze focus, boosting gazeScore to allow the score to reach HighlyEngaged.
/// 
/// KEY FEATURES:
/// 1. Weighted scoring from physical signals (gaze, head, movement, reading)
/// 2. Reading behavior integration for cognitive engagement
/// 3. UI OCCLUSION COMPENSATION for sustained reading
/// 4. Behavioral gating rules to prevent false positives
/// 5. Reading grace period (2.5s) allows repositioning movements during reading
/// 6. Optimized for laser-pointer VR (hand proximity weight reduced)
/// 
/// ENGAGEMENT LEVELS (for IUI paper):
/// - HighlyEngaged: Deep cognitive engagement (actively reading content for 2+ seconds)
/// - Engaged: Focused attention (slow, stable, looking) OR repositioning while reading
/// - Neutral: Transitioning / casual browsing
/// - Disengaged: Low attention, moving around without reading
/// - HighlyDisengaged: Not engaging at all
/// </summary>
public class EngagementClassifier : MonoBehaviour
{
    public static EngagementClassifier Instance { get; private set; }

    // ============================================================================
    // CONFIGURATION - FIXED VALUES
    // ============================================================================

    [Header("Update Settings")]
    [Tooltip("Update classification every X seconds")]
    public float updateInterval = 2f;

    [Header("Signal Weights (must sum to 1.0)")]
    [Tooltip("Weight of physical signals (hand, head, gaze, movement)")]
    [Range(0.3f, 0.8f)]
    public float physicalSignalWeight = 0.75f;

    [Tooltip("Weight of reading behavior signal")]
    [Range(0.2f, 0.7f)]
    public float readingSignalWeight = 0.25f;

    [Header("Level Thresholds (0-1 scale) - FIXED FOR VR")]
    [Tooltip("Score above this = Highly Engaged")]
    [Range(0.6f, 0.95f)]
    public float highlyEngagedThreshold = 0.68f;

    [Tooltip("Score above this = Engaged")]
    [Range(0.4f, 0.8f)]
    public float engagedThreshold = 0.48f;

    [Tooltip("Score above this = Neutral")]
    [Range(0.25f, 0.5f)]
    public float neutralThreshold = 0.30f;

    [Tooltip("Score above this = Disengaged")]
    [Range(0.1f, 0.35f)]
    public float disengagedThreshold = 0.22f;

    [Tooltip("Score BELOW this = Highly Disengaged")]
    [Range(0.05f, 0.2f)]
    public float highlyDisengagedThreshold = 0.18f;

    [Header("Smoothing Settings")]
    [Tooltip("How quickly engagement level can change (0-1). Lower = smoother")]
    [Range(0.1f, 0.5f)]
    public float levelTransitionSpeed = 0.35f;

    [Tooltip("Minimum score change required to trigger level transition")]
    [Range(0.02f, 0.1f)]
    public float levelChangeHysteresis = 0.01f;

    [Header("Individual Criterion Thresholds - FIXED FOR VR")]
    [Tooltip("Controller gaze duration (seconds) for focused criterion")]
    public float gazeFocusedThreshold = 1.0f;

    [Tooltip("Head rotation variance (°/s) for stable head criterion")]
    public float headStableThreshold = 30f;

    [Tooltip("Movement speed (m/s) for slow movement criterion")]
    public float slowMovementThreshold = 1.2f;

    [Header("Criterion Weights - REBALANCED")]
    [Tooltip("Weight for controller gaze")]
    [Range(0f, 1f)]
    public float gazeWeight = 0.30f;

    [Tooltip("Weight for head stability")]
    [Range(0f, 1f)]
    public float headStabilityWeight = 0.35f;

    [Tooltip("Weight for slow movement")]
    [Range(0f, 1f)]
    public float movementWeight = 0.30f;

    [Tooltip("Weight for hand proximity")]
    [Range(0f, 1f)]
    public float handProximityWeight = 0.05f;

    [Header("Behavioral Gating Settings")]
    [Tooltip("Seconds of sustained reading required for HighlyEngaged")]
    public float requiredReadingDuration = 2f;

    [Tooltip("Movement speed (m/s) above which user is considered 'very fast' - caps at Disengaged")]
    public float veryFastMovementThreshold = 2f;

    [Tooltip("Enable very fast movement gating (optional stricter rule)")]
    public bool enableVeryFastGating = true;

    [Tooltip("Grace period (seconds) after reading stops before movement gating kicks in")]
    public float readingGracePeriod = 3f;

    [Header("Debug")]
    public bool showDebugLogs = true;
    public bool showDetailedLogs = true;

    // ============================================================================
    // INTERNAL STATE
    // ============================================================================

    // Current state
    private EngagementLevel currentEngagementLevel = EngagementLevel.Neutral;
    private float currentEngagementScore = 0.5f;
    private float rawEngagementScore = 0.5f;
    private float lastUpdateTime = 0f;

    // Physical criteria (4 criteria)
    private bool handProximityEngaged = false;
    private bool headStable = false;
    private bool gazeFocused = false;
    private bool movementSlow = false;
    private float physicalScore = 0.5f;

    // Reading criterion
    private bool readingEngaged = false;
    private float readingScore = 0.5f;

    // Statistics
    private int physicalCriteriaMetCount = 0;
    private float readingConfidence = 0f;

    // Detailed scores
    private float gazeScore = 0f;
    private float headScore = 0f;
    private float movementScore = 0f;
    private float handScore = 0f;

    // Firebase logging
    private float lastFirebaseLogTime = 0f;
    private float firebaseLogInterval = 5f;

    // Behavioral gating state
    private float readingEngagedDuration = 0f;
    private float currentMovementSpeed = 0f;
    private float lastReadingTime = -10f;

    // UI Occlusion fix tracking
    private bool uiOcclusionCompensationApplied = false;

    // ============================================================================
    // UNITY LIFECYCLE
    // ============================================================================

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
        // Validate weights
        float totalWeight = gazeWeight + headStabilityWeight + movementWeight + handProximityWeight;
        if (Mathf.Abs(totalWeight - 1f) > 0.01f)
        {
            Debug.LogWarning($"[EngagementClassifier] Physical weights don't sum to 1.0 ({totalWeight}). Normalizing...");
            float factor = 1f / totalWeight;
            gazeWeight *= factor;
            headStabilityWeight *= factor;
            movementWeight *= factor;
            handProximityWeight *= factor;
        }

        if (showDebugLogs)
        {
            Debug.Log("✅ [EngagementClassifier] WITH UI OCCLUSION FIX initialized");
            StartCoroutine(CheckGazeTrackerStatus());
            Debug.Log($"   Weights: Gaze={gazeWeight:P0}, Head={headStabilityWeight:P0}, Move={movementWeight:P0}, Hand={handProximityWeight:P0}");
            Debug.Log($"   Thresholds: HE>{highlyEngagedThreshold:F2}, E>{engagedThreshold:F2}, N>{neutralThreshold:F2}, D>{disengagedThreshold:F2}, HD>{highlyDisengagedThreshold:F2}");
        }
    }

    System.Collections.IEnumerator CheckGazeTrackerStatus()
    {
        yield return new WaitForSeconds(2f);
        bool hasControllerGaze = ControllerGazeTracker.Instance != null;
        bool hasHeadGaze = HeadTrackingAnalyzer.Instance != null;
        Debug.Log($"[EngagementClassifier] GAZE STATUS: Controller={hasControllerGaze}, Head={hasHeadGaze}");
        if (!hasControllerGaze && !hasHeadGaze)
            Debug.LogError("[EngagementClassifier] NO GAZE TRACKER FOUND!");
    }

    void Update()
    {
        if (Time.time - lastUpdateTime < updateInterval)
            return;

        lastUpdateTime = Time.time;

        // STAGE 1: Evaluate all criteria
        EvaluatePhysicalCriteria();
        EvaluateReadingCriterion();

        // =========================================================================
        // NEW STAGE 1.5: UI OCCLUSION COMPENSATION
        // When user is reading content, the UI text canvas often occludes the
        // physical object collider. The raycast hits UI, so gazeFocused=false.
        // But if user has been reading for 2+ seconds, they ARE clearly looking!
        // =========================================================================
        ApplyUIocclusionCompensation();

        // STAGE 2: Calculate weighted score (now uses compensated gazeScore)
        CalculateWeightedScore();

        // STAGE 3: Apply smoothing
        ApplySmoothing();

        // STAGE 4: Determine engagement level
        EngagementLevel newLevel = DetermineEngagementLevelDirect();

        // STAGE 5: Handle level transition
        if (newLevel != currentEngagementLevel)
        {
            HandleLevelTransition(newLevel);
        }

        // Periodic Firebase logging
        if (Time.time - lastFirebaseLogTime >= firebaseLogInterval)
        {
            lastFirebaseLogTime = Time.time;
            LogCurrentStateToFirebase();
        }

        if (showDetailedLogs)
        {
            LogDetailedState();
        }
    }

    // ============================================================================
    // STAGE 1: EVALUATE CRITERIA
    // ============================================================================

    void EvaluatePhysicalCriteria()
    {
        physicalCriteriaMetCount = 0;
        uiOcclusionCompensationApplied = false;  // Reset each frame

        // =====================================================================
        // Criterion 1: CONTROLLER GAZE
        // =====================================================================
        if (ControllerGazeTracker.Instance != null)
        {
            float gazeDuration = ControllerGazeTracker.Instance.GetGazeDuration();
            gazeFocused = gazeDuration >= gazeFocusedThreshold;
            gazeScore = Mathf.Clamp01(gazeDuration / gazeFocusedThreshold);
            if (gazeFocused) physicalCriteriaMetCount++;
        }
        else if (HeadTrackingAnalyzer.Instance != null)
        {
            float gazeDuration = HeadTrackingAnalyzer.Instance.GetGazeDuration();
            gazeFocused = gazeDuration >= gazeFocusedThreshold;
            gazeScore = Mathf.Clamp01(gazeDuration / gazeFocusedThreshold);
            if (gazeFocused) physicalCriteriaMetCount++;
        }
        else
        {
            gazeFocused = false;
            gazeScore = 0f;
        }

        // =====================================================================
        // Criterion 2: Head Stability
        // =====================================================================
        if (HeadTrackingAnalyzer.Instance != null)
        {
            float rotationVariance = HeadTrackingAnalyzer.Instance.GetRotationVariance();
            headStable = rotationVariance < headStableThreshold;
            headScore = Mathf.Clamp01(1f - (rotationVariance / (headStableThreshold * 1.5f)));
            if (headStable) physicalCriteriaMetCount++;
        }
        else
        {
            headStable = false;
            headScore = 0f;
        }

        // =====================================================================
        // Criterion 3: Movement Speed
        // =====================================================================
        if (HeadTrackingAnalyzer.Instance != null)
        {
            currentMovementSpeed = HeadTrackingAnalyzer.Instance.GetMovementSpeed();
            movementSlow = currentMovementSpeed < slowMovementThreshold;
            movementScore = Mathf.Clamp01(1f - (currentMovementSpeed / (slowMovementThreshold * 1.5f)));
            if (movementSlow) physicalCriteriaMetCount++;
        }
        else
        {
            currentMovementSpeed = 0f;
            movementSlow = false;
            movementScore = 0f;
        }

        // =====================================================================
        // Criterion 4: Hand Proximity
        // =====================================================================
        if (HandProximityTracker.Instance != null)
        {
            handProximityEngaged = HandProximityTracker.Instance.IsEngaged();
            float distance = HandProximityTracker.Instance.GetClosestObjectDistance();
            handScore = Mathf.Clamp01(1f - (distance / 1f));
            if (handProximityEngaged) physicalCriteriaMetCount++;
        }
        else
        {
            handProximityEngaged = false;
            handScore = 0f;
        }
    }

    void EvaluateReadingCriterion()
    {
        if (ReadingBehaviorTracker.Instance != null)
        {
            readingScore = ReadingBehaviorTracker.Instance.GetReadingEngagementScore();
            readingEngaged = ReadingBehaviorTracker.Instance.IsReadingEngaged();
            readingConfidence = ReadingBehaviorTracker.Instance.GetConfidence();

            // Boost reading score when actually reading
            if (readingEngaged && readingScore < 0.6f)
            {
                readingScore = Mathf.Max(readingScore, 0.6f);
            }

            // Track sustained reading duration
            if (readingEngaged)
            {
                readingEngagedDuration += updateInterval;
                lastReadingTime = Time.time;
            }
            else
            {
                readingEngagedDuration = 0f;
            }
        }
        else
        {
            readingScore = 0f;
            readingEngaged = false;
            readingConfidence = 0f;
            readingEngagedDuration = 0f;
        }
    }

    // ============================================================================
    // STAGE 1.5: UI OCCLUSION COMPENSATION (NEW!)
    // ============================================================================

    /// <summary>
    /// FIX FOR UI OCCLUSION BUG:
    /// When user is reading content, the UI text canvas occludes the physical object
    /// collider. The controller raycast hits the UI layer, so gazeFocused=false and
    /// gazeScore stays near zero. This prevents users from ever reaching HighlyEngaged
    /// even when reading for 40+ seconds!
    /// 
    /// Solution: Treat sustained reading (2+ seconds) as a valid proxy for gaze focus.
    /// If user is actively reading content, they ARE looking at it - just at the UI
    /// representation rather than the underlying object collider.
    /// 
    /// ADDITIONAL FIX: Also check for reading evidence from ReadingBehaviorTracker
    /// directly, since the readingEngaged flag may not be true at exact sample time.
    /// </summary>
    void ApplyUIocclusionCompensation()
    {
        // Primary check: sustained reading as tracked by this classifier
        bool sustainedReading = readingEngaged && (readingEngagedDuration >= requiredReadingDuration);

        // Secondary check: Get reading evidence directly from tracker
        // The tracker may have detected reading that we haven't captured yet
        bool hasReadingEvidence = false;
        float currentViewDuration = 0f;

        if (ReadingBehaviorTracker.Instance != null)
        {
            // Check if tracker considers user currently engaged in reading
            hasReadingEvidence = ReadingBehaviorTracker.Instance.IsReadingEngaged();

            // Also check confidence - high confidence reading detection is good signal
            float confidence = ReadingBehaviorTracker.Instance.GetConfidence();
            float engagementScore = ReadingBehaviorTracker.Instance.GetReadingEngagementScore();

            // If tracker shows high reading engagement, treat as reading
            if (!hasReadingEvidence && engagementScore > 0.5f && confidence > 0.8f)
            {
                hasReadingEvidence = true;
            }
        }

        // Apply compensation if EITHER sustained reading OR strong reading evidence
        bool shouldCompensate = sustainedReading || (hasReadingEvidence && readingConfidence > 0.7f);

        if (!shouldCompensate)
        {
            return;  // No compensation needed
        }

        // =========================================================================
        // COMPENSATION: User has reading evidence
        // They ARE looking at content - just hitting UI layer instead of object
        // =========================================================================

        float originalGazeScore = gazeScore;
        bool originalGazeFocused = gazeFocused;

        // If gaze isn't already focused, treat reading as "technically looking"
        if (!gazeFocused)
        {
            gazeFocused = true;
            physicalCriteriaMetCount++;
            uiOcclusionCompensationApplied = true;
        }

        // Boost gazeScore to allow total score to reach HighlyEngaged threshold
        // Reading = user is clearly engaged with visual content
        if (gazeScore < 1.0f)
        {
            gazeScore = 1.0f;
            uiOcclusionCompensationApplied = true;
        }

        if (uiOcclusionCompensationApplied && showDetailedLogs)
        {
            Debug.Log($"[EngagementClassifier] 👁️ UI OCCLUSION FIX: Reading evidence detected → gazeFocused: {originalGazeFocused}→{gazeFocused}, gazeScore: {originalGazeScore:F2}→{gazeScore:F2}");
        }
    }

    // ============================================================================
    // STAGE 2: CALCULATE WEIGHTED SCORE
    // ============================================================================

    void CalculateWeightedScore()
    {
        float effectiveReadingWeight = readingSignalWeight * readingConfidence;
        float effectivePhysicalWeight = physicalSignalWeight + (readingSignalWeight - effectiveReadingWeight);

        // Now uses compensated gazeScore if UI occlusion fix was applied
        physicalScore = (gazeScore * gazeWeight) +
                       (headScore * headStabilityWeight) +
                       (movementScore * movementWeight) +
                       (handScore * handProximityWeight);

        physicalScore = Mathf.Clamp01(physicalScore);

        rawEngagementScore = (physicalScore * effectivePhysicalWeight) +
                            (readingScore * effectiveReadingWeight);

        rawEngagementScore = Mathf.Clamp01(rawEngagementScore);
    }

    // ============================================================================
    // STAGE 3: APPLY SMOOTHING
    // ============================================================================

    void ApplySmoothing()
    {
        currentEngagementScore = Mathf.Lerp(currentEngagementScore, rawEngagementScore, levelTransitionSpeed);
        currentEngagementScore = Mathf.Clamp01(currentEngagementScore);
    }

    // ============================================================================
    // STAGE 4: DETERMINE ENGAGEMENT LEVEL - WITH BEHAVIORAL GATING
    // ============================================================================

    EngagementLevel DetermineEngagementLevelDirect()
    {
        float score = currentEngagementScore;
        EngagementLevel maxAllowedLevel = EngagementLevel.HighlyEngaged;

        // Reading grace period
        bool recentlyReading = readingEngaged || (Time.time - lastReadingTime < readingGracePeriod);

        // GATING RULE 1: Very fast movement = cap at Disengaged
        bool movingVeryFast = currentMovementSpeed > veryFastMovementThreshold;

        if (enableVeryFastGating && movingVeryFast && !recentlyReading)
        {
            maxAllowedLevel = EngagementLevel.Disengaged;

            if (showDetailedLogs)
            {
                Debug.Log($"[EngagementClassifier] GATE: Very fast ({currentMovementSpeed:F2}m/s) → capped at Disengaged");
            }
        }

        // GATING RULE 2: Fast movement + not reading = cap at Neutral
        bool movingFastWithoutReading = !movementSlow && !recentlyReading;

        if (movingFastWithoutReading && maxAllowedLevel > EngagementLevel.Neutral)
        {
            maxAllowedLevel = EngagementLevel.Neutral;

            if (showDetailedLogs)
            {
                Debug.Log("[EngagementClassifier] GATE: Fast + not reading → capped at Neutral");
            }
        }

        // GATING RULE 3: HighlyEngaged requires SUSTAINED READING OR very high score
        // Original: Required sustained reading, but this was too strict when reading
        // detection doesn't align with classifier sampling
        // 
        // NEW APPROACH: Allow HE if EITHER:
        // A) Sustained reading (reading now for 2+ seconds), OR
        // B) Very high raw score (> 0.75) indicating strong engagement signals, OR
        // C) Recently reading (was reading within grace period) AND good score (> 0.6)
        bool sustainedReading = readingEngaged && (readingEngagedDuration >= requiredReadingDuration);
        bool veryHighScore = rawEngagementScore >= 0.75f;
        bool recentReadingWithGoodScore = recentlyReading && rawEngagementScore >= 0.6f;

        bool allowHighlyEngaged = sustainedReading || veryHighScore || recentReadingWithGoodScore;

        if (!allowHighlyEngaged && maxAllowedLevel > EngagementLevel.Engaged)
        {
            maxAllowedLevel = EngagementLevel.Engaged;

            if (showDetailedLogs)
            {
                Debug.Log($"[EngagementClassifier] GATE: HE blocked (sustained={sustainedReading}, highScore={veryHighScore}, recentRead={recentReadingWithGoodScore}) → capped at Engaged");
            }
        }

        // SCORE-BASED CLASSIFICATION
        EngagementLevel scoreBasedLevel;

        if (score >= highlyEngagedThreshold)
        {
            scoreBasedLevel = EngagementLevel.HighlyEngaged;
        }
        else if (score >= engagedThreshold)
        {
            scoreBasedLevel = EngagementLevel.Engaged;
        }
        else if (score >= neutralThreshold)
        {
            scoreBasedLevel = EngagementLevel.Neutral;
        }
        else if (score >= highlyDisengagedThreshold)
        {
            scoreBasedLevel = EngagementLevel.Disengaged;
        }
        else
        {
            scoreBasedLevel = EngagementLevel.HighlyDisengaged;
        }

        // APPLY GATING
        EngagementLevel finalLevel = (EngagementLevel)Mathf.Min((int)scoreBasedLevel, (int)maxAllowedLevel);

        if (showDebugLogs && finalLevel != scoreBasedLevel)
        {
            Debug.Log($"[EngagementClassifier] GATED: {scoreBasedLevel} → {finalLevel}");
        }

        return finalLevel;
    }

    // ============================================================================
    // STAGE 5: HANDLE LEVEL TRANSITION
    // ============================================================================

    async void HandleLevelTransition(EngagementLevel newLevel)
    {
        EngagementLevel previousLevel = currentEngagementLevel;
        currentEngagementLevel = newLevel;

        string direction = (int)newLevel > (int)previousLevel ? "📈 UP" : "📉 DOWN";

        bool recentlyReading = readingEngaged || (Time.time - lastReadingTime < readingGracePeriod);
        bool sustainedReading = readingEngaged && (readingEngagedDuration >= requiredReadingDuration);

        var indicators = new Dictionary<string, object>
        {
            { "gazeFocused", gazeFocused },
            { "gazeScore", gazeScore },
            { "uiOcclusionFix", uiOcclusionCompensationApplied },
            { "headStable", headStable },
            { "headScore", headScore },
            { "movementSlow", movementSlow },
            { "movementScore", movementScore },
            { "movementSpeed", currentMovementSpeed },
            { "physicalCriteriaMet", physicalCriteriaMetCount },
            { "physicalScore", physicalScore },
            { "readingEngaged", readingEngaged },
            { "recentlyReading", recentlyReading },
            { "sustainedReading", sustainedReading },
            { "readingScore", readingScore },
            { "readingConfidence", readingConfidence },
            { "readingDuration", readingEngagedDuration },
            { "rawScore", rawEngagementScore },
            { "smoothedScore", currentEngagementScore },
            { "previousLevel", previousLevel.ToString() },
            { "newLevel", newLevel.ToString() },
            { "direction", direction },
            { "gazeSource", ControllerGazeTracker.Instance != null ? "Controller" : "Head" }
        };

        if (FirebaseManager.Instance != null && FirebaseManager.Instance.IsReady)
        {
            await FirebaseManager.Instance.LogEngagementStateAsync(
                newLevel.ToString(),
                indicators
            );
        }

        if (showDebugLogs)
        {
            Debug.Log($"🎯 [EngagementClassifier] {direction} Level: {previousLevel} → <b>{newLevel}</b>");
            Debug.Log($"   Score: raw={rawEngagementScore:F2}, smoothed={currentEngagementScore:F2} | Criteria: {physicalCriteriaMetCount}/4 | UIFix: {uiOcclusionCompensationApplied}");
            Debug.Log($"   Gaze: {gazeScore:F2} | Head: {headScore:F2} | Move: {movementScore:F2} | Reading: {readingScore:F2} (engaged={readingEngaged}, dur={readingEngagedDuration:F1}s)");
        }
    }

    void LogDetailedState()
    {
        Debug.Log($"[EngagementClassifier] Detailed State:");
        Debug.Log($"  Physical Criteria ({physicalCriteriaMetCount}/4):");
        Debug.Log($"    • Controller Gaze: {(gazeFocused ? "✓" : "✗")} (score: {gazeScore:F2}) {(uiOcclusionCompensationApplied ? "[UI FIX APPLIED]" : "")}");
        Debug.Log($"    • Head Stability: {(headStable ? "✓" : "✗")} (score: {headScore:F2})");
        Debug.Log($"    • Movement: {(movementSlow ? "✓ Slow" : "✗ Fast")} (score: {movementScore:F2})");
        Debug.Log($"    • Hand Proximity: {(handProximityEngaged ? "✓" : "✗")} (score: {handScore:F2})");
        Debug.Log($"  Reading: {readingScore:F2} ({(readingEngaged ? "✓ Engaged" : "✗")}, duration: {readingEngagedDuration:F1}s)");
        Debug.Log($"  Combined: Raw={rawEngagementScore:F2}, Smoothed={currentEngagementScore:F2}");
        Debug.Log($"  → Level: {currentEngagementLevel}");
    }

    // ============================================================================
    // PUBLIC API
    // ============================================================================

    public EngagementLevel GetEngagementLevel() => currentEngagementLevel;
    public bool IsEngaged() => currentEngagementLevel >= EngagementLevel.Engaged;
    public int GetEngagementScore() => (int)currentEngagementLevel;
    public float GetSmoothedEngagementScore() => currentEngagementScore;
    public int GetPhysicalCriteriaCount() => physicalCriteriaMetCount;
    public int GetEngagedCriteriaCount() => physicalCriteriaMetCount;

    public EngagementCriteriaBreakdown GetCriteriaBreakdown()
    {
        return new EngagementCriteriaBreakdown
        {
            handProximity = handProximityEngaged,
            headStability = headStable,
            gazeFocus = gazeFocused,
            slowMovement = movementSlow,
            readingEngaged = readingEngaged,
            physicalScore = physicalScore,
            readingScore = readingScore,
            combinedScore = currentEngagementScore,
            totalScore = physicalCriteriaMetCount + (readingEngaged ? 1 : 0)
        };
    }

    public bool IsAtPeakEngagement() => currentEngagementLevel == EngagementLevel.HighlyEngaged;
    public bool NeedsIntervention() => currentEngagementLevel <= EngagementLevel.Disengaged;

    public bool IsRecentlyReading() => readingEngaged || (Time.time - lastReadingTime < readingGracePeriod);

    // Threshold getters for LLMAdaptiveContentManager
    public float GetHighlyEngagedThreshold() => highlyEngagedThreshold;
    public float GetEngagedThreshold() => engagedThreshold;
    public float GetNeutralThreshold() => neutralThreshold;
    public float GetDisengagedThreshold() => disengagedThreshold;
    public float GetHighlyDisengagedThreshold() => highlyDisengagedThreshold;

    public string GetEngagementReason()
    {
        List<string> engaged = new List<string>();
        List<string> disengaged = new List<string>();

        if (gazeFocused) engaged.Add(uiOcclusionCompensationApplied ? "reading content" : "focused pointing");
        else disengaged.Add("scanning");

        if (headStable) engaged.Add("steady viewing");
        else disengaged.Add("looking around");

        if (movementSlow) engaged.Add("slow movement");
        else disengaged.Add("moving");

        if (handProximityEngaged) engaged.Add("hands close");

        if (readingEngaged) engaged.Add($"reading ({readingEngagedDuration:F0}s)");

        return $"Engaged: {string.Join(", ", engaged)} | Disengaged: {string.Join(", ", disengaged)}";
    }

    // ============================================================================
    // FIREBASE LOGGING
    // ============================================================================

    public async void LogCurrentStateToFirebase()
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
            return;

        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
            return;

        try
        {
            bool recentlyReading = readingEngaged || (Time.time - lastReadingTime < readingGracePeriod);
            bool sustainedReading = readingEngaged && (readingEngagedDuration >= requiredReadingDuration);

            var data = new Dictionary<string, object>
            {
                { "engagementLevel", currentEngagementLevel.ToString() },
                { "engagementScore", (int)currentEngagementLevel },
                { "smoothedScore", currentEngagementScore },
                { "rawScore", rawEngagementScore },
                { "physicalCriteriaCount", physicalCriteriaMetCount },
                { "physicalScore", physicalScore },
                { "gazeScore", gazeScore },
                { "headScore", headScore },
                { "movementScore", movementScore },
                { "movementSpeed", currentMovementSpeed },
                { "handScore", handScore },
                { "gazeFocused", gazeFocused },
                { "uiOcclusionFix", uiOcclusionCompensationApplied },
                { "headStable", headStable },
                { "movementSlow", movementSlow },
                { "handProximity", handProximityEngaged },
                { "readingScore", readingScore },
                { "readingConfidence", readingConfidence },
                { "readingEngaged", readingEngaged },
                { "recentlyReading", recentlyReading },
                { "sustainedReading", sustainedReading },
                { "readingDuration", readingEngagedDuration },
                { "gazeSource", ControllerGazeTracker.Instance != null ? "Controller" : "Head" },
                { "timestamp", Firebase.Firestore.FieldValue.ServerTimestamp },
                { "sessionTime", Time.time }
            };

            string userId = PlayerManager.Instance.userId;
            string sessionId = FirebaseManager.Instance.CurrentSessionId;

            if (string.IsNullOrEmpty(sessionId))
            {
                Debug.LogWarning("[EngagementClassifier] sessionId not available - skipping log");
                return;
            }

            string docId = $"{System.DateTime.UtcNow.Ticks}";

            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .Collection("sessions").Document(sessionId)
                .Collection("engagementStates").Document(docId)
                .SetAsync(data);

            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .Collection("engagementStates").Document(docId)
                .SetAsync(data);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[EngagementClassifier] Failed to log: {e.Message}");
        }
    }
}

// ============================================================================
// ENUMS AND DATA STRUCTURES
// ============================================================================

public enum EngagementLevel
{
    HighlyDisengaged = 0,
    Disengaged = 1,
    Neutral = 2,
    Engaged = 3,
    HighlyEngaged = 4
}

public struct EngagementCriteriaBreakdown
{
    public bool handProximity;
    public bool headStability;
    public bool gazeFocus;
    public bool slowMovement;
    public bool readingEngaged;
    public float physicalScore;
    public float readingScore;
    public float combinedScore;
    public int totalScore;
}