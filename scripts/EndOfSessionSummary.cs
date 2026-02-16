using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Generates comprehensive end-of-session summary for each participant
/// Provides final verdict: Was user engaged or disengaged overall?
/// Call this when user finishes museum experience
/// 
/// FIXED VERSION - Corrected method nesting and brace issues
/// </summary>
public class EndOfSessionSummary : MonoBehaviour
{
    public static EndOfSessionSummary Instance { get; private set; }

    [Header("Settings")]
    [Tooltip("Automatically generate summary when user exits museum")]
    public bool autoGenerateOnExit = true;

    [Header("Verdict Thresholds")]
    [Tooltip("Average engagement score threshold for 'Highly Engaged' verdict")]
    public float highlyEngagedThreshold = 3.0f;

    [Tooltip("Average engagement score threshold for 'Engaged' verdict")]
    public float engagedThreshold = 2.5f;

    [Tooltip("Average engagement score threshold for 'Neutral' verdict")]
    public float neutralThreshold = 2.0f;

    [Tooltip("Below this = 'Disengaged' verdict")]
    public float disengagedThreshold = 1.5f;

    [Header("Debug")]
    public bool showDebugLogs = true;

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

    // ============================================================================
    // PUBLIC API - Call this when user finishes
    // ============================================================================

    /// <summary>
    /// Generate and save complete session summary
    /// Call this when user clicks "End Session" or exits museum
    /// </summary>
    public async void GenerateSessionSummary()
    {
        if (showDebugLogs)
        {
            Debug.Log("════════════════════════════════════════════════════════");
            Debug.Log("  GENERATING END-OF-SESSION SUMMARY");
            Debug.Log("════════════════════════════════════════════════════════");
        }

        // Collect data from all systems
        SessionSummaryData summary = CollectAllData();

        // Calculate aggregate metrics
        CalculateAggregateMetrics(ref summary);

        // Determine final verdict
        string verdict = DetermineFinalVerdict(summary);
        summary.overallVerdict = verdict;

        // Log summary to console
        if (showDebugLogs)
        {
            PrintSummaryToConsole(summary);
        }

        // Save to Firebase
        await SaveSummaryToFirebase(summary);

        // CRITICAL: Log all subsystem summaries to Firebase
        LogAllSubsystemSummaries();

        if (showDebugLogs)
        {
            Debug.Log("════════════════════════════════════════════════════════");
            Debug.Log($"  FINAL VERDICT: {verdict}");
            Debug.Log("════════════════════════════════════════════════════════");
        }
    }

    // ============================================================================
    // DATA COLLECTION
    // ============================================================================

    /// <summary>
    /// Collect data from all tracking systems
    /// </summary>
    SessionSummaryData CollectAllData()
    {
        SessionSummaryData data = new SessionSummaryData();

        // Basic info
        data.participantId = PlayerManager.Instance?.userId ?? "Unknown";
        data.condition = ExperimentConditionManager.Instance?.condition.ToString() ?? "Unknown";
        data.totalSessionTime = Time.time;

        // Engagement data
        if (TemporalEngagementAnalyzer.Instance != null)
        {
            var temporal = TemporalEngagementAnalyzer.Instance.GetTemporalSummary();
            data.averageEngagement = temporal.averageEngagement;
            data.peakEngagement = temporal.peakEngagement.ToString();
            data.peakEngagementTime = temporal.peakEngagementTime;
            data.engagementVariance = temporal.engagementVariance;
            data.fatigueDetected = temporal.fatigueDetected;
            data.interestSpikeDetected = temporal.interestSpikeDetected;
            data.totalEngagementSamples = temporal.totalSamples;

            // Get engagement curve for distribution calculation
            var curve = TemporalEngagementAnalyzer.Instance.GetEngagementCurve();
            CalculateEngagementDistribution(curve, ref data);

            // Get optimal learning window
            var optimalWindow = TemporalEngagementAnalyzer.Instance.GetOptimalLearningWindow();
            data.optimalLearningWindowStart = optimalWindow.start;
            data.optimalLearningWindowEnd = optimalWindow.end;
        }

        // Curiosity data
        if (CuriosityTracker.Instance != null)
        {
            var curiosity = CuriosityTracker.Instance.GetCuriositySummary();
            data.curiosityScore = curiosity.curiosityScore;
            data.curiousObjectCount = curiosity.curiousObjectCount;
            data.explorationStyle = curiosity.explorationStyle.ToString();
            data.roomsVisited = curiosity.roomsVisited;
        }

        // Badge and card data
        if (BadgeManager.instance != null)
        {
            data.totalCardsCollected = BadgeManager.instance.GetTotalCardsCollected();
            data.totalBadgesUnlocked = BadgeManager.instance.GetUnlockedBadges().Count;
        }

        // Score data
        if (ScoreManager.Instance != null)
        {
            data.totalScore = ScoreManager.Instance.GetScore();
        }

        return data;
    }

