using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// Hidden Card - Collectible cards in the museum
/// FIXED: Now uses user-specific PlayerPrefs keys to prevent cross-user contamination
/// </summary>
public class HiddenCard : MonoBehaviour
{
    [Header("Card Identity")]
    [Tooltip("Unique ID for this card (e.g., 'balcony_card_01')")]
    public string cardID = "balcony_card_01";

    [Tooltip("Which room is this card in? (e.g., 'balcony', 'living_room')")]
    public string roomID = "balcony";

    [Header("Localized Text")]
    [Tooltip("Localized title for this card")]
    public LocalizedString cardTitle;

    [Tooltip("Localized description/story for this card")]
    public LocalizedString cardDescription;

    [Header("Discovery Settings")]
    [Tooltip("How close must the player be to notice this card? (in meters)")]
    public float detectionRadius = 3f;

    [Tooltip("Must the player look directly at the card to collect it?")]
    public bool requiresDirectLook = true;

    [Tooltip("Interaction key (for non-VR testing)")]
    public KeyCode interactionKey = KeyCode.E;

    [Tooltip("Auto-collect after looking for this many seconds (0 = disabled)")]
    [Range(0f, 3f)]
    public float autoCollectDelay = 0f;

    [Header("Visual Feedback")]
    [Tooltip("Particle effect or glow that shows when player is near")]
    public GameObject hintEffect;

    [Tooltip("Color when idle")]
    public Color idleColor = new Color(1f, 0.6f, 0.2f);

    [Tooltip("Color when player can collect")]
    public Color activeColor = new Color(1f, 0.8f, 0f);

    [Header("Audio")]
    [Tooltip("Sound when card is discovered (optional)")]
    public AudioClip discoverySound;

    [Header("Quest 3 VR")]
    [Tooltip("Use controller-based highlighting instead of head-based (for Quest 3)")]
    public bool useControllerHighlighting = true;

    // Internal variables
    private bool isDiscovered = false;
    private Transform playerTransform;
    private Camera playerCamera;
    private Renderer cardRenderer;
    private AudioSource audioSource;
    private Material cardMaterial;
    private float lookTimer = 0f;
    private bool hasCheckedDiscovery = false; // ✅ NEW: Track if we've checked

    void Start()
    {
        // Find the player
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
        {
            playerTransform = player.transform;
            playerCamera = Camera.main;

            if (playerCamera == null)
            {
                playerCamera = player.GetComponentInChildren<Camera>();
            }

            if (playerCamera == null)
            {
                Debug.LogError("HiddenCard: No camera found! Card interaction won't work.");
            }
        }
        else
        {
            Debug.LogError("HiddenCard: No GameObject with 'Player' tag found! Please tag your player.");
        }

        // Get the renderer to change colors
        cardRenderer = GetComponent<Renderer>();
        if (cardRenderer != null)
        {
            cardMaterial = new Material(cardRenderer.material);
            cardRenderer.material = cardMaterial;
            cardMaterial.color = idleColor;

            cardMaterial.EnableKeyword("_EMISSION");
            cardMaterial.SetColor("_EmissionColor", idleColor * 0.5f);
        }

        // Setup audio
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null && discoverySound != null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
        }

        // Hide hint effect initially
        if (hintEffect != null)
        {
            hintEffect.SetActive(false);
        }

        // ✅ FIXED: Check if already discovered (with user-specific key)
        CheckIfAlreadyDiscovered();

