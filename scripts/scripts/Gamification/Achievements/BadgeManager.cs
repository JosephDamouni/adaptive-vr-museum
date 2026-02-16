using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using System.Collections.Generic;

public class BadgeManager : MonoBehaviour
{
    // Singleton pattern - only one BadgeManager exists
    public static BadgeManager instance;

    [Header("Badge Tracking")]
    private int totalCardsCollected = 0;
    private HashSet<string> unlockedBadges = new HashSet<string>();

    // Track which cards have been found in each room
    private Dictionary<string, HashSet<string>> cardsByRoom = new Dictionary<string, HashSet<string>>();

    // Define how many cards each room should have
    private Dictionary<string, int> expectedCardsPerRoom = new Dictionary<string, int>()
    {
        { "balcony", 3 },
        { "bedroom", 3 },
        { "guest_room", 3 },
        { "kitchen", 3 },
        { "workshop", 3 },
        { "archive", 3 }
    };

    [Header("Localization")]
    public string badgeTableName = "FullMuseum"; // Name of your String Table for badges

    [Header("Debug")]
    public bool showDebugMessages = true;

    [Header("Firebase Integration")]
    public bool useFirebase = true; // Toggle for testing without Firebase

    void Awake()
    {
        // Singleton setup
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject); // Persists between scenes
        }
        else
        {
            Destroy(gameObject);
            return;
        }
    }

    async void Start()
    {
        // Load progress from Firebase when game starts
        if (useFirebase && PlayerManager.Instance != null && !string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            await LoadProgressFromFirebase();
        }
        else
        {
            // Fallback to local PlayerPrefs
            LoadBadgeProgress();
        }

        if (showDebugMessages)
        {
            Debug.Log($" BadgeManager initialized. Total cards collected: {totalCardsCollected}");
            Debug.Log($" Unlocked badges: {unlockedBadges.Count}");
        }
    }

    // ============================================================================
    // LOCALIZATION HELPERS
    // ============================================================================

    /// <summary>
    /// Get localized string from the Badges table
    /// </summary>
    private string GetLocalizedString(string key)
    {
        var stringTable = LocalizationSettings.StringDatabase.GetTable(badgeTableName);
        if (stringTable != null)
        {
            var entry = stringTable.GetEntry(key);
            if (entry != null)
            {
                return entry.GetLocalizedString();
            }
        }

        // Fallback: return the key itself if not found
        Debug.LogWarning($"Localization key '{key}' not found in table '{badgeTableName}'");
        return key;
    }

    /// <summary>
    /// Get localized badge name
    /// </summary>
    private string GetBadgeName(string badgeID)
    {
        return GetLocalizedString($"{badgeID}_name");
    }

    /// <summary>
    /// Get localized badge description
    /// </summary>
    private string GetBadgeDescription(string badgeID)
    {
        return GetLocalizedString($"{badgeID}_desc");
    }

    // ============================================================================
    // FIREBASE INTEGRATION
    // ============================================================================

    /// <summary>
    /// Load user's progress from Firebase
    /// </summary>
    public async System.Threading.Tasks.Task LoadProgressFromFirebase()
    {
        if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            Debug.LogWarning("Cannot load from Firebase: User not logged in");
            return;
        }

        // Load from PlayerManager (which loads from Firebase)
        await PlayerManager.Instance.LoadUserProgressAsync();

        // Sync local data
        totalCardsCollected = PlayerManager.Instance.totalCardsCollected;

        unlockedBadges.Clear();
        foreach (string badgeId in PlayerManager.Instance.badges)
        {
            unlockedBadges.Add(badgeId);
        }

        Debug.Log($"✅ Loaded from Firebase: {totalCardsCollected} cards, {unlockedBadges.Count} badges");
    }

    // ============================================================================
    // CARD COLLECTION
    // ============================================================================

    /// <summary>
    /// Call this when ANY card is collected
    /// </summary>
    public void OnCardCollected(string cardID, string roomID)
    {
        totalCardsCollected++;

        // Track room-specific progress
        if (!cardsByRoom.ContainsKey(roomID))
        {
            cardsByRoom[roomID] = new HashSet<string>();
        }

        // Check if this is the first card in this room
        bool isFirstInRoom = cardsByRoom[roomID].Count == 0;

        cardsByRoom[roomID].Add(cardID);

        // Award points for card collection
        if (ScoreManager.Instance != null)
        {
            ScoreManager.Instance.OnCardCollected(cardID, roomID, isFirstInRoom);
        }

        // Log room progress for debugging
        int cardsInRoom = cardsByRoom[roomID].Count;
        int expectedCards = expectedCardsPerRoom.ContainsKey(roomID) ? expectedCardsPerRoom[roomID] : 0;
        Debug.Log($" Room '{roomID}' progress: {cardsInRoom}/{expectedCards} cards found");

        // Save to Firebase
        if (useFirebase && PlayerManager.Instance != null && !string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            PlayerManager.Instance.OnCardCollected(cardID);
        }
        else
        {
            // Fallback to local storage
            SaveProgress();
        }

        if (showDebugMessages)
        {
            Debug.Log($" Card collected! Total: {totalCardsCollected}");
        }

        // Check if any badges should be unlocked
        CheckBadgeUnlocks();
    }

    // ============================================================================
    // BADGE UNLOCK CHECKS
    // ============================================================================

    /// <summary>
    /// Check if player earned any badges
    /// </summary>
    void CheckBadgeUnlocks()
    {
        // ============================================================
        // MILESTONE BADGES (based on total card count)
        // ============================================================

        // Badge 1: First Discovery - collect any card
        if (totalCardsCollected >= 1 && !HasBadge("first_discovery"))
        {
            UnlockBadge("first_discovery");
        }

        // Badge 2: Curious Explorer - collect 3 cards
        if (totalCardsCollected >= 3 && !HasBadge("curious_explorer"))
        {
            UnlockBadge("curious_explorer");
        }

        // Badge 3: Dedicated Seeker - collect 6 cards
        if (totalCardsCollected >= 6 && !HasBadge("dedicated_seeker"))
        {
            UnlockBadge("dedicated_seeker");
        }

        // Badge 4: Persistent Scholar - collect 9 cards
        if (totalCardsCollected >= 9 && !HasBadge("persistent_scholar"))
        {
            UnlockBadge("persistent_scholar");
        }

        // Badge 5: Heritage Guardian - collect 15 cards
        if (totalCardsCollected >= 15 && !HasBadge("heritage_guardian"))
        {
            UnlockBadge("heritage_guardian");
        }

        // Badge 6: Complete Collection - collect all 18 cards
        if (totalCardsCollected >= 18 && !HasBadge("complete_collection"))
        {
            UnlockBadge("complete_collection");
        }

        // ============================================================
        // ROOM COMPLETION BADGES (based on completing specific rooms)
        // ============================================================

        // Balcony Complete (Çardak)
        if (HasAllCardsInRoom("balcony") && !HasBadge("balcony_master"))
        {
            UnlockBadge("balcony_master");
        }

        // Bedroom Complete
        if (HasAllCardsInRoom("bedroom") && !HasBadge("bedroom_master"))
        {
            UnlockBadge("bedroom_master");
        }

        // Guest Room Complete
        if (HasAllCardsInRoom("guest_room") && !HasBadge("guest_room_master"))
        {
            UnlockBadge("guest_room_master");
        }

        // Kitchen Complete
        if (HasAllCardsInRoom("kitchen") && !HasBadge("kitchen_master"))
        {
            UnlockBadge("kitchen_master");
        }

        // Workshop Complete
        if (HasAllCardsInRoom("workshop") && !HasBadge("workshop_master"))
        {
            UnlockBadge("workshop_master");
        }

        // Archive Complete
        if (HasAllCardsInRoom("archive") && !HasBadge("archive_master"))
        {
            UnlockBadge("archive_master");
        }

        // ============================================================
        // MASTER COMPLETION BADGE (all rooms finished)
        // ============================================================

        if (HasCompletedAllRooms() && !HasBadge("museum_master"))
        {
            UnlockBadge("museum_master");
        }
    }

    // ============================================================================
    // BADGE UNLOCKING
    // ============================================================================

    /// <summary>
    /// Unlock a specific badge (localized version)
    /// </summary>
    void UnlockBadge(string badgeID)
    {
        if (unlockedBadges.Contains(badgeID))
        {
            return; // Already unlocked
        }

        unlockedBadges.Add(badgeID);

        // Get localized name and description
        string badgeName = GetBadgeName(badgeID);
        string description = GetBadgeDescription(badgeID);

        if (ScoreManager.Instance != null)
        {
            ScoreManager.Instance.OnBadgeUnlocked(totalCardsCollected);
        }

        // Save to Firebase
        if (useFirebase && PlayerManager.Instance != null && !string.IsNullOrEmpty(PlayerManager.Instance.userId))
        {
            PlayerManager.Instance.AddBadge(badgeID, badgeName, description);
        }
        else
        {
            // Fallback to local storage
            PlayerPrefs.SetInt($"Badge_{badgeID}_Unlocked", 1);
            PlayerPrefs.Save();
        }

        Debug.Log("╔════════════════════════════════════════╗");
        Debug.Log($"║   BADGE UNLOCKED: {badgeName}");
        Debug.Log($"║  {description}");
        Debug.Log("╚════════════════════════════════════════╝");

        // Show badge popup UI with badgeID for icon lookup
        ShowBadgeNotification(badgeID, badgeName, description);
    }

    /// <summary>
    /// Check if player has a specific badge
    /// </summary>
    public bool HasBadge(string badgeID)
    {
        return unlockedBadges.Contains(badgeID);
    }

    /// <summary>
    /// Get total number of cards collected
    /// </summary>
    public int GetTotalCardsCollected()
    {
        return totalCardsCollected;
    }

    /// <summary>
    /// Get list of all unlocked badges
    /// </summary>
    public List<string> GetUnlockedBadges()
    {
        return new List<string>(unlockedBadges);
    }

    // ============================================================================
    // LOCAL STORAGE (FALLBACK)
    // ============================================================================

    /// <summary>
    /// Save progress to PlayerPrefs (fallback when offline)
    /// </summary>
    void SaveProgress()
    {
        PlayerPrefs.SetInt("TotalCardsCollected", totalCardsCollected);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Load progress from PlayerPrefs (fallback when offline)
    /// </summary>
    void LoadBadgeProgress()
    {
        // Load total cards collected
        totalCardsCollected = PlayerPrefs.GetInt("TotalCardsCollected", 0);

        // Load unlocked badges
        unlockedBadges.Clear();

        // Check each possible badge
        string[] allBadgeIDs = {
            "first_discovery",
            "curious_explorer",
            "dedicated_seeker",
            "persistent_scholar",
            "heritage_guardian",
            "complete_collection",
            "balcony_master",
            "bedroom_master",
            "guest_room_master",
            "kitchen_master",
            "workshop_master",
            "archive_master",
            "museum_master"
        };

        foreach (string badgeID in allBadgeIDs)
        {
            if (PlayerPrefs.GetInt($"Badge_{badgeID}_Unlocked", 0) == 1)
            {
                unlockedBadges.Add(badgeID);
            }
        }
    }

    /// <summary>
    /// Show badge notification UI
    /// </summary>
    void ShowBadgeNotification(string badgeID, string badgeName, string description)
    {
        // Show UI notification with badgeID for icon lookup
        if (BadgeNotificationUI.instance != null)
        {
            BadgeNotificationUI.instance.ShowBadge(badgeID, badgeName, description);
        }
        else
        {
            Debug.LogWarning("BadgeNotificationUI not found in scene!");
        }
    }

    /// <summary>
    /// Reset all badges (for testing)
    /// </summary>
    public void ResetAllBadges()
    {
        unlockedBadges.Clear();
        totalCardsCollected = 0;
        cardsByRoom.Clear(); // Clear room progress

        // Clear Firebase data
        if (PlayerManager.Instance != null)
        {
            PlayerManager.Instance.badges.Clear();
            PlayerManager.Instance.totalCardsCollected = 0;
            PlayerManager.Instance.cardsFound.Clear();
        }

        // Clear ALL PlayerPrefs (easiest way to reset everything)
        PlayerPrefs.DeleteAll();
        PlayerPrefs.Save();

        Debug.Log(" All badges, cards, and progress reset!");
    }

    // ============================================================================
    // ROOM PROGRESS HELPERS
    // ============================================================================

    /// <summary>
    /// Check if player has found all cards in a specific room
    /// </summary>
    public bool HasAllCardsInRoom(string roomID)
    {
        if (!expectedCardsPerRoom.ContainsKey(roomID))
        {
            Debug.LogWarning($"Room '{roomID}' not defined in expectedCardsPerRoom!");
            return false;
        }

        int expectedCards = expectedCardsPerRoom[roomID];
        int foundCards = cardsByRoom.ContainsKey(roomID) ? cardsByRoom[roomID].Count : 0;

        return foundCards >= expectedCards;
    }

    /// <summary>
    /// Get progress for a specific room (e.g., "2/4")
    /// </summary>
    public string GetRoomProgress(string roomID)
    {
        int expectedCards = expectedCardsPerRoom.ContainsKey(roomID) ? expectedCardsPerRoom[roomID] : 0;
        int foundCards = cardsByRoom.ContainsKey(roomID) ? cardsByRoom[roomID].Count : 0;

        return $"{foundCards}/{expectedCards}";
    }

    /// <summary>
    /// Get how many cards have been found in a room
    /// </summary>
    public int GetCardsFoundInRoom(string roomID)
    {
        return cardsByRoom.ContainsKey(roomID) ? cardsByRoom[roomID].Count : 0;
    }

    /// <summary>
    /// Check if player has completed ALL rooms
    /// </summary>
    public bool HasCompletedAllRooms()
    {
        foreach (var room in expectedCardsPerRoom.Keys)
        {
            if (!HasAllCardsInRoom(room))
            {
                return false;
            }
        }
        return true;
    }
}