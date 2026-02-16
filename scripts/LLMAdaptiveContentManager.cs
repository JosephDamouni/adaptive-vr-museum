using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// ADAPTATION STAGE: LLM-powered adaptive content generation
/// 
/// This component completes the closed-loop adaptive system:
/// 
/// [User reads content] → [ReadingBehaviorTracker measures] → [EngagementClassifier classifies] →
/// [LLMAdaptiveContentManager adapts] → [User reads NEW adapted content] → (loop continues)
/// 
/// Architecture (LEVEL-BASED with score interpolation):
/// - Receives engagement LEVEL from EngagementClassifier (primary constraint)
/// - Uses smoothed score only for interpolation WITHIN level bounds
/// - Enforces HARD CAPS: disengaged users always get short content
/// - Bonuses only apply to Neutral+ engagement levels
/// 
/// ENGAGEMENT WINDOW (Request-Time Smoothing):
/// - Tracks engagement samples over a rolling window (default 4 seconds)
/// - At content request time, uses BEST engagement sample from recent window
/// - Prevents click-moment movement from reducing content for engaged users
/// - Safety: Current level caps still enforced (disengaged = short content)
/// - Reading context: If recently reading in window, boosts to at least Engaged
/// 
/// Content Length Hierarchy:
/// - HighlyEngaged: highWords → maxWords (longest content)
/// - Engaged: medWords → highWords
/// - Neutral: lowWords → medWords  
/// - Disengaged: minWords → lowWords (CAPPED)
/// - HighlyDisengaged: minWords only (HARD CAP)
/// 
/// Key Design Decisions:
/// 1. engagementLevel sets HARD CAPS (score cannot override disengagement)
/// 2. smoothedScore interpolates WITHIN level bounds only
/// 3. Bonuses disabled for Disengaged/HighlyDisengaged
/// 4. Engagement window selects SINGLE best sample (level priority, score tiebreaker)
/// 5. All adaptations logged to Firebase for research analysis
/// </summary>
public class LLMAdaptiveContentManager : MonoBehaviour
{
    public static LLMAdaptiveContentManager Instance { get; private set; }

    // ============================================================================
    // API CONFIGURATION
    // ============================================================================

    [Header("OpenAI API Settings")]
    [Tooltip("Your OpenAI API key")]
    public string openAIApiKey = "sk-...";

    [Tooltip("Which model to use")]
    public string modelName = "gpt-3.5-turbo";

    [Tooltip("Maximum time to wait for API response (seconds)")]
    public float apiTimeout = 30f;

    // ============================================================================
    // ADAPTIVE CONTENT SETTINGS - CARDS
    // ============================================================================

    [Header("Card Content Lengths (words)")]
    [Tooltip("Words for highly engaged users")]
    public int cardMaxWords = 120;

    [Tooltip("Words for engaged users")]
    public int cardHighWords = 80;

    [Tooltip("Words for neutral users")]
    public int cardMediumWords = 50;

    [Tooltip("Words for disengaged users")]
    public int cardLowWords = 30;

    [Tooltip("Words for highly disengaged users")]
    public int cardMinWords = 15;

    // ============================================================================
    // ADAPTIVE CONTENT SETTINGS - OBJECTS
    // ============================================================================

    [Header("Object Content Lengths (words)")]
    [Tooltip("Words for highly engaged users")]
    public int objectMaxWords = 70;

    [Tooltip("Words for engaged users")]
    public int objectHighWords = 50;

    [Tooltip("Words for neutral users")]
    public int objectMediumWords = 30;

    [Tooltip("Words for disengaged users")]
    public int objectLowWords = 15;

    [Tooltip("Words for highly disengaged users")]
    public int objectMinWords = 10;

    // ============================================================================
    // CURIOSITY & ENGAGEMENT BONUSES
    // ============================================================================

    [Header("Engagement Modifiers")]
    [Tooltip("Extra words if user is curious about this specific topic")]
    public int curiosityBonusWords = 0;

    [Tooltip("Extra words if reading engagement is high (even if other signals neutral)")]
    public int readingEngagementBonusWords = 0;

    [Tooltip("Use smoothed score for interpolation (vs discrete levels)")]
    public bool useSmoothedScore = true;

