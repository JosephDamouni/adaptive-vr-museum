using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using Firebase.Firestore;
using Firebase.Extensions;
using TMPro;
using UnityEngine.UI;

public class AchievementsManager : MonoBehaviour
{
    [Header("Score UI")]
    public TMP_Text totalScoreText;
    public TMP_Text scoreBreakdownText;

    [Header("TEST MODE")]
    public bool useTestUserId = false;  
    public string testUserId;

    [Header("Prefabs")]
    public GameObject badgeUIPrefab;
    public GameObject cardUIPrefab;

    [Header("ScrollView Content Parents")]
    public Transform badgesContent;
    public Transform cardsContent;

    [Header("Summary UI")]
    public TMP_Text totalCardsText;
    public TMP_Text totalBadgesText;
    public TMP_Text lastCardFoundText;
    public TMP_Text lastBadgeUnlockedText;
    public TMP_Text roomStatsSummaryText;

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

    [Header("Localization")]
    public string localizationTableName = "FullMuseum"; 

    private FirebaseFirestore db;
    private string odId;
    private StringTable localizedTable;

    // -------------------------------------------------------
    // UNITY FLOW
    // -------------------------------------------------------

    void Start()
    {
        StartCoroutine(LoadAchievementsFlow());
    }

    private IEnumerator LoadAchievementsFlow()
    {
        // Wait for localization to initialize
        yield return LocalizationSettings.InitializationOperation;

        // Load the string table
        var tableOperation = LocalizationSettings.StringDatabase.GetTableAsync(localizationTableName);
        yield return tableOperation;
        localizedTable = tableOperation.Result;

        if (localizedTable == null)
        {
            Debug.LogError($"Failed to load localization table '{localizationTableName}'!");
        }
        else
        {
            Debug.Log($" Loaded localization table '{localizationTableName}' with {localizedTable.Count} entries");
        }

        db = FirebaseFirestore.DefaultInstance;

        if (useTestUserId)
        {
            if (string.IsNullOrEmpty(testUserId))
            {
                Debug.LogError("useTestUserId is ON but testUserId is empty!");
                yield break;
            }
            odId = testUserId;
        }
        else
        {
            if (PlayerManager.Instance == null || string.IsNullOrEmpty(PlayerManager.Instance.userId))
            {
                Debug.LogError("PlayerManager not initialized or user not logged in!");
                yield break;
            }
            odId = PlayerManager.Instance.userId;
        }

        Debug.Log($" Loading achievements for user: {odId}");

        LoadBadges();
        LoadCards();
        LoadSummary();
        LoadScore();
        LoadRoomStats();
    }

    // -------------------------------------------------------
    // LOCALIZATION HELPERS
    // -------------------------------------------------------

    /// <summary>
    /// Get localized string from the table
    /// </summary>
    private string GetLocalizedString(string key)
    {
        if (localizedTable != null)
        {
            var entry = localizedTable.GetEntry(key);
            if (entry != null)
            {
                return entry.GetLocalizedString();
            }
        }
        Debug.LogWarning($"Localization key '{key}' not found!");
        return key; 
    }

    /// <summary>
    /// Get localized string with formatting 
    /// </summary>
    private string GetLocalizedFormat(string key, params object[] args)
    {
        string format = GetLocalizedString(key);
        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return format; // Return unformatted if format fails
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
        return null; // fallback if missing
    }

    // -------------------------------------------------------
    // LOAD BADGES (FIXED - removed 'unlocked' check)
    // -------------------------------------------------------
    private void LoadBadges()
    {
        db.Collection("users").Document(odId)
            .Collection("badges")
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    Debug.LogError($"Failed to load badges: {task.Exception?.Message}");
                    return;
                }

                Debug.Log($" Found {task.Result.Count} badges for user {odId}");

