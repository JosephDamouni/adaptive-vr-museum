using UnityEngine;

/// <summary>
/// Manages experimental conditions for IUI 2026 research study
/// Controls whether user gets STATIC (Group A) or ADAPTIVE (Group B) content
/// 
/// IMPORTANT FIX: Now collects tracking data for BOTH groups (for research comparison)
/// Only the CONTENT ADAPTATION is disabled for Static group
/// </summary>
public class ExperimentConditionManager : MonoBehaviour
{
    public static ExperimentConditionManager Instance { get; private set; }

    [Header("Experimental Condition")]
    [Tooltip("Which condition is this participant in? (set automatically from login)")]
    public ExperimentCondition condition = ExperimentCondition.Static;

    [Header("Participant Info")]
    [Tooltip("Participant number (extracted from login code like P001, P002)")]
    public int participantNumber = 0;

    [Header("Debug")]
    public bool showDebugLogs = true;

    // Public accessors
    public bool IsAdaptiveCondition => condition == ExperimentCondition.Adaptive;
    public bool IsStaticCondition => condition == ExperimentCondition.Static;

    private bool hasBeenConfigured = false;

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
            return;
        }

        if (showDebugLogs)
        {
            Debug.Log("[ExperimentConditionManager] Waiting for participant login...");
        }
    }

    void Update()
    {
        // Wait for PlayerManager to have a userId (from login)
        if (!hasBeenConfigured && PlayerManager.Instance != null && !string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            ConfigureFromParticipantCode(PlayerManager.Instance.userId);
            hasBeenConfigured = true;
        }
    }

    /// <summary>
    /// Configure condition based on participant code from login
    /// </summary>
    public void ConfigureFromParticipantCode(string participantCode)
    {
        // Extract number from participant code (e.g., "P001" → 1, "P015" → 15)
        participantNumber = ExtractParticipantNumber(participantCode);

        if (participantNumber == 0)
        {
            Debug.LogError($"[ExperimentConditionManager] Could not extract number from '{participantCode}'");
            return;
        }

        // Auto-assign condition: odd = Static, even = Adaptive
        AssignConditionByParticipantNumber();

        // Log condition
        if (showDebugLogs)
        {
            Debug.Log($"╔═══════════════════════════════════════════╗");
            Debug.Log($"║  PARTICIPANT CODE: {participantCode}");
            Debug.Log($"║  PARTICIPANT NUMBER: {participantNumber}");
            Debug.Log($"║  EXPERIMENT CONDITION: {condition.ToString().ToUpper()}");
            Debug.Log($"║  Content Adaptation: {(IsAdaptiveCondition ? "ENABLED" : "DISABLED")}");
            Debug.Log($"║  Data Collection: ENABLED (both groups)");
            Debug.Log($"╚═══════════════════════════════════════════╝");
        }

        // ✅ FIX: Keep ALL trackers enabled for BOTH groups (for research data)
        // Only content adaptation behavior differs
        ConfigureEngagementTracking();

        // Log to Firebase
        LogConditionToFirebase();
    }

    /// <summary>
    /// Extract participant number from code like "P001", "P015", "001", "15", etc.
    /// </summary>
    int ExtractParticipantNumber(string code)
    {
        if (string.IsNullOrEmpty(code))
            return 0;

        // Remove any non-digit characters
        string digitsOnly = "";
        foreach (char c in code)
        {
            if (char.IsDigit(c))
                digitsOnly += c;
        }

        // Try to parse the number
        if (int.TryParse(digitsOnly, out int number))
        {
            return number;
        }

        return 0;
    }

    /// <summary>
    /// Automatically assign condition: odd numbers = Static, even = Adaptive
    /// This ensures balanced assignment across participants
    /// </summary>
    void AssignConditionByParticipantNumber()
    {
        if (participantNumber % 2 == 0)
        {
            condition = ExperimentCondition.Adaptive;
        }
        else
        {
            condition = ExperimentCondition.Static;
        }

        if (showDebugLogs)
        {
            Debug.Log($"[ExperimentConditionManager] Auto-assigned P{participantNumber:D3} to {condition} condition");
        }
    }

    /// <summary>
    /// ✅ FIXED: Enable engagement tracking for BOTH groups
    /// This allows comparing engagement patterns between Control and Adaptive
    /// Only content adaptation uses the IsAdaptiveCondition flag
    /// </summary>
    void ConfigureEngagementTracking()
    {
        // ✅ ALWAYS enable trackers - they collect data for research analysis
        // The LLMAdaptiveContentManager checks IsAdaptiveCondition before adapting content
        
        if (HandProximityTracker.Instance != null)
        {
            HandProximityTracker.Instance.enabled = true;
            if (showDebugLogs)
                Debug.Log("[ExperimentConditionManager] HandProximityTracker ENABLED (data collection)");
        }

        if (HeadTrackingAnalyzer.Instance != null)
        {
            HeadTrackingAnalyzer.Instance.enabled = true;
            if (showDebugLogs)
                Debug.Log("[ExperimentConditionManager] HeadTrackingAnalyzer ENABLED (data collection)");
        }

        if (EngagementClassifier.Instance != null)
        {
            EngagementClassifier.Instance.enabled = true;
            if (showDebugLogs)
                Debug.Log("[ExperimentConditionManager] EngagementClassifier ENABLED (data collection)");
        }

        if (showDebugLogs)
        {
            if (IsStaticCondition)
            {
                Debug.Log("[ExperimentConditionManager] 📊 Static: Tracking ON, Adaptation OFF");
            }
            else
            {
                Debug.Log("[ExperimentConditionManager] 🔄 Adaptive: Tracking ON, Adaptation ON");
            }
        }
    }

    /// <summary>
    /// Log which condition this participant is in to Firebase
    /// </summary>
    async void LogConditionToFirebase()
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
            return;

        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
            return;

        try
        {
            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                { "condition", condition.ToString() },
                { "participantNumber", participantNumber },
                { "adaptiveEnabled", IsAdaptiveCondition },
                { "trackingEnabled", true }, // ✅ Always true now
                { "timestamp", Firebase.Firestore.FieldValue.ServerTimestamp }
            };

            string userId = PlayerManager.Instance.userId;

            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .Collection("experimentInfo").Document("condition")
                .SetAsync(data);

            // ✅ Also update the root user document with groupAssignment for dashboard compatibility
            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .SetAsync(new System.Collections.Generic.Dictionary<string, object>
                {
                    { "groupAssignment", IsStaticCondition ? "Control" : "Adaptive" }
                }, Firebase.Firestore.SetOptions.MergeAll);

            if (showDebugLogs)
                Debug.Log($"[ExperimentConditionManager] Logged condition to Firebase: {condition}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ExperimentConditionManager] Failed to log condition: {e.Message}");
        }
    }

    /// <summary>
    /// Get the appropriate content version based on condition and engagement
    /// </summary>
    public ContentVersion GetContentVersion()
    {
        if (IsStaticCondition)
        {
            // Static condition: ALWAYS show standard content (medium detail)
            return ContentVersion.Standard;
        }
        else
        {
            // Adaptive condition: Check engagement classifier
            if (EngagementClassifier.Instance != null && EngagementClassifier.Instance.IsEngaged())
            {
                return ContentVersion.Detailed;
            }
            else
            {
                return ContentVersion.Brief;
            }
        }
    }

    /// <summary>
    /// Check if content should adapt (only in Adaptive condition)
    /// </summary>
    public bool ShouldAdaptContent()
    {
        return IsAdaptiveCondition;
    }
}

/// <summary>
/// Experimental conditions for the study
/// </summary>
public enum ExperimentCondition
{
    Static,    // Group A: No adaptation (control) - but still collects tracking data
    Adaptive   // Group B: Real-time adaptation (experimental)
}

/// <summary>
/// Content detail levels
/// </summary>
public enum ContentVersion
{
    Brief,      // Quick summary (50 words, 3 bullets) - for disengaged
    Standard,   // Medium detail (100 words) - for static condition
    Detailed    // Full content (150-200 words) - for engaged
}