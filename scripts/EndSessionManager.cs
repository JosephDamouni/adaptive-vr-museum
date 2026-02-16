using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class EndSessionManager : MonoBehaviour
{
    [Header("Scene Names")]
    public string achievementsScene = "AchievementsScene";

    public void EndMuseumSession()
    {
        Debug.Log("Ending museum session...");

        // ✅ NEW: Generate comprehensive session summary
        if (EndOfSessionSummary.Instance != null)
        {
            EndOfSessionSummary.Instance.GenerateSessionSummary();
            Debug.Log("✅ Session summary generated!");
        }
        else
        {
            Debug.LogWarning("⚠️ EndOfSessionSummary not found - summary not generated!");
        }

        // Small delay to ensure Firebase saves complete
        StartCoroutine(LoadAchievementsAfterDelay());
    }

    System.Collections.IEnumerator LoadAchievementsAfterDelay()
    {
        // Give Firebase 2 seconds to save summary
        yield return new WaitForSeconds(2f);

        Debug.Log("Loading Achievements scene...");
        SceneManager.LoadScene(achievementsScene);
    }
}