    /// <summary>
    /// Calculate percentage of time in each engagement level
    /// </summary>
    void CalculateEngagementDistribution(List<EngagementDataPoint> curve, ref SessionSummaryData data)
    {
        if (curve == null || curve.Count == 0) return;

        int highlyEngaged = curve.Count(e => e.engagementLevel == EngagementLevel.HighlyEngaged);
        int engaged = curve.Count(e => e.engagementLevel == EngagementLevel.Engaged);
        int neutral = curve.Count(e => e.engagementLevel == EngagementLevel.Neutral);
        int disengaged = curve.Count(e => e.engagementLevel == EngagementLevel.Disengaged);
        int highlyDisengaged = curve.Count(e => e.engagementLevel == EngagementLevel.HighlyDisengaged);

        int total = curve.Count;

        data.percentHighlyEngaged = (highlyEngaged / (float)total) * 100f;
        data.percentEngaged = (engaged / (float)total) * 100f;
        data.percentNeutral = (neutral / (float)total) * 100f;
        data.percentDisengaged = (disengaged / (float)total) * 100f;
        data.percentHighlyDisengaged = (highlyDisengaged / (float)total) * 100f;

        // Calculate time in each state (samples * sample interval)
        float sampleInterval = TemporalEngagementAnalyzer.Instance != null
            ? TemporalEngagementAnalyzer.Instance.sampleInterval
            : 5f;

        data.timeHighlyEngaged = highlyEngaged * sampleInterval;
        data.timeEngaged = engaged * sampleInterval;
        data.timeNeutral = neutral * sampleInterval;
        data.timeDisengaged = disengaged * sampleInterval;
        data.timeHighlyDisengaged = highlyDisengaged * sampleInterval;
    }

    // ============================================================================
    // AGGREGATE METRICS
    // ============================================================================

    /// <summary>
    /// Calculate additional aggregate metrics
    /// </summary>
    void CalculateAggregateMetrics(ref SessionSummaryData data)
    {
        // Engagement consistency (lower variance = more consistent)
        if (data.engagementVariance < 0.5f)
            data.engagementConsistency = "Very Consistent";
        else if (data.engagementVariance < 1.0f)
            data.engagementConsistency = "Consistent";
        else if (data.engagementVariance < 1.5f)
            data.engagementConsistency = "Variable";
        else
            data.engagementConsistency = "Highly Variable";

        // Curiosity level
        if (data.curiosityScore >= 70f)
            data.curiosityLevel = "Highly Curious";
        else if (data.curiosityScore >= 50f)
            data.curiosityLevel = "Moderately Curious";
        else if (data.curiosityScore >= 30f)
            data.curiosityLevel = "Somewhat Curious";
        else
            data.curiosityLevel = "Low Curiosity";

        // Completion rate
        data.completionRate = (data.totalCardsCollected / 18f) * 100f; // 18 total cards

        // Learning effectiveness (combined metric)
        data.learningEffectivenessScore =
            (data.averageEngagement * 25f) +
            (data.curiosityScore * 0.5f) +
            (data.completionRate * 0.25f);
    }

    // ============================================================================
    // FINAL VERDICT
    // ============================================================================

