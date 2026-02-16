using UnityEngine;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;

public class BadgeNotificationUI : MonoBehaviour
{
    public static BadgeNotificationUI instance;

    [Header("UI References")]
    public GameObject notificationPanel;
    public UnityEngine.UI.Image badgeIcon;
    public TextMeshProUGUI badgeNameText;
    public TextMeshProUGUI badgeDescriptionText;

    // -------------------------------------------------------
    // BADGE ICON SYSTEM
    // -------------------------------------------------------
    [System.Serializable]
    public class BadgeIconEntry
    {
        public string badgeId;   
        public Sprite icon;      
    }

    [Header("Badge Icons (assign in Inspector)")]
    public List<BadgeIconEntry> badgeIcons;

    [Header("Animation Settings")]
    public float slideInDuration = 0.5f;
    public float displayDuration = 3.5f;
    public float slideOutDuration = 0.5f;

    [Header("Positions")]
    public float hiddenYPosition = -100f;
    public float visibleYPosition = -100f;

    private RectTransform panelRect;
    private bool isShowing = false;

    
    private Queue<BadgeData> badgeQueue = new Queue<BadgeData>();

    
    private class BadgeData
    {
        public string badgeId;
        public string name;
        public string description;

        public BadgeData(string badgeId, string name, string description)
        {
            this.badgeId = badgeId;
            this.name = name;
            this.description = description;
        }
    }

    void Awake()
    {
        // Singleton
        if (instance == null)
        {
            instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        if (notificationPanel != null)
        {
            panelRect = notificationPanel.GetComponent<RectTransform>();
        }
    }

    void Start()
    {
        // Make sure panel is hidden
        if (notificationPanel != null)
        {
            notificationPanel.SetActive(false);
        }
    }

    // -------------------------------------------------------
    // BADGE ICON HELPER
    // -------------------------------------------------------
    private Sprite GetBadgeIcon(string badgeId)
    {
        foreach (var entry in badgeIcons)
        {
            if (entry.badgeId == badgeId)
                return entry.icon;
        }
        return null; 
    }

    /// <summary>
    /// Show a badge notification (now queues if one is already showing)
    /// Updated to accept badgeId for icon lookup
    /// </summary>
    public void ShowBadge(string badgeId, string badgeName, string description)
    {
       
        badgeQueue.Enqueue(new BadgeData(badgeId, badgeName, description));

        
        if (!isShowing)
        {
            StartCoroutine(ProcessBadgeQueue());
        }
    }

    /// <summary>
    /// ✅ NEW: Process badges one at a time from queue
    /// </summary>
    IEnumerator ProcessBadgeQueue()
    {
        while (badgeQueue.Count > 0)
        {
            BadgeData badge = badgeQueue.Dequeue();
            yield return StartCoroutine(ShowBadgeRoutine(badge.badgeId, badge.name, badge.description));

            // Small delay between badges
            yield return new WaitForSeconds(0.3f);
        }
    }

    IEnumerator ShowBadgeRoutine(string badgeId, string badgeName, string description)
    {
        isShowing = true;

        
        Sprite iconSprite = GetBadgeIcon(badgeId);

        // Set icon image
        if (badgeIcon != null)
        {
            if (iconSprite != null)
            {
                badgeIcon.sprite = iconSprite;
                badgeIcon.enabled = true;
            }
            else
            {
                // Fallback: keep current sprite or disable
                Debug.LogWarning($"No icon found for badge: {badgeId}");
                badgeIcon.enabled = false;
            }
        }

        // Set header text (top small text)
        if (badgeNameText != null)
            badgeNameText.text = "ACHIEVEMENT UNLOCKED";

        // Set description
        if (badgeDescriptionText != null)
            badgeDescriptionText.text = $"{badgeName}\n\n{description}";

        // Activate panel
        notificationPanel.SetActive(true);

        // Start position (hidden above screen)
        if (panelRect != null)
        {
            panelRect.anchoredPosition = new Vector2(0, hiddenYPosition);
        }

        // Slide in
        yield return StartCoroutine(SlideToPosition(visibleYPosition, slideInDuration));

        // Wait (display time)
        yield return new WaitForSeconds(displayDuration);

        // Slide out
        yield return StartCoroutine(SlideToPosition(hiddenYPosition, slideOutDuration));

        // Hide panel
        notificationPanel.SetActive(false);

        isShowing = false;
    }

    IEnumerator SlideToPosition(float targetY, float duration)
    {
        if (panelRect == null)
            yield break;

        Vector2 startPos = panelRect.anchoredPosition;
        Vector2 targetPos = new Vector2(0, targetY);

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            // Smooth easing
            t = Mathf.SmoothStep(0f, 1f, t);

            panelRect.anchoredPosition = Vector2.Lerp(startPos, targetPos, t);

            yield return null;
        }

        panelRect.anchoredPosition = targetPos;
    }
}