        StartCoroutine(ForceColliderRefresh());
    }

    System.Collections.IEnumerator ForceColliderRefresh()
    {
        yield return new WaitForSeconds(0.1f);

        Collider col = GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = false;
            yield return null;
            col.enabled = true;
            Debug.Log($"[{cardID}] Collider refreshed!");
        }
    }

    void Update()
    {
        // ✅ NEW: Retry discovery check if userId wasn't ready at Start
        if (!hasCheckedDiscovery && PlayerManager.Instance != null && !string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            CheckIfAlreadyDiscovered();
        }

        // If using controller highlighting, MinimalInteractionManager handles it
        if (useControllerHighlighting)
        {
            // Just show hint effect when player is near
            if (playerTransform != null && hintEffect != null && !isDiscovered)
            {
                float dist = Vector3.Distance(transform.position, playerTransform.position);
                hintEffect.SetActive(dist <= detectionRadius);
            }
            return;
        }

        // OLD SYSTEM: Head-based highlighting (backward compatibility)
        if (isDiscovered || playerTransform == null)
            return;

        float distanceToPlayer = Vector3.Distance(transform.position, playerTransform.position);

        if (hintEffect != null)
        {
            hintEffect.SetActive(distanceToPlayer <= detectionRadius);
        }

        if (requiresDirectLook)
        {
            if (IsPlayerLookingAtCard())
            {
                if (cardMaterial != null)
                {
                    cardMaterial.color = activeColor;
                    cardMaterial.SetColor("_EmissionColor", activeColor);
                }

                ShowInteractionPrompt(true);

                if (autoCollectDelay > 0)
                {
                    lookTimer += Time.deltaTime;
                    if (lookTimer >= autoCollectDelay)
                    {
                        CollectCard();
                        lookTimer = 0f;
                    }
                }
            }
            else
            {
                lookTimer = 0f;
                if (cardMaterial != null)
                {
                    cardMaterial.color = idleColor;
                    cardMaterial.SetColor("_EmissionColor", idleColor * 0.5f);
                }
                ShowInteractionPrompt(false);
            }
        }
        else
        {
            if (distanceToPlayer <= detectionRadius * 0.5f)
            {
                CollectCard();
            }
        }
    }

    bool IsPlayerLookingAtCard()
    {
        if (playerCamera == null)
            return false;

        Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, detectionRadius + 2f))
        {
            if (hit.collider.gameObject == gameObject)
            {
                return true;
            }
        }

        return false;
    }

    public void TriggerCollection()
    {
        if (!isDiscovered)
        {
            CollectCard();
        }
    }

    public bool IsDiscovered()
    {
        return isDiscovered;
    }

    /// <summary>
    /// Called by MinimalInteractionManager when controller points at card
    /// </summary>
    public void SetHighlight(bool highlight)
    {
        if (isDiscovered || cardMaterial == null) return;

        if (highlight)
        {
            cardMaterial.color = activeColor;
            cardMaterial.SetColor("_EmissionColor", activeColor);
        }
        else
        {
            cardMaterial.color = idleColor;
            cardMaterial.SetColor("_EmissionColor", idleColor * 0.5f);
        }
    }

    // ============================================================================
    // ✅ FIXED: User-specific PlayerPrefs key generation
    // ============================================================================

    /// <summary>
    /// Get user-specific key for this card
    /// Format: Card_{userId}_{cardID}_Found
    /// </summary>
    private string GetUserCardKey()
    {
        string userId = "";
        if (PlayerManager.Instance != null && !string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            userId = PlayerManager.Instance.userId;
        }

        // If no user logged in, use empty prefix (shouldn't happen in normal flow)
        if (string.IsNullOrEmpty(userId))
        {
            Debug.LogWarning($"[HiddenCard] No userId available for card {cardID} - using legacy key");
            return $"Card_{cardID}_Found"; // Legacy fallback
        }

        return $"Card_{userId}_{cardID}_Found";
    }

    void CollectCard()
    {
        isDiscovered = true;

        // Get localized title
        var titleOperation = cardTitle.GetLocalizedStringAsync();

        titleOperation.Completed += (op) =>
        {
            string localizedTitle = op.Result;
            Debug.Log($"Card Discovered: {localizedTitle}");

            // Use LLM to generate adaptive content
            if (LLMAdaptiveContentManager.Instance != null)
            {
                // Show loading UI - ✅ NOW PASSES cardID for tracking
                if (CardDiscoveryUI.Instance != null)
                {
                    CardDiscoveryUI.Instance.ShowCardDiscovery(cardID, localizedTitle, "Generating personalized content...");
                }

                // Get LLM-generated content with CARD type
                // Content length is now determined by closed-loop engagement (including reading behavior)
                LLMAdaptiveContentManager.Instance.GetAdaptiveContent(
                    cardID,
                    localizedTitle,
                    ContentType.Card,
                    (generatedContent) =>
                    {
                        // Content generated! Update UI - ✅ PASSES cardID
                        if (CardDiscoveryUI.Instance != null)
                        {
                            CardDiscoveryUI.Instance.ShowCardDiscovery(cardID, localizedTitle, generatedContent);
                        }

                        Debug.Log($"[HiddenCard] Displayed adaptive content for {cardID} (length based on engagement)");
                    }
                );
            }
            else
            {
                // Fallback to localized content if LLM not available
                var descOperation = cardDescription.GetLocalizedStringAsync();
                descOperation.Completed += (descOp) =>
                {
                    string localizedDesc = descOp.Result;

                    if (CardDiscoveryUI.Instance != null)
                    {
                        // ✅ PASSES cardID for tracking even in fallback
                        CardDiscoveryUI.Instance.ShowCardDiscovery(cardID, localizedTitle, localizedDesc);
                    }
                };
            }
        };

        // Register card collection with BadgeManager
        if (BadgeManager.instance != null)
        {
            BadgeManager.instance.OnCardCollected(cardID, roomID);
        }

        // Play collection sound
        if (audioSource != null && discoverySound != null)
        {
            audioSource.PlayOneShot(discoverySound);
        }

        // Hide the card visually
        if (cardRenderer != null)
        {
            cardRenderer.enabled = false;
        }

        if (hintEffect != null)
        {
            hintEffect.SetActive(false);
        }

        // ✅ FIXED: Save to PlayerPrefs with USER-SPECIFIC key
        string userCardKey = GetUserCardKey();
        PlayerPrefs.SetInt(userCardKey, 1);
        PlayerPrefs.Save();

        Debug.Log($"✅ [HiddenCard] Saved card discovery: {userCardKey}");

        ShowInteractionPrompt(false);
    }

    void CheckIfAlreadyDiscovered()
    {
        // ✅ FIXED: Need userId to check user-specific key
        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            // User not logged in yet - will retry in Update()
            Debug.Log($"[HiddenCard] {cardID}: Waiting for user login to check discovery status...");
            return;
        }

        hasCheckedDiscovery = true; // Mark that we've done the check

        string userCardKey = GetUserCardKey();

        if (PlayerPrefs.GetInt(userCardKey, 0) == 1)
        {
            isDiscovered = true;

            if (cardRenderer != null)
            {
                cardRenderer.enabled = false;
            }

            Debug.Log($"[HiddenCard] Card {cardID} was already discovered by user {PlayerManager.Instance.userId}");
        }
        else
        {
            Debug.Log($"[HiddenCard] Card {cardID} is available for user {PlayerManager.Instance.userId}");
        }
    }

    void ShowInteractionPrompt(bool show)
    {
        // Placeholder for UI prompt
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, detectionRadius * 0.5f);
    }
}