    /// <summary>
    /// Determine overall engagement verdict for this user
    /// </summary>
    string DetermineFinalVerdict(SessionSummaryData data)
    {
        float avgEngagement = data.averageEngagement;

        // Primary determination: Average engagement score
        string primaryVerdict;
        if (avgEngagement >= highlyEngagedThreshold)
            primaryVerdict = "Highly Engaged";
        else if (avgEngagement >= engagedThreshold)
            primaryVerdict = "Engaged";
        else if (avgEngagement >= neutralThreshold)
            primaryVerdict = "Moderately Engaged";
        else if (avgEngagement >= disengagedThreshold)
            primaryVerdict = "Disengaged";
        else
            primaryVerdict = "Highly Disengaged";

        // Modifiers based on other factors
        List<string> modifiers = new List<string>();

        if (data.curiosityScore >= 70f && avgEngagement >= engagedThreshold)
        {
            modifiers.Add("Highly Curious");
        }

        if (data.fatigueDetected && avgEngagement < engagedThreshold)
        {
            modifiers.Add("With Fatigue");
        }

        if (data.explorationStyle == "Systematic")
        {
            modifiers.Add("Systematic Explorer");
        }

        if (data.completionRate >= 80f)
        {
            modifiers.Add("High Completion");
        }

        // Construct final verdict
        string finalVerdict = primaryVerdict;
        if (modifiers.Count > 0)
        {
            finalVerdict += $" ({string.Join(", ", modifiers)})";
        }

        return finalVerdict;
    }

    // ============================================================================
    // CONSOLE OUTPUT
    // ============================================================================

