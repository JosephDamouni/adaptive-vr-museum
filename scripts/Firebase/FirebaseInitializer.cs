using System.Threading.Tasks;
using UnityEngine;
using Firebase;
using Firebase.Extensions;
using Firebase.Auth;
using Firebase.Firestore;

/// <summary>
/// Ensures Firebase initializes before other managers try to use it.
/// Use this if you have a dedicated "Startup" scene.
/// </summary>
public class FirebaseInitializer : MonoBehaviour
{
    public static FirebaseInitializer Instance { get; private set; }
    public static bool IsReady { get; private set; } = false;

    public FirebaseApp App { get; private set; }
    public FirebaseAuth Auth { get; private set; }
    public FirebaseFirestore Firestore { get; private set; }

    private async void Awake()
    {
        // Singleton pattern
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            await InitializeFirebaseAsync();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Initializes Firebase and ensures dependencies are ready.
    /// </summary>
    private async Task InitializeFirebaseAsync()
    {
        Debug.Log("[FirebaseInitializer] Checking dependencies...");

        var dependencyStatus = await FirebaseApp.CheckAndFixDependenciesAsync();
        if (dependencyStatus != DependencyStatus.Available)
        {
            Debug.LogError($"Could not resolve Firebase dependencies: {dependencyStatus}");
            return;
        }

        App = FirebaseApp.DefaultInstance;
        Auth = FirebaseAuth.DefaultInstance;

        try
        {
            Firestore = FirebaseFirestore.DefaultInstance;
            Debug.Log("Firestore reference created.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to initialize Firestore: {e.Message}");
        }

        IsReady = true;
        Debug.Log("Firebase fully initialized and ready!");
    }

    /// <summary>
    /// Wait until Firebase is initialized before continuing.
    /// Example use: await FirebaseInitializer.WaitUntilReady();
    /// </summary>
    public static async Task WaitUntilReady()
    {
        int retries = 0;
        while (!IsReady && retries < 100)
        {
            await Task.Delay(100);
            retries++;
        }

        if (!IsReady)
            Debug.LogWarning("FirebaseInitializer.WaitUntilReady() timed out after 10 seconds.");
    }
}
