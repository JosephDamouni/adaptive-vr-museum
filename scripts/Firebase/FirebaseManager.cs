using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Firebase;
using Firebase.Auth;
using Firebase.Firestore;

/// <summary>
/// Enhanced FirebaseManager for IUI 2026 Research Project
/// Tracks ALL engagement signals for paper analysis
/// </summary>
public class FirebaseManager : MonoBehaviour
{
    public static FirebaseManager Instance { get; private set; }

    public FirebaseApp App { get; private set; }
    public FirebaseFirestore Firestore { get; private set; }
    public FirebaseAuth Auth { get; private set; }

    public bool IsReady { get; private set; } = false;
    public string CurrentParticipantCode { get; private set; }

    // Research tracking
    private string sessionId;
    private float sessionStartTime;

    // ✅ PUBLIC: Expose sessionId for unified Firestore paths
    public string CurrentSessionId => sessionId;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeFirebase();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private async void InitializeFirebase()
    {
        Debug.Log("[FirebaseManager] Checking dependencies...");

        var status = await FirebaseApp.CheckAndFixDependenciesAsync();
        if (status != DependencyStatus.Available)
        {
            Debug.LogError($"[FirebaseManager] Firebase dependencies missing: {status}");
            return;
        }

        App = FirebaseApp.DefaultInstance;
        Firestore = FirebaseFirestore.DefaultInstance;
        Auth = FirebaseAuth.DefaultInstance;

        IsReady = true;
        Debug.Log("[FirebaseManager] Firebase initialized successfully!");
    }

    // ========================================================================
    // PARTICIPANT LOGIN & SESSION MANAGEMENT
    // ========================================================================