    // ============================================================================
    // ENGAGEMENT WINDOW SETTINGS
    // ============================================================================

    [Header("Engagement Window (Request-Time Smoothing)")]
    [Tooltip("Enable peak engagement window - uses best engagement from recent seconds instead of exact click moment")]
    public bool enableEngagementWindow = true;

    [Tooltip("How many seconds of recent engagement to consider (recommended: 3-5s)")]
    [Range(1f, 10f)]
    public float requestWindowSeconds = 4f;

    [Tooltip("How often to sample engagement (seconds)")]
    [Range(0.1f, 1f)]
    public float windowSampleInterval = 0.5f;

    // ============================================================================
    // FALLBACK
    // ============================================================================

    [Header("Fallback Content")]
    [Tooltip("Show this if API fails")]
    public string fallbackContent = "Content temporarily unavailable. Please continue exploring.";

    [Header("Debug")]
    public bool showDebugLogs = true;
    public bool showDetailedLogs = false;

    // ============================================================================
    // INTERNAL STATE
    // ============================================================================

    // Engagement window samples
    private struct EngagementSample
    {
        public float timestamp;
        public EngagementLevel level;
        public float score;
        public bool recentlyReading;
    }
    private List<EngagementSample> engagementWindow = new List<EngagementSample>();
    private float lastSampleTime = 0f;