                foreach (var doc in task.Result.Documents)
                {
                    

                    string badgeId = doc.Id;

                    // Get LOCALIZED name and description instead of Firebase values
                    string name = GetLocalizedString($"{badgeId}_name");
                    string description = GetLocalizedString($"{badgeId}_desc");

                    Debug.Log($" Loading badge: {badgeId} → {name}");

                    // Spawn UI item
                    GameObject item = Instantiate(badgeUIPrefab, badgesContent);

                    item.transform.Find("Title").GetComponent<TMP_Text>().text = name;
                    item.transform.Find("Desc").GetComponent<TMP_Text>().text = description;

                    // Assign Icon
                    Image iconImage = item.transform.Find("Icon").GetComponent<Image>();
                    Sprite iconSprite = GetBadgeIcon(badgeId);

                    if (iconSprite != null)
                    {
                        iconImage.sprite = iconSprite;
                    }
                    else
                    {
                        iconImage.enabled = false;
                    }
                }
            });
    }

    // -------------------------------------------------------
    // LOAD CARDS (with localization)
    // -------------------------------------------------------
    private void LoadCards()
    {
        db.Collection("users").Document(odId)
          .Collection("cards")
          .GetSnapshotAsync()
          .ContinueWithOnMainThread(task =>
          {
              if (!task.IsCompletedSuccessfully)
              {
                  Debug.LogError($"Failed to load cards: {task.Exception?.Message}");
                  return;
              }

              Debug.Log($" Found {task.Result.Count} cards for user {odId}");

              foreach (var doc in task.Result.Documents)
              {
                  string cardId = doc.Id;
                  bool found = true; 

                 
                  if (doc.ContainsField("cardId"))
                  {
                      cardId = doc.GetValue<string>("cardId");
                  }

                  if (doc.ContainsField("found"))
                  {
                      found = doc.GetValue<bool>("found");
                  }

                  GameObject item = Instantiate(cardUIPrefab, cardsContent);

                  // Try to get localized card name, fallback to cardId
                  string cardName = GetLocalizedString($"{cardId}_name");
                  if (cardName == $"{cardId}_name") cardName = cardId; // Use ID if no translation

                  item.transform.Find("Title").GetComponent<TMP_Text>().text = cardName;

                  // Localized status text
                  string status = found
                      ? GetLocalizedString("card_status_collected")    
                      : GetLocalizedString("card_status_not_found");  

                  item.transform.Find("Status").GetComponent<TMP_Text>().text = status;
              }
          });
    }

    // -------------------------------------------------------
    // LOAD SUMMARY (with localization)
    // -------------------------------------------------------
    private void LoadSummary()
    {
        db.Collection("users").Document(odId)
          .Collection("progress").Document("summary")
          .GetSnapshotAsync()
          .ContinueWithOnMainThread(task =>
          {
              if (!task.IsCompletedSuccessfully)
              {
                  Debug.LogError($"Failed to load summary: {task.Exception?.Message}");
                  return;
              }

              var doc = task.Result;

              if (!doc.Exists)
              {
                  Debug.LogWarning("Progress summary document doesn't exist");
                  return;
              }

              long totalCards = doc.ContainsField("totalCardsCollected")
                  ? doc.GetValue<long>("totalCardsCollected") : 0;
              long totalBadges = doc.ContainsField("totalBadges")
                  ? doc.GetValue<long>("totalBadges") : 0;
              string lastCard = doc.ContainsField("lastCardFound")
                  ? doc.GetValue<string>("lastCardFound") : "";
              string lastBadge = doc.ContainsField("lastBadgeUnlocked")
                  ? doc.GetValue<string>("lastBadgeUnlocked") : "";

              // Localized UI text with values
              if (totalCardsText != null)
                  totalCardsText.text = GetLocalizedFormat("ui_total_cards", totalCards);
              if (totalBadgesText != null)
                  totalBadgesText.text = GetLocalizedFormat("ui_total_badges", totalBadges);

              // Try to get localized names for last card/badge
              if (lastCardFoundText != null && !string.IsNullOrEmpty(lastCard))
              {
                  string localizedLastCard = GetLocalizedString($"{lastCard}_name");
                  if (localizedLastCard == $"{lastCard}_name") localizedLastCard = lastCard;
                  lastCardFoundText.text = GetLocalizedFormat("ui_last_card_found", localizedLastCard);
              }

              if (lastBadgeUnlockedText != null && !string.IsNullOrEmpty(lastBadge))
              {
                  string localizedLastBadge = GetLocalizedString($"{lastBadge}_name");
                  if (localizedLastBadge == $"{lastBadge}_name") localizedLastBadge = lastBadge;
                  lastBadgeUnlockedText.text = GetLocalizedFormat("ui_last_badge_unlocked", localizedLastBadge);
              }
          });
    }

    // -------------------------------------------------------
    // LOAD SCORE (with localization)
    // -------------------------------------------------------
    private void LoadScore()
    {
        db.Collection("users").Document(odId)
            .Collection("progress").Document("summary")
            .GetSnapshotAsync()
            .ContinueWithOnMainThread(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    Debug.LogError($"Failed to load score: {task.Exception?.Message}");
                    return;
                }

                var doc = task.Result;

                if (!doc.Exists)
                {
                    Debug.LogWarning("Progress summary document doesn't exist");
                    return;
                }

                int totalScore = 0;
                if (doc.ContainsField("totalScore"))
                {
                    totalScore = (int)doc.GetValue<long>("totalScore");
                }

                long cardsCollected = 0;
                if (doc.ContainsField("totalCardsCollected"))
                {
                    cardsCollected = doc.GetValue<long>("totalCardsCollected");
                }

                if (totalScoreText != null)
                {
                    totalScoreText.text = totalScore.ToString("N0");
                }

                if (scoreBreakdownText != null)
                {
                    int cardPoints = (int)(cardsCollected * 100);
                    int badgePoints = totalScore - cardPoints;

                    
                    scoreBreakdownText.text = GetLocalizedFormat("ui_score_breakdown",
                        cardPoints.ToString("N0"),
                        badgePoints.ToString("N0"));
                }
            });
    }

    // -------------------------------------------------------
    // LOAD ROOM STATS (with localization)
    // -------------------------------------------------------
    private void LoadRoomStats()
    {
        db.Collection("users").Document(odId)
          .Collection("roomStats")
          .GetSnapshotAsync()
          .ContinueWithOnMainThread(task =>
          {
              if (!task.IsCompletedSuccessfully)
              {
                  Debug.LogError($"Failed to load room stats: {task.Exception?.Message}");
                  return;
              }

              string summary = "";

              foreach (var doc in task.Result.Documents)
              {
                  string roomId = doc.Id;

                  double timeSpent = doc.ContainsField("timeSpent")
                      ? doc.GetValue<double>("timeSpent") : 0;

                  long visitCount = doc.ContainsField("visitCount")
                      ? doc.GetValue<long>("visitCount") : 0;

                  // Try to get localized room name
                  string roomName = GetLocalizedString($"room_{roomId}_name");
                  if (roomName == $"room_{roomId}_name") roomName = roomId;

                  summary += GetLocalizedFormat("ui_room_stats_line",
                      roomName,
                      timeSpent.ToString("F1"),
                      visitCount) + "\n";
              }

              if (roomStatsSummaryText != null)
                  roomStatsSummaryText.text = summary;
          });
    }
}