    /// <summary>
    /// Print comprehensive summary to console
    /// </summary>
    void PrintSummaryToConsole(SessionSummaryData data)
    {
        Debug.Log("\n");
        Debug.Log("╔════════════════════════════════════════════════════════════╗");
        Debug.Log($"║  SESSION SUMMARY: {data.participantId,-38} ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log($"║  Condition: {data.condition,-46} ║");
        Debug.Log($"║  Session Duration: {data.totalSessionTime:F0}s ({data.totalSessionTime / 60f:F1} minutes)          ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log("║  ENGAGEMENT METRICS                                       ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log($"║  Average Engagement: {data.averageEngagement:F2}/4.0 ({GetEngagementLabel(data.averageEngagement)})     ║");
        Debug.Log($"║  Peak Engagement: {data.peakEngagement,-36} ║");
        Debug.Log($"║  Engagement Consistency: {data.engagementConsistency,-32} ║");
        Debug.Log("║                                                           ║");
        Debug.Log("║  Time Distribution:                                       ║");
        Debug.Log($"║    Highly Engaged:   {data.percentHighlyEngaged,5:F1}% ({data.timeHighlyEngaged,5:F0}s)              ║");
        Debug.Log($"║    Engaged:          {data.percentEngaged,5:F1}% ({data.timeEngaged,5:F0}s)              ║");
        Debug.Log($"║    Neutral:          {data.percentNeutral,5:F1}% ({data.timeNeutral,5:F0}s)              ║");
        Debug.Log($"║    Disengaged:       {data.percentDisengaged,5:F1}% ({data.timeDisengaged,5:F0}s)              ║");
        Debug.Log($"║    Highly Disengaged: {data.percentHighlyDisengaged,5:F1}% ({data.timeHighlyDisengaged,5:F0}s)              ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log("║  CURIOSITY & EXPLORATION                                  ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log($"║  Curiosity Score: {data.curiosityScore:F1}/100 ({data.curiosityLevel})          ║");
        Debug.Log($"║  Curious Objects: {data.curiousObjectCount,-40} ║");
        Debug.Log($"║  Exploration Style: {data.explorationStyle,-38} ║");
        Debug.Log($"║  Rooms Visited: {data.roomsVisited,-42} ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log("║  TEMPORAL PATTERNS                                        ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log($"║  Fatigue Detected: {(data.fatigueDetected ? "YES ⚠️" : "NO"),-41} ║");
        Debug.Log($"║  Interest Spikes: {(data.interestSpikeDetected ? "YES 📈" : "NO"),-42} ║");
        Debug.Log($"║  Optimal Learning Window: {data.optimalLearningWindowStart:F0}s - {data.optimalLearningWindowEnd:F0}s           ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log("║  LEARNING OUTCOMES                                        ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log($"║  Cards Collected: {data.totalCardsCollected}/18 ({data.completionRate:F0}%)                    ║");
        Debug.Log($"║  Badges Unlocked: {data.totalBadgesUnlocked,-40} ║");
        Debug.Log($"║  Total Score: {data.totalScore,-44} ║");
        Debug.Log($"║  Learning Effectiveness: {data.learningEffectivenessScore:F1}/100                   ║");
        Debug.Log("╠════════════════════════════════════════════════════════════╣");
        Debug.Log($"║  FINAL VERDICT: {data.overallVerdict,-42} ║");
        Debug.Log("╚════════════════════════════════════════════════════════════╝");
        Debug.Log("\n");
    }

    string GetEngagementLabel(float score)
    {
        if (score >= 3.5f) return "Excellent";
        if (score >= 3.0f) return "Very Good";
        if (score >= 2.5f) return "Good";
        if (score >= 2.0f) return "Fair";
        if (score >= 1.5f) return "Poor";
        return "Very Poor";
    }

    // ============================================================================
    // FIREBASE SAVE
    // ============================================================================

    /// <summary>
    /// Save summary to Firebase
    /// </summary>
    async System.Threading.Tasks.Task SaveSummaryToFirebase(SessionSummaryData data)
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
        {
            Debug.LogError("[EndOfSessionSummary] Firebase not ready!");
            return;
        }

        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            Debug.LogError("[EndOfSessionSummary] User not logged in!");
            return;
        }

        try
        {
            var firebaseData = new Dictionary<string, object>
            {
                // Basic info
                { "participantId", data.participantId },
                { "condition", data.condition },
                { "sessionDuration", data.totalSessionTime },
                
                // Engagement metrics
                { "averageEngagement", data.averageEngagement },
                { "peakEngagement", data.peakEngagement },
                { "peakEngagementTime", data.peakEngagementTime },
                { "engagementVariance", data.engagementVariance },
                { "engagementConsistency", data.engagementConsistency },
                
                // Time distribution
                { "percentHighlyEngaged", data.percentHighlyEngaged },
                { "percentEngaged", data.percentEngaged },
                { "percentNeutral", data.percentNeutral },
                { "percentDisengaged", data.percentDisengaged },
                { "percentHighlyDisengaged", data.percentHighlyDisengaged },
                { "timeHighlyEngaged", data.timeHighlyEngaged },
                { "timeEngaged", data.timeEngaged },
                { "timeNeutral", data.timeNeutral },
                { "timeDisengaged", data.timeDisengaged },
                { "timeHighlyDisengaged", data.timeHighlyDisengaged },
                
                // Curiosity
                { "curiosityScore", data.curiosityScore },
                { "curiosityLevel", data.curiosityLevel },
                { "curiousObjectCount", data.curiousObjectCount },
                { "explorationStyle", data.explorationStyle },
                { "roomsVisited", data.roomsVisited },
                
                // Temporal patterns
                { "fatigueDetected", data.fatigueDetected },
                { "interestSpikeDetected", data.interestSpikeDetected },
                { "optimalLearningWindowStart", data.optimalLearningWindowStart },
                { "optimalLearningWindowEnd", data.optimalLearningWindowEnd },
                
                // Learning outcomes
                { "totalCardsCollected", data.totalCardsCollected },
                { "totalBadgesUnlocked", data.totalBadgesUnlocked },
                { "completionRate", data.completionRate },
                { "totalScore", data.totalScore },
                { "learningEffectivenessScore", data.learningEffectivenessScore },
                
                // Final verdict
                { "overallVerdict", data.overallVerdict },
                
                // Metadata
                { "timestamp", Firebase.Firestore.FieldValue.ServerTimestamp },
                { "totalSamples", data.totalEngagementSamples }
            };

            string userId = PlayerManager.Instance.userId;

            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .Collection("sessionSummary").Document("final")
                .SetAsync(firebaseData);

            if (showDebugLogs)
            {
                Debug.Log("✅ [EndOfSessionSummary] Summary saved to Firebase!");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[EndOfSessionSummary] Failed to save summary: {e.Message}");
        }
    }

    // ============================================================================
    // SUBSYSTEM SUMMARIES - MUST BE SEPARATE METHOD (was incorrectly nested before)
    // ============================================================================

    /// <summary>
    /// CRITICAL: Log all subsystem summaries to Firebase
    /// Must be called at session end to capture complete data
    /// </summary>
    void LogAllSubsystemSummaries()
    {
        if (showDebugLogs)
        {
            Debug.Log("[EndOfSessionSummary] Logging all subsystem summaries...");
        }

        // Log reading behavior summary
        if (ReadingBehaviorTracker.Instance != null)
        {
            ReadingBehaviorTracker.Instance.LogFinalSummaryToFirebase();
            if (showDebugLogs)
                Debug.Log("   ✓ Reading behavior summary logged");
        }
        else
        {
            Debug.LogWarning("   ✗ ReadingBehaviorTracker not found!");
        }

        // Log temporal engagement summary
        if (TemporalEngagementAnalyzer.Instance != null)
        {
            TemporalEngagementAnalyzer.Instance.LogTemporalSummaryToFirebase();
            if (showDebugLogs)
                Debug.Log("   ✓ Temporal engagement summary logged");
        }
        else
        {
            Debug.LogWarning("   ✗ TemporalEngagementAnalyzer not found!");
        }

        // Log curiosity/exploration summary
        if (CuriosityTracker.Instance != null)
        {
            CuriosityTracker.Instance.LogExplorationSummaryToFirebase();
            if (showDebugLogs)
                Debug.Log("   ✓ Curiosity/exploration summary logged");
        }
        else
        {
            Debug.LogWarning("   ✗ CuriosityTracker not found!");
        }

        if (showDebugLogs)
        {
            Debug.Log("[EndOfSessionSummary] All subsystem summaries complete!");
        }
    }

    // ============================================================================
    // UTILITY
    // ============================================================================

    /// <summary>
    /// Get simple summary for quick display
    /// </summary>
    public string GetQuickSummary()
    {
        if (TemporalEngagementAnalyzer.Instance == null) return "No data available";

        float avgEngagement = TemporalEngagementAnalyzer.Instance.GetAverageEngagement();
        string verdict = avgEngagement >= engagedThreshold ? "ENGAGED" : "DISENGAGED";

        return $"{verdict} (Avg: {avgEngagement:F2}/4.0)";
    }
}

// ============================================================================
// DATA STRUCTURE
// ============================================================================

[System.Serializable]
public class SessionSummaryData
{
    // Basic info
    public string participantId;
    public string condition;
    public float totalSessionTime;

    // Engagement metrics
    public float averageEngagement;
    public string peakEngagement;
    public float peakEngagementTime;
    public float engagementVariance;
    public string engagementConsistency;
    public int totalEngagementSamples;

    // Engagement distribution
    public float percentHighlyEngaged;
    public float percentEngaged;
    public float percentNeutral;
    public float percentDisengaged;
    public float percentHighlyDisengaged;
    public float timeHighlyEngaged;
    public float timeEngaged;
    public float timeNeutral;
    public float timeDisengaged;
    public float timeHighlyDisengaged;

    // Curiosity
    public float curiosityScore;
    public string curiosityLevel;
    public int curiousObjectCount;
    public string explorationStyle;
    public int roomsVisited;

    // Temporal patterns
    public bool fatigueDetected;
    public bool interestSpikeDetected;
    public float optimalLearningWindowStart;
    public float optimalLearningWindowEnd;

    // Learning outcomes
    public int totalCardsCollected;
    public int totalBadgesUnlocked;
    public float completionRate;
    public int totalScore;
    public float learningEffectivenessScore;

    // Final verdict
    public string overallVerdict;
}