    // Cache to avoid regenerating same content
    private Dictionary<string, CachedContent> contentCache = new Dictionary<string, CachedContent>();
    private float cacheExpirySeconds = 300f; // 5 minutes

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
        if (showDebugLogs)
        {
            Debug.Log("✅ [LLMAdaptiveContentManager] ADAPTATION STAGE initialized");
            Debug.Log($"   Card words: {cardMinWords}-{cardMaxWords}, Object words: {objectMinWords}-{objectMaxWords}");
            Debug.Log($"   Smoothed adaptation: {(useSmoothedScore ? "ENABLED" : "DISABLED")}");
            Debug.Log($"   Engagement window: {(enableEngagementWindow ? $"ENABLED ({requestWindowSeconds}s)" : "DISABLED")}");
        }
    }

    void Update()
    {
        // Sample engagement periodically for the rolling window
        if (!enableEngagementWindow) return;
        if (Time.time - lastSampleTime < windowSampleInterval) return;

        lastSampleTime = Time.time;
        SampleEngagement();
    }

    // ============================================================================
    // ENGAGEMENT WINDOW - Continuous Sampling
    // ============================================================================

    /// <summary>
    /// Sample current engagement and add to rolling window.
    /// Uses EngagementClassifier.IsRecentlyReading() to match classifier's grace period logic.
    /// </summary>
    void SampleEngagement()
    {
        if (EngagementClassifier.Instance == null) return;

        // Get current engagement state
        EngagementLevel level = EngagementClassifier.Instance.GetEngagementLevel();
        float score = EngagementClassifier.Instance.GetSmoothedEngagementScore();

        // FIX #1: Use classifier's IsRecentlyReading() which includes grace period
        // This matches the same "recentlyReading" logic used for gating decisions
        bool recentlyReading = EngagementClassifier.Instance.IsRecentlyReading();

        // Add sample
        engagementWindow.Add(new EngagementSample
        {
            timestamp = Time.time,
            level = level,
            score = score,
            recentlyReading = recentlyReading
        });

        // Prune old samples outside the window
        float cutoffTime = Time.time - requestWindowSeconds;
        engagementWindow.RemoveAll(s => s.timestamp < cutoffTime);
    }

    // ============================================================================
    // ENGAGEMENT WINDOW - Request-Time Computation
    // ============================================================================

    /// <summary>
    /// Compute request-time engagement using the rolling window.
    /// FIX #2: Selects SINGLE best sample (highest level, then highest score as tiebreaker).
    /// Returns that sample's level and score, with safety constraints.
    /// </summary>
    void ComputeRequestEngagement(
        EngagementLevel currentLevel,
        float currentScore,
        out EngagementLevel requestLevel,
        out float requestScore,
        out bool wasRecentlyReading)
    {
        // Default to current values
        requestLevel = currentLevel;
        requestScore = currentScore;
        wasRecentlyReading = false;

        // If window disabled or empty, use current values
        if (!enableEngagementWindow || engagementWindow.Count == 0)
        {
            return;
        }

        // =========================================================================
        // SAFETY CHECK: If currently Disengaged/HighlyDisengaged, don't override
        // Rationale: Movement gating is intentional; user is truly disengaged NOW
        // =========================================================================
        if (currentLevel <= EngagementLevel.Disengaged)
        {
            if (showDetailedLogs)
            {
                Debug.Log($"[EngagementWindow] Safety: Current level is {currentLevel}, skipping window peak");
            }
            return;
        }

        // =========================================================================
        // FIX #2: Find SINGLE best sample (highest level, then highest score)
        // =========================================================================
        EngagementSample bestSample = engagementWindow[0];
        bool anyRecentlyReading = engagementWindow[0].recentlyReading;

        for (int i = 1; i < engagementWindow.Count; i++)
        {
            var sample = engagementWindow[i];

            // Track if ANY sample had recentlyReading
            if (sample.recentlyReading)
            {
                anyRecentlyReading = true;
            }

            // Compare: higher level wins, then higher score breaks ties
            if ((int)sample.level > (int)bestSample.level)
            {
                bestSample = sample;
            }
            else if (sample.level == bestSample.level && sample.score > bestSample.score)
            {
                bestSample = sample;
            }
        }

        wasRecentlyReading = anyRecentlyReading;

        // =========================================================================
        // APPLY BEST SAMPLE (only if it improves engagement)
        // =========================================================================
        if ((int)bestSample.level > (int)currentLevel || bestSample.score > currentScore)
        {
            requestLevel = bestSample.level;
            requestScore = bestSample.score;
        }

        // =========================================================================
        // READING CONTEXT BOOST
        // If user was reading recently in window, ensure at least Engaged level
        // (Only applies if current level is Neutral+, not overriding safety)
        // =========================================================================
        if (anyRecentlyReading && requestLevel == EngagementLevel.Neutral)
        {
            requestLevel = EngagementLevel.Engaged;
            // Also boost score to match Engaged threshold
            if (EngagementClassifier.Instance != null)
            {
                float engagedThreshold = EngagementClassifier.Instance.GetEngagedThreshold();
                if (requestScore < engagedThreshold)
                {
                    requestScore = engagedThreshold;
                }
            }
            if (showDetailedLogs)
            {
                Debug.Log("[EngagementWindow] Reading boost: Neutral → Engaged");
            }
        }
    }

    // ============================================================================
    // MAIN API - GetAdaptiveContent
    // ============================================================================

    /// <summary>
    /// Get adaptive content for an item.
    /// Uses engagement window to capture peak engagement from recent seconds.
    /// </summary>
    public void GetAdaptiveContent(string itemId, string itemName, ContentType contentType, System.Action<string> callback)
    {
        // Check if static condition (control group)
        if (ExperimentConditionManager.Instance != null &&
            ExperimentConditionManager.Instance.IsStaticCondition)
        {
            // Static: Generate standard (medium) content
            int staticWords = contentType == ContentType.Card ? cardMediumWords : objectMediumWords;

            if (showDebugLogs)
            {
                Debug.Log($"📄 [LLMAdaptiveContentManager] STATIC condition → {staticWords} words (no adaptation)");
            }

            StartCoroutine(GenerateLLMContent(
                itemId, itemName, staticWords,
                EngagementLevel.Neutral, 0.5f, false, false,
                contentType, callback));
            return;
        }

        // ADAPTIVE: Get CURRENT engagement data from classifier
        EngagementLevel currentLevel = EngagementLevel.Neutral;
        float currentScore = 0.5f;

        if (EngagementClassifier.Instance != null)
        {
            currentLevel = EngagementClassifier.Instance.GetEngagementLevel();
            currentScore = EngagementClassifier.Instance.GetSmoothedEngagementScore();
        }

        // =========================================================================
        // ENGAGEMENT WINDOW: Use best engagement from recent seconds
        // =========================================================================
        EngagementLevel requestLevel;
        float requestScore;
        bool wasRecentlyReading;

        ComputeRequestEngagement(currentLevel, currentScore, out requestLevel, out requestScore, out wasRecentlyReading);

        // Check reading engagement (current OR from window)
        bool hasHighReadingEngagement = wasRecentlyReading;
        if (ReadingBehaviorTracker.Instance != null)
        {
            hasHighReadingEngagement = hasHighReadingEngagement || ReadingBehaviorTracker.Instance.IsReadingEngaged();
        }

        // Check curiosity
        bool isCurious = false;
        if (CuriosityTracker.Instance != null)
        {
            isCurious = CuriosityTracker.Instance.IsObjectOfCuriosity(itemId) ||
                       (HeadTrackingAnalyzer.Instance != null && HeadTrackingAnalyzer.Instance.IsCurious());
        }

        // =========================================================================
        // DEBUG: Show window effect
        // =========================================================================
        if (showDebugLogs && enableEngagementWindow)
        {
            bool windowUsed = (requestLevel != currentLevel || Mathf.Abs(requestScore - currentScore) > 0.01f);
            Debug.Log($" [EngagementWindow] Current: {currentLevel} ({currentScore:F2}) | Best: {requestLevel} ({requestScore:F2}) | Used: {(windowUsed ? "WINDOW" : "CURRENT")}");
        }

        // Calculate target word count based on REQUEST engagement (from window)
        int targetWords = CalculateWordCount(requestLevel, requestScore, contentType);

        // =========================================================================
        // BONUSES - Only apply when user is actually engaged
        // =========================================================================
        bool bonusesAllowed = requestLevel >= EngagementLevel.Neutral;

        if (bonusesAllowed && isCurious && curiosityBonusWords > 0)
        {
            targetWords += curiosityBonusWords;
            if (showDebugLogs)
            {
                Debug.Log($"    Curiosity bonus: +{curiosityBonusWords} words");
            }
        }

        if (bonusesAllowed && hasHighReadingEngagement && requestLevel < EngagementLevel.Engaged && readingEngagementBonusWords > 0)
        {
            targetWords += readingEngagementBonusWords;
            if (showDebugLogs)
            {
                Debug.Log($"Reading engagement bonus: +{readingEngagementBonusWords} words");
            }
        }

        // =========================================================================
        // HARD CAP ENFORCEMENT - Use CURRENT level for safety cap
        // This ensures movement gating still works even if window had higher engagement
        // =========================================================================
        int maxAllowedWords = GetMaxWordsForLevel(currentLevel, contentType);
        if (targetWords > maxAllowedWords)
        {
            if (showDebugLogs)
            {
                Debug.Log($" Word count capped by current level: {targetWords} → {maxAllowedWords} (current={currentLevel})");
            }
            targetWords = maxAllowedWords;
        }

        if (showDebugLogs)
        {
            Debug.Log($"[LLMAdaptiveContentManager] ADAPTIVE content for '{itemName}'");
            Debug.Log($"   Final: {requestLevel} ({requestScore:F2}) → {targetWords} words ({contentType})");
            Debug.Log($"   Window: {engagementWindow.Count} samples | RecentlyReading: {wasRecentlyReading}");
        }

        StartCoroutine(GenerateLLMContent(
            itemId, itemName, targetWords,
            requestLevel, requestScore, isCurious, hasHighReadingEngagement,
            contentType, callback));
    }

    // ============================================================================
    // WORD COUNT CALCULATION - Level-Based with Score Interpolation
    // ============================================================================

    /// <summary>
    /// Calculate word count based on ENGAGEMENT LEVEL (primary) and smoothed score (secondary).
    /// FIX #3: Uses classifier thresholds via getters instead of hardcoded values.
    /// </summary>
    int CalculateWordCount(EngagementLevel level, float smoothedScore, ContentType contentType)
    {
        // Define word count ranges
        int minWords, lowWords, medWords, highWords, maxWords;

        if (contentType == ContentType.Card)
        {
            minWords = cardMinWords;
            lowWords = cardLowWords;
            medWords = cardMediumWords;
            highWords = cardHighWords;
            maxWords = cardMaxWords;
        }
        else
        {
            minWords = objectMinWords;
            lowWords = objectLowWords;
            medWords = objectMediumWords;
            highWords = objectHighWords;
            maxWords = objectMaxWords;
        }

        // =========================================================================
        // LEVEL-BASED HARD CAPS (primary constraint)
        // =========================================================================
        int levelMin, levelMax;

        switch (level)
        {
            case EngagementLevel.HighlyEngaged:
                levelMin = highWords;
                levelMax = maxWords;
                break;

            case EngagementLevel.Engaged:
                levelMin = medWords;
                levelMax = highWords;
                break;

            case EngagementLevel.Neutral:
                levelMin = lowWords;
                levelMax = medWords;
                break;

            case EngagementLevel.Disengaged:
                levelMin = minWords;
                levelMax = lowWords;
                break;

            case EngagementLevel.HighlyDisengaged:
            default:
                levelMin = minWords;
                levelMax = minWords;
                break;
        }

        // =========================================================================
        // SCORE-BASED INTERPOLATION (secondary, within level range only)
        // =========================================================================
        int words;

        if (!useSmoothedScore || levelMin == levelMax)
        {
            // No interpolation - use level midpoint
            words = (levelMin + levelMax) / 2;
        }
        else
        {
            // FIX #3: Get thresholds from classifier instead of hardcoding
            float scoreWithinLevel = GetScorePositionWithinLevel(level, smoothedScore);
            words = Mathf.RoundToInt(Mathf.Lerp(levelMin, levelMax, scoreWithinLevel));
        }

        if (showDetailedLogs)
        {
            Debug.Log($"[LLMAdaptiveContentManager] Level={level}, Score={smoothedScore:F2} → {words} words (range: {levelMin}-{levelMax})");
        }

        return words;
    }

    /// <summary>
    /// Get normalized position (0-1) of score within its level's expected range.
    /// FIX #3: Uses classifier threshold getters instead of hardcoded values.
    /// </summary>
    float GetScorePositionWithinLevel(EngagementLevel level, float score)
    {
        // Get thresholds from classifier (or use defaults if not available)
        float heThreshold = 0.68f;
        float eThreshold = 0.48f;
        float nThreshold = 0.30f;
        float dThreshold = 0.22f;
        float hdThreshold = 0.18f;

        if (EngagementClassifier.Instance != null)
        {
            heThreshold = EngagementClassifier.Instance.GetHighlyEngagedThreshold();
            eThreshold = EngagementClassifier.Instance.GetEngagedThreshold();
            nThreshold = EngagementClassifier.Instance.GetNeutralThreshold();
            dThreshold = EngagementClassifier.Instance.GetDisengagedThreshold();
            hdThreshold = EngagementClassifier.Instance.GetHighlyDisengagedThreshold();
        }

        float levelMin, levelMax;

        switch (level)
        {
            case EngagementLevel.HighlyEngaged:
                levelMin = heThreshold;
                levelMax = 1.0f;
                break;
            case EngagementLevel.Engaged:
                levelMin = eThreshold;
                levelMax = heThreshold;
                break;
            case EngagementLevel.Neutral:
                levelMin = nThreshold;
                levelMax = eThreshold;
                break;
            case EngagementLevel.Disengaged:
                levelMin = hdThreshold;
                levelMax = nThreshold;
                break;
            case EngagementLevel.HighlyDisengaged:
            default:
                return 0f; // Always minimum
        }

        // Clamp and normalize
        float clamped = Mathf.Clamp(score, levelMin, levelMax);
        float range = levelMax - levelMin;
        if (range <= 0) return 0.5f;
        return (clamped - levelMin) / range;
    }

    /// <summary>
    /// Get the MAXIMUM allowed words for a given engagement level.
    /// Used to enforce hard caps even after bonuses are applied.
    /// </summary>
    int GetMaxWordsForLevel(EngagementLevel level, ContentType contentType)
    {
        int minWords, lowWords, medWords, highWords, maxWords;

        if (contentType == ContentType.Card)
        {
            minWords = cardMinWords;
            lowWords = cardLowWords;
            medWords = cardMediumWords;
            highWords = cardHighWords;
            maxWords = cardMaxWords;
        }
        else
        {
            minWords = objectMinWords;
            lowWords = objectLowWords;
            medWords = objectMediumWords;
            highWords = objectHighWords;
            maxWords = objectMaxWords;
        }

        switch (level)
        {
            case EngagementLevel.HighlyEngaged:
                return maxWords;
            case EngagementLevel.Engaged:
                return highWords;
            case EngagementLevel.Neutral:
                return medWords;
            case EngagementLevel.Disengaged:
                return lowWords;
            case EngagementLevel.HighlyDisengaged:
            default:
                return minWords;
        }
    }

    // ============================================================================
    // LLM CONTENT GENERATION
    // ============================================================================

    System.Collections.IEnumerator GenerateLLMContent(
        string itemId,
        string itemName,
        int wordCount,
        EngagementLevel engagementLevel,
        float smoothedScore,
        bool isCurious,
        bool hasHighReadingEngagement,
        ContentType contentType,
        System.Action<string> callback)
    {
        // Check cache
        string cacheKey = $"{itemId}_{wordCount}_{engagementLevel}";
        if (contentCache.ContainsKey(cacheKey))
        {
            var cached = contentCache[cacheKey];
            if (Time.time - cached.timestamp < cacheExpirySeconds)
            {
                if (showDebugLogs)
                    Debug.Log($"[LLMAdaptiveContentManager] Using cached content");

                callback?.Invoke(cached.content);
                yield break;
            }
        }

        // Build prompt
        string prompt = BuildAdaptivePrompt(itemName, wordCount, engagementLevel, smoothedScore, isCurious, contentType);

        if (showDebugLogs)
        {
            Debug.Log($"[LLMAdaptiveContentManager] Generating {wordCount} words for '{itemName}'...");
        }

        float startTime = Time.time;

        string requestBody = BuildRequestJSON(prompt, wordCount);

        using (UnityWebRequest request = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(requestBody);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");

            request.timeout = (int)apiTimeout;

            yield return request.SendWebRequest();
            float responseTime = Time.time - startTime;

            if (request.result == UnityWebRequest.Result.Success)
            {
                string responseText = request.downloadHandler.text;
                string generatedContent = ParseOpenAIResponse(responseText);

                if (!string.IsNullOrEmpty(generatedContent))
                {
                    // Cache the content
                    contentCache[cacheKey] = new CachedContent
                    {
                        content = generatedContent,
                        timestamp = Time.time
                    };

                    if (showDebugLogs)
                    {
                        int actualWords = generatedContent.Split(' ').Length;
                        Debug.Log($"✅ [LLMAdaptiveContentManager] Generated {actualWords} words in {responseTime:F2}s");
                    }

                    // Log adaptation to Firebase
                    LogAdaptationToFirebase(
                        itemId, itemName, prompt, generatedContent,
                        wordCount, engagementLevel, smoothedScore,
                        isCurious, hasHighReadingEngagement,
                        contentType, true, responseTime);

                    callback?.Invoke(generatedContent);
                }
                else
                {
                    Debug.LogError("[LLMAdaptiveContentManager] Failed to parse response");
                    LogAdaptationToFirebase(
                        itemId, itemName, prompt, "",
                        wordCount, engagementLevel, smoothedScore,
                        isCurious, hasHighReadingEngagement,
                        contentType, false, responseTime);
                    callback?.Invoke(fallbackContent);
                }
            }
            else
            {
                Debug.LogError($"[LLMAdaptiveContentManager] API Error: {request.error}");

                LogAdaptationToFirebase(
                    itemId, itemName, prompt, "",
                    wordCount, engagementLevel, smoothedScore,
                    isCurious, hasHighReadingEngagement,
                    contentType, false, responseTime);

                callback?.Invoke(fallbackContent);
            }
        }
    }

    /// <summary>
    /// Build prompt that adapts style based on ENGAGEMENT LEVEL.
    /// </summary>
    string BuildAdaptivePrompt(string itemName, int wordCount, EngagementLevel level, float score, bool isCurious, ContentType contentType)
    {
        string basePrompt = $"Explain '{itemName}', an Ottoman cultural artifact. ";
        basePrompt += $"IMPORTANT: Use EXACTLY {wordCount} words or fewer. Do NOT exceed {wordCount} words.";

        switch (level)
        {
            case EngagementLevel.HighlyEngaged:
                if (contentType == ContentType.Card)
                {
                    basePrompt += " Provide comprehensive historical context, cultural significance, craftsmanship details, and usage information.";
                    basePrompt += " Write in an engaging, scholarly style with rich detail, specific examples, and interesting anecdotes.";
                    basePrompt += " Include connections to Ottoman daily life and cultural practices.";
                }
                else
                {
                    basePrompt += " Provide interesting historical context and cultural significance.";
                    basePrompt += " Write in an engaging, informative style with vivid details.";
                }
                break;

            case EngagementLevel.Engaged:
                if (contentType == ContentType.Card)
                {
                    basePrompt += " Provide key historical facts, cultural context, and interesting details.";
                    basePrompt += " Write in an accessible, informative style that balances depth with clarity.";
                }
                else
                {
                    basePrompt += " Provide key facts and cultural context.";
                    basePrompt += " Write clearly and informatively.";
                }
                break;

            case EngagementLevel.Neutral:
                basePrompt += " Provide essential historical facts and cultural context.";
                basePrompt += " Write clearly and concisely, balancing detail with accessibility.";
                break;

            case EngagementLevel.Disengaged:
                if (contentType == ContentType.Card)
                {
                    basePrompt += $" BE VERY BRIEF - maximum {wordCount} words total.";
                    basePrompt += " Format as 2-3 SHORT bullet points only.";
                    basePrompt += " Each bullet: one simple fact, under 10 words.";
                }
                else
                {
                    basePrompt += $" BE VERY BRIEF - maximum {wordCount} words.";
                    basePrompt += " Write 1-2 short sentences with only the most basic information.";
                }
                break;

            case EngagementLevel.HighlyDisengaged:
            default:
                basePrompt += $" EXTREMELY BRIEF - maximum {wordCount} words.";
                basePrompt += " Write ONE sentence with the single most interesting fact.";
                basePrompt += " Use the simplest possible language. No elaboration.";
                break;
        }

        if (isCurious && level >= EngagementLevel.Neutral)
        {
            basePrompt += " Include one fascinating detail if space permits.";
        }

        return basePrompt;
    }

    string BuildRequestJSON(string prompt, int wordCount)
    {
        string escapedPrompt = prompt.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

        string systemMessage = "You are a knowledgeable museum guide explaining Ottoman cultural artifacts. " +
            "CRITICAL: You MUST stay within the requested word count. Do not exceed it. " +
            "If asked for 30 words, write exactly 25-30 words. " +
            "Be accurate and engaging while respecting length constraints.";

        int maxTokens = Mathf.Max(50, Mathf.RoundToInt(wordCount * 1.5f));

        return "{\"model\":\"" + modelName + "\"," +
               "\"messages\":[" +
               "{\"role\":\"system\",\"content\":\"" + systemMessage + "\"}," +
               "{\"role\":\"user\",\"content\":\"" + escapedPrompt + "\"}" +
               "]," +
               "\"max_tokens\":" + maxTokens + "," +
               "\"temperature\":0.7}";
    }

    string ParseOpenAIResponse(string jsonResponse)
    {
        try
        {
            int contentIndex = jsonResponse.IndexOf("\"content\":");
            if (contentIndex == -1) return "";

            int contentStart = jsonResponse.IndexOf("\"", contentIndex + 10) + 1;
            if (contentStart == 0) return "";

            int contentEnd = contentStart;
            while (contentEnd < jsonResponse.Length)
            {
                if (jsonResponse[contentEnd] == '"' && jsonResponse[contentEnd - 1] != '\\')
                    break;
                contentEnd++;
            }

            if (contentEnd >= jsonResponse.Length) return "";

            string content = jsonResponse.Substring(contentStart, contentEnd - contentStart);
            content = content.Replace("\\n", "\n");
            content = content.Replace("\\\"", "\"");
            content = content.Replace("\\\\", "\\");

            return content.Trim();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[LLMAdaptiveContentManager] Parse error: {e.Message}");
            return "";
        }
    }

    // ============================================================================
    // FIREBASE LOGGING
    // ============================================================================

    async void LogAdaptationToFirebase(
        string itemId,
        string itemName,
        string prompt,
        string response,
        int targetWordCount,
        EngagementLevel engagementLevel,
        float smoothedScore,
        bool isCurious,
        bool hasHighReadingEngagement,
        ContentType contentType,
        bool success,
        float responseTime)
    {
        if (FirebaseManager.Instance == null || !FirebaseManager.Instance.IsReady)
            return;

        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
            return;

        try
        {
            float readingEngagementScore = 0.5f;
            int readingEventsCount = 0;
            if (ReadingBehaviorTracker.Instance != null)
            {
                var summary = ReadingBehaviorTracker.Instance.GetSummary();
                readingEngagementScore = summary.currentSmoothedEngagement;
                readingEventsCount = summary.totalEventsReceived;
            }

            var data = new Dictionary<string, object>
            {
                { "itemId", itemId },
                { "itemName", itemName },
                { "contentType", contentType.ToString() },
                { "targetWordCount", targetWordCount },
                { "actualWordCount", response.Split(' ').Length },
                { "engagementLevel", engagementLevel.ToString() },
                { "engagementScore", (int)engagementLevel },
                { "smoothedScore", smoothedScore },
                { "readingEngagementScore", readingEngagementScore },
                { "readingEventsCount", readingEventsCount },
                { "hasHighReadingEngagement", hasHighReadingEngagement },
                { "isCurious", isCurious },
                { "prompt", prompt },
                { "response", response },
                { "success", success },
                { "responseTime", responseTime },
                { "model", modelName },
                { "condition", ExperimentConditionManager.Instance?.condition.ToString() ?? "Unknown" },
                { "useSmoothedScore", useSmoothedScore },
                { "engagementWindowEnabled", enableEngagementWindow },
                { "windowSampleCount", engagementWindow.Count },
                { "timestamp", Firebase.Firestore.FieldValue.ServerTimestamp },
                { "sessionTime", Time.time }
            };

            string userId = PlayerManager.Instance.userId;
            string docId = $"adaptation_{itemId}_{System.DateTime.UtcNow.Ticks}";

            await FirebaseManager.Instance.Firestore
                .Collection("users").Document(userId)
                .Collection("contentAdaptation").Document(docId)
                .SetAsync(data);

            if (showDetailedLogs)
            {
                Debug.Log($"📊 [LLMAdaptiveContentManager] Adaptation logged to Firebase");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[LLMAdaptiveContentManager] Failed to log adaptation: {e.Message}");
        }
    }

    // ============================================================================
    // UTILITY
    // ============================================================================

    public void ClearCache()
    {
        contentCache.Clear();
        Debug.Log("[LLMAdaptiveContentManager] Cache cleared");
    }

    public AdaptationState GetCurrentAdaptationState()
    {
        float score = 0.5f;
        EngagementLevel level = EngagementLevel.Neutral;

        if (EngagementClassifier.Instance != null)
        {
            score = EngagementClassifier.Instance.GetSmoothedEngagementScore();
            level = EngagementClassifier.Instance.GetEngagementLevel();
        }

        return new AdaptationState
        {
            engagementLevel = level,
            smoothedScore = score,
            cardWordCount = CalculateWordCount(level, score, ContentType.Card),
            objectWordCount = CalculateWordCount(level, score, ContentType.Object),
            cardMaxAllowed = GetMaxWordsForLevel(level, ContentType.Card),
            objectMaxAllowed = GetMaxWordsForLevel(level, ContentType.Object)
        };
    }

    /// <summary>
    /// Get engagement window status for debugging
    /// </summary>
    public int GetWindowSampleCount() => engagementWindow.Count;
}

// ============================================================================
// DATA STRUCTURES
// ============================================================================

public enum ContentType
{
    Card,
    Object
}

public struct AdaptationState
{
    public EngagementLevel engagementLevel;
    public float smoothedScore;
    public int cardWordCount;
    public int objectWordCount;
    public int cardMaxAllowed;
    public int objectMaxAllowed;
}

class CachedContent
{
    public string content;
    public float timestamp;
}