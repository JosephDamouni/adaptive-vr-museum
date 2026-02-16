using UnityEngine;
using TMPro;
using System;
using System.Collections;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// MEASUREMENT STAGE (UI Component): Card Discovery Panel
/// 
/// Responsibilities:
/// 1. Display card information to user
/// 2. Measure viewing duration accurately (unscaled time)
/// 3. Report reading events to ReadingBehaviorTracker for closed-loop adaptation
/// 4. Fire OnCardClosed event for other listeners
/// 
/// Flow:
/// Panel Opens → User views card → Panel Closes → Report to ReadingBehaviorTracker → 
/// EngagementClassifier updates → Next card adapts content length
/// 
/// Note: Cards typically have longer, more detailed content than objects,
/// so reading time thresholds and weights are adjusted accordingly.
/// </summary>
public class CardDiscoveryUI : MonoBehaviour
{
    public static CardDiscoveryUI Instance { get; private set; }

    [Header("UI References")]
    public GameObject discoveryPanel;
    public TextMeshProUGUI cardTitleText;
    public TextMeshProUGUI cardDescriptionText;
    public UnityEngine.UI.Button continueButton;

    [Header("Animation Settings")]
    public float fadeInDuration = 0.5f;
    public float fadeOutDuration = 0.3f;

    [Header("Background Dimming")]
    public UnityEngine.UI.Image backgroundDim;
    public Color dimColor = new Color(0, 0, 0, 0.7f);

    [Header("Reading Time Analysis")]
    [Tooltip("Average words per minute for card reading (slower than objects due to detail)")]
    public float averageWordsPerMinute = 180f;

    [Tooltip("Minimum time (seconds) to consider card was actually read")]
    public float minimumReadTime = 3f;

    [Header("Debug")]
    public bool showDebugLogs = true;

    // Events for tracking
    public event Action<string, float, bool> OnCardClosed; // (cardId, duration, wasLikelyRead)

    private CanvasGroup panelCanvasGroup;
    private bool isShowing = false;

    // Reading time tracking
    private float showStartTime = 0f;
    private string currentCardId = "";
    private string currentCardTitle = "";
    private string currentContent = "";
    private int currentWordCount = 0;
    private float estimatedReadTime = 0f;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        if (discoveryPanel != null)
        {
            panelCanvasGroup = discoveryPanel.GetComponent<CanvasGroup>();
            if (panelCanvasGroup == null)
            {
                panelCanvasGroup = discoveryPanel.AddComponent<CanvasGroup>();
            }
        }