    public async Task<string> LoginWithParticipantCodeAsync(string participantCode, string groupAssignment = "")
    {
        if (!IsReady)
        {
            Debug.LogWarning("Firebase not ready yet.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(participantCode))
        {
            Debug.LogError("[FirebaseManager] Participant code cannot be empty.");
            return null;
        }

        participantCode = participantCode.Trim().ToUpper();
        CurrentParticipantCode = participantCode;

        // Generate unique session ID
        sessionId = $"{participantCode}_{System.DateTime.UtcNow.Ticks}";
        sessionStartTime = Time.time;

        try
        {
            var userDocRef = Firestore.Collection("users").Document(participantCode);

            await userDocRef.SetAsync(new Dictionary<string, object>
            {
                { "participantCode", participantCode },
                { "groupAssignment", groupAssignment }, // "Control" or "Adaptive"
                { "createdAt", FieldValue.ServerTimestamp },
                { "lastActive", FieldValue.ServerTimestamp },
                { "currentSessionId", sessionId }
            }, SetOptions.MergeAll);

            // Create session document
            var sessionDoc = Firestore.Collection("users").Document(participantCode)
                .Collection("sessions").Document(sessionId);

            await sessionDoc.SetAsync(new Dictionary<string, object>
            {
                { "sessionId", sessionId },
                { "startTime", FieldValue.ServerTimestamp },
                { "groupAssignment", groupAssignment },
                { "platform", "Quest3" },
                { "unityVersion", Application.unityVersion }
            });

            Debug.Log($"[FirebaseManager] Session started: {sessionId}");

            return participantCode;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Login failed: {e.Message}");
            return null;
        }
    }

    public async Task EndSessionAsync()
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            float sessionDuration = Time.time - sessionStartTime;

            var sessionDoc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId);

            await sessionDoc.SetAsync(new Dictionary<string, object>
            {
                { "endTime", FieldValue.ServerTimestamp },
                { "duration", sessionDuration },
                { "completed", true }
            }, SetOptions.MergeAll);

            Debug.Log($"[FirebaseManager] Session ended: {sessionDuration:F1}s");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to end session: {e.Message}");
        }
    }

    // ========================================================================
    // ENGAGEMENT TRACKING - HAND DATA
    // ========================================================================

    /// <summary>
    /// Track hand proximity to objects (key engagement indicator)
    /// </summary>
    public async Task LogHandProximityAsync(
        string objectName,
        float leftHandDistance,
        float rightHandDistance,
        bool isInteracting,
        string room = "unknown")
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("handTracking").Document(docId);

            await doc.SetAsync(new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "objectName", objectName },
                { "leftHandDistance", leftHandDistance },
                { "rightHandDistance", rightHandDistance },
                { "minDistance", Mathf.Min(leftHandDistance, rightHandDistance) },
                { "isInteracting", isInteracting },
                { "room", room }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Hand tracking error: {e.Message}");
        }
    }

    /// <summary>
    /// Track pinch gestures and hand interactions
    /// </summary>
    public async Task LogHandGestureAsync(
        string gestureType,
        string targetObject,
        Vector3 handPosition)
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("handGestures").Document(docId);

            await doc.SetAsync(new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "gestureType", gestureType }, // "pinch", "grab", "point", etc.
                { "targetObject", targetObject },
                { "positionX", handPosition.x },
                { "positionY", handPosition.y },
                { "positionZ", handPosition.z }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Gesture tracking error: {e.Message}");
        }
    }

    // ========================================================================
    // ENGAGEMENT TRACKING - HEAD/GAZE DATA
    // ========================================================================

    /// <summary>
    /// Track head position and rotation (gaze approximation)
    /// </summary>
    public async Task LogHeadTrackingAsync(
        Vector3 headPosition,
        Quaternion headRotation,
        float rotationVelocity,
        string lookingAtObject)
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("headTracking").Document(docId);

            await doc.SetAsync(new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "positionX", headPosition.x },
                { "positionY", headPosition.y },
                { "positionZ", headPosition.z },
                { "rotationX", headRotation.eulerAngles.x },
                { "rotationY", headRotation.eulerAngles.y },
                { "rotationZ", headRotation.eulerAngles.z },
                { "rotationVelocity", rotationVelocity }, // High = looking around fast (disengaged)
                { "lookingAtObject", lookingAtObject }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Head tracking error: {e.Message}");
        }
    }

    /// <summary>
    /// Track focused gaze on objects (engagement indicator)
    /// </summary>
    public async Task LogGazeEventAsync(
        string objectName,
        float gazeDuration,
        bool wasInteracted)
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{objectName}_{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("gazeEvents").Document(docId);

            await doc.SetAsync(new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "objectName", objectName },
                { "gazeDuration", gazeDuration }, // >25s = engaged, <15s = disengaged
                { "wasInteracted", wasInteracted }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Gaze tracking error: {e.Message}");
        }
    }

    // ========================================================================
    // ENGAGEMENT TRACKING - MOVEMENT & NAVIGATION
    // ========================================================================

    /// <summary>
    /// Track player movement speed (slow = engaged, fast = disengaged)
    /// </summary>
    public async Task LogMovementAsync(
        Vector3 position,
        float velocity,
        string currentRoom)
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("movement").Document(docId);

            await doc.SetAsync(new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "positionX", position.x },
                { "positionY", position.y },
                { "positionZ", position.z },
                { "velocity", velocity }, // m/s
                { "currentRoom", currentRoom }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Movement tracking error: {e.Message}");
        }
    }

    // ========================================================================
    // ENGAGEMENT CLASSIFICATION (Real-time state)
    // ========================================================================

    /// <summary>
    /// Log engagement state classification
    /// </summary>
    public async Task LogEngagementStateAsync(
        string state, // "ENGAGED" or "DISENGAGED"
        Dictionary<string, object> indicators)
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("engagementStates").Document(docId);

            var data = new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "engagementLevel", state }
            };

            // Merge indicators (hand proximity, gaze duration, etc.)
            foreach (var kvp in indicators)
            {
                data[kvp.Key] = kvp.Value;
            }

            await doc.SetAsync(data);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Engagement state error: {e.Message}");
        }
    }

    // ========================================================================
    // CONTENT ADAPTATION TRACKING
    // ========================================================================

    /// <summary>
    /// Log when content is adapted based on engagement
    /// </summary>
    public async Task LogContentAdaptationAsync(
        string cardId,
        string variantShown, // "engaged" or "disengaged"
        string reason,
        Dictionary<string, object> engagementMetrics)
    {
        if (!IsReady || string.IsNullOrEmpty(CurrentParticipantCode))
            return;

        try
        {
            string docId = $"{cardId}_{System.DateTime.UtcNow.Ticks}";

            var doc = Firestore.Collection("users").Document(CurrentParticipantCode)
                .Collection("sessions").Document(sessionId)
                .Collection("contentAdaptations").Document(docId);

            var data = new Dictionary<string, object>
            {
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionTime", Time.time - sessionStartTime },
                { "cardId", cardId },
                { "variantShown", variantShown },
                { "reason", reason }
            };

            foreach (var kvp in engagementMetrics)
            {
                data[kvp.Key] = kvp.Value;
            }

            await doc.SetAsync(data);

            Debug.Log($"[FirebaseManager] Content adapted: {cardId} → {variantShown}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Content adaptation error: {e.Message}");
        }
    }

    // ========================================================================
    // EXISTING METHODS (Demographics, Badges, Cards, etc.)
    // ========================================================================

    public async Task SaveDemographicsAsync(
        string odId,
        string age,
        string gender,
        string nationality,
        string computerSkills,
        string vrInterest)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("[FirebaseManager] Firestore not ready or null.");
            return;
        }

        Debug.Log($"[FirebaseManager] Saving demographics for {odId}...");

        try
        {
            var data = new Dictionary<string, object>
            {
                { "age", age },
                { "gender", gender },
                { "nationality", nationality },
                { "computerSkills", computerSkills },
                { "vrInterest", vrInterest },
                { "timestamp", FieldValue.ServerTimestamp }
            };

            var doc = Firestore.Collection("users").Document(odId)
                .Collection("demographics").Document("info");

            await doc.SetAsync(data, SetOptions.MergeAll);
            Debug.Log($"[FirebaseManager] Demographics saved for {odId}!");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to save demographics: {e.Message}");
        }
    }

    public async Task SaveBadgeAsync(string odId, string badgeId, string badgeName, string description)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("[FirebaseManager] Firestore not ready.");
            return;
        }

        try
        {
            var badgeDoc = Firestore.Collection("users").Document(odId)
                .Collection("badges").Document(badgeId);

            await badgeDoc.SetAsync(new Dictionary<string, object>
            {
                { "badgeId", badgeId },
                { "badgeName", badgeName },
                { "description", description },
                { "unlockedAt", FieldValue.ServerTimestamp },
                { "sessionId", sessionId }
            }, SetOptions.MergeAll);

            Debug.Log($"[FirebaseManager] Badge '{badgeName}' saved for {odId}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to save badge: {e.Message}");
        }
    }

    public async Task SaveCardCollectedAsync(string odId, string cardId, int totalCardsCollected)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("[FirebaseManager] Firestore not ready.");
            return;
        }

        try
        {
            var cardDoc = Firestore.Collection("users").Document(odId)
                .Collection("cards").Document(cardId);

            await cardDoc.SetAsync(new Dictionary<string, object>
            {
                { "cardId", cardId },
                { "found", true },
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionId", sessionId }
            }, SetOptions.MergeAll);

            var progressDoc = Firestore.Collection("users").Document(odId)
                .Collection("progress").Document("summary");

            await progressDoc.SetAsync(new Dictionary<string, object>
            {
                { "totalCardsCollected", totalCardsCollected },
                { "lastCardFound", cardId },
                { "lastCardTimestamp", FieldValue.ServerTimestamp }
            }, SetOptions.MergeAll);

            Debug.Log($"[FirebaseManager] Card '{cardId}' saved for {odId}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to save card: {e.Message}");
        }
    }

    public async Task<List<string>> LoadUserBadgesAsync(string odId)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("[FirebaseManager] Firestore not ready.");
            return new List<string>();
        }

        try
        {
            var badgesSnapshot = await Firestore.Collection("users").Document(odId)
                .Collection("badges").GetSnapshotAsync();

            var badgeList = new List<string>();
            foreach (var doc in badgesSnapshot.Documents)
            {
                badgeList.Add(doc.Id);
            }

            Debug.Log($"[FirebaseManager] Loaded {badgeList.Count} badges for {odId}");
            return badgeList;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to load badges: {e.Message}");
            return new List<string>();
        }
    }

    public async Task<int> LoadUserCardsAsync(string odId)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("[FirebaseManager] Firestore not ready.");
            return 0;
        }

        try
        {
            var cardsSnapshot = await Firestore.Collection("users").Document(odId)
                .Collection("cards").GetSnapshotAsync();

            Debug.Log($"[FirebaseManager] Loaded {cardsSnapshot.Count} cards for {odId}");
            return cardsSnapshot.Count;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to load cards: {e.Message}");
            return 0;
        }
    }

    public async Task SaveRoomTimeAsync(string odId, string roomId, float timeSpent)
    {
        if (!IsReady || Firestore == null) return;

        try
        {
            var docRef = Firestore.Collection("users").Document(odId)
                .Collection("roomStats").Document(roomId);

            await docRef.SetAsync(new Dictionary<string, object>
            {
                { "timeSpent", FieldValue.Increment(timeSpent) },
                { "visitCount", FieldValue.Increment(1) },
                { "sessionId", sessionId }
            }, SetOptions.MergeAll);

            Debug.Log($"[FirebaseManager] Room '{roomId}' updated for {odId}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to save room time: {e.Message}");
        }
    }

    public async Task SaveUserScoreAsync(string odId, int totalScore)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("Firestore not ready.");
            return;
        }

        try
        {
            var progressDoc = Firestore.Collection("users").Document(odId)
                .Collection("progress").Document("summary");

            await progressDoc.SetAsync(new Dictionary<string, object>
            {
                { "totalScore", totalScore },
                { "lastScoreUpdate", FieldValue.ServerTimestamp }
            }, SetOptions.MergeAll);

            Debug.Log($"Score {totalScore} saved for {odId}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to save user score: {e.Message}");
        }
    }

    public async Task SaveObjectInteractionAsync(
        string odId,
        string objectName,
        float duration,
        int totalInteractions,
        float averageTime)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("[FirebaseManager] Firestore not ready.");
            return;
        }

        try
        {
            string docId = $"{objectName}_{System.DateTime.UtcNow.Ticks}";

            var interactionDoc = Firestore.Collection("users").Document(odId)
                .Collection("objectInteractions").Document(docId);

            await interactionDoc.SetAsync(new Dictionary<string, object>
            {
                { "objectName", objectName },
                { "duration", duration },
                { "totalInteractions", totalInteractions },
                { "averageTime", averageTime },
                { "timestamp", FieldValue.ServerTimestamp },
                { "sessionId", sessionId }
            });

            var statsDoc = Firestore.Collection("users").Document(odId)
                .Collection("objectStats").Document(objectName);

            await statsDoc.SetAsync(new Dictionary<string, object>
            {
                { "totalInteractions", totalInteractions },
                { "totalTimeSpent", FieldValue.Increment(duration) },
                { "averageTime", averageTime },
                { "lastInteraction", FieldValue.ServerTimestamp }
            }, SetOptions.MergeAll);

            Debug.Log($"[FirebaseManager] Object '{objectName}' saved for {odId}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[FirebaseManager] Failed to save object interaction: {e.Message}");
        }
    }

    public async Task<bool> UserHasDemographicsAsync(string odId)
    {
        if (!IsReady || Firestore == null)
        {
            Debug.LogError("Firestore not ready.");
            return false;
        }

        try
        {
            var docRef = Firestore.Collection("users").Document(odId)
                .Collection("demographics").Document("info");

            var snapshot = await docRef.GetSnapshotAsync();
            bool exists = snapshot.Exists;

            Debug.Log($"[FirebaseManager] {odId} has demographics: {exists}");
            return exists;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error checking demographics for {odId}: {e.Message}");
            return false;
        }
    }
}