        if (continueButton != null)
        {
            continueButton.onClick.AddListener(CloseDiscovery);
        }
    }

    void Start()
    {
        if (discoveryPanel != null)
        {
            discoveryPanel.SetActive(false);
        }

        if (backgroundDim != null)
        {
            backgroundDim.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Show card discovery with title and description (legacy method)
    /// </summary>
    public void ShowCardDiscovery(string title, string description)
    {
        ShowCardDiscovery("", title, description);
    }

    /// <summary>
    /// Show card discovery with explicit card ID for tracking
    /// </summary>
    public void ShowCardDiscovery(string cardId, string title, string description)
    {
        // If already showing, update the text
        if (isShowing)
        {
            // Report previous card viewing before updating
            if (!string.IsNullOrEmpty(currentCardTitle))
            {
                float previousDuration = Time.unscaledTime - showStartTime;
                bool wasLikelyRead = DetermineIfLikelyRead(previousDuration);
                ReportReadingEvent(previousDuration, wasLikelyRead);

                if (showDebugLogs)
                {
                    Debug.Log($"📜 [CardDiscoveryUI] Content updated (previous: {previousDuration:F1}s, likely read: {wasLikelyRead})");
                }
            }

            // Update for new content
            currentCardId = cardId;
            currentCardTitle = title;
            currentContent = description;
            currentWordCount = CountWords(description);
            estimatedReadTime = CalculateEstimatedReadTime(currentWordCount);
            showStartTime = Time.unscaledTime;

            if (cardTitleText != null)
                cardTitleText.text = title;
            if (cardDescriptionText != null)
                cardDescriptionText.text = description;
            return;
        }

        // Store for tracking
        currentCardId = cardId;
        currentCardTitle = title;
        currentContent = description;
        currentWordCount = CountWords(description);
        estimatedReadTime = CalculateEstimatedReadTime(currentWordCount);

        StartCoroutine(ShowDiscoveryRoutine(title, description));
    }

    IEnumerator ShowDiscoveryRoutine(string title, string description)
    {
        isShowing = true;
        showStartTime = Time.unscaledTime; // Use unscaled time for accuracy

        if (cardTitleText != null)
            cardTitleText.text = title;

        if (cardDescriptionText != null)
            cardDescriptionText.text = description;

        // Show background dim
        if (backgroundDim != null)
        {
            backgroundDim.gameObject.SetActive(true);
            backgroundDim.color = new Color(dimColor.r, dimColor.g, dimColor.b, 0);
        }

        discoveryPanel.SetActive(true);

        if (panelCanvasGroup != null)
        {
            panelCanvasGroup.alpha = 0f;
        }

        // Fade in
        yield return StartCoroutine(FadePanel(0f, 1f, fadeInDuration));

        // Also fade in background
        if (backgroundDim != null)
        {
            yield return StartCoroutine(FadeBackground(0f, dimColor.a, fadeInDuration * 0.5f));
        }

        if (showDebugLogs)
        {
            Debug.Log($"📜 [CardDiscoveryUI] Panel opened: '{title}' ({currentWordCount} words, est. {estimatedReadTime:F1}s read time)");
        }
    }

    public void CloseDiscovery()
    {
        if (!isShowing) return;

        StartCoroutine(CloseDiscoveryRoutine());
    }

    IEnumerator CloseDiscoveryRoutine()
    {
        // Calculate reading metrics before closing
        float viewDuration = Time.unscaledTime - showStartTime;
        bool wasLikelyRead = DetermineIfLikelyRead(viewDuration);

        if (showDebugLogs)
        {
            string readStatus = wasLikelyRead ? "✅ LIKELY READ" : "❌ SKIPPED/SKIMMED";
            Debug.Log($"📜 [CardDiscoveryUI] Panel closing: '{currentCardTitle}'");
            Debug.Log($"   Duration: {viewDuration:F1}s (estimated: {estimatedReadTime:F1}s)");
            Debug.Log($"   Word count: {currentWordCount}");
            Debug.Log($"   Status: {readStatus}");
        }

        // ✅ CRITICAL: Report to ReadingBehaviorTracker for closed-loop adaptation
        ReportReadingEvent(viewDuration, wasLikelyRead);

        // Fire event for external tracking
        OnCardClosed?.Invoke(currentCardId, viewDuration, wasLikelyRead);

        // Fade out panel
        yield return StartCoroutine(FadePanel(1f, 0f, fadeOutDuration));

        // Fade out background
        if (backgroundDim != null)
        {
            yield return StartCoroutine(FadeBackground(dimColor.a, 0f, fadeOutDuration * 0.5f));
            backgroundDim.gameObject.SetActive(false);
        }

        discoveryPanel.SetActive(false);
        isShowing = false;

        // Reset tracking variables
        currentCardId = "";
        currentCardTitle = "";
        currentContent = "";
        currentWordCount = 0;
        estimatedReadTime = 0f;
    }

    IEnumerator FadePanel(float startAlpha, float endAlpha, float duration)
    {
        if (panelCanvasGroup == null)
            yield break;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = elapsed / duration;
            t = Mathf.SmoothStep(0f, 1f, t);
            panelCanvasGroup.alpha = Mathf.Lerp(startAlpha, endAlpha, t);
            yield return null;
        }

        panelCanvasGroup.alpha = endAlpha;
    }

    IEnumerator FadeBackground(float startAlpha, float endAlpha, float duration)
    {
        if (backgroundDim == null)
            yield break;

        float elapsed = 0f;
        Color startColor = new Color(dimColor.r, dimColor.g, dimColor.b, startAlpha);
        Color endColor = new Color(dimColor.r, dimColor.g, dimColor.b, endAlpha);

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = elapsed / duration;
            backgroundDim.color = Color.Lerp(startColor, endColor, t);
            yield return null;
        }

        backgroundDim.color = endColor;
    }

    void Update()
    {
        if (isShowing && Time.unscaledTime > showStartTime + 0.5f)
        {
            if (Keyboard.current != null &&
                (Keyboard.current[Key.E].wasPressedThisFrame ||
                 Keyboard.current[Key.Space].wasPressedThisFrame))
            {
                CloseDiscovery();
            }
        }
    }

    // ============================================================================
    // READING TIME ANALYSIS
    // ============================================================================

    int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split(new char[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries).Length;
    }

    float CalculateEstimatedReadTime(int wordCount)
    {
        if (wordCount <= 0)
            return minimumReadTime;

        float estimatedSeconds = (wordCount / averageWordsPerMinute) * 60f;

        // Cards have more detailed content, add extra buffer for comprehension
        estimatedSeconds *= 1.3f;

        return Mathf.Max(estimatedSeconds, minimumReadTime);
    }

    bool DetermineIfLikelyRead(float viewDuration)
    {
        if (viewDuration < minimumReadTime)
            return false;

        if (viewDuration >= estimatedReadTime * 0.5f)
            return true;

        // Content-length specific thresholds (cards have longer content)
        if (currentWordCount <= 50 && viewDuration >= 4f)
            return true;

        if (currentWordCount <= 100 && viewDuration >= 8f)
            return true;

        if (currentWordCount <= 200 && viewDuration >= 15f)
            return true;

        return false;
    }

    // ============================================================================
    // CLOSED-LOOP INTEGRATION
    // ============================================================================

    /// <summary>
    /// Report reading event to ReadingBehaviorTracker for engagement adaptation
    /// Cards are weighted higher than objects in the engagement calculation
    /// </summary>
    void ReportReadingEvent(float viewDuration, bool wasLikelyRead)
    {
        if (ReadingBehaviorTracker.Instance == null)
        {
            if (showDebugLogs)
            {
                Debug.LogWarning("[CardDiscoveryUI] ReadingBehaviorTracker not found - reading event not reported for adaptation");
            }
            return;
        }

        // Create reading event data
        ReadingEventData eventData = new ReadingEventData
        {
            contentName = currentCardTitle,
            contentType = "Card", // Cards are weighted differently than objects
            viewDuration = viewDuration,
            estimatedReadTime = estimatedReadTime,
            wordCount = currentWordCount,
            wasLikelyRead = wasLikelyRead
        };

        // Report to tracker (this feeds into EngagementClassifier)
        ReadingBehaviorTracker.Instance.OnContentViewed(eventData);

        if (showDebugLogs)
        {
            Debug.Log($"🔄 [CardDiscoveryUI] Reading event reported to tracker → affects next content adaptation");
        }
    }

    // ============================================================================
    // PUBLIC API
    // ============================================================================

    public bool IsShowing => isShowing;

    public float GetCurrentViewDuration()
    {
        if (!isShowing)
            return 0f;
        return Time.unscaledTime - showStartTime;
    }

    public string GetCurrentCardId()
    {
        return currentCardId;
    }
}