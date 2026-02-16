using UnityEngine;
using UnityEngine.SceneManagement;

public class AchievementsFlow : MonoBehaviour
{
    [Header("Scene Names")]
    public string backScene = "MuseumScene";   // set in Inspector
    public string susScene = "SUSScene";       // set in Inspector
    public string finishScene = "MuseumScene"; // set in Inspector

    public void GoToSUS()
    {
        LoadSceneSafe(susScene, "SUS");
    }

    public void GoBack()
    {
        LoadSceneSafe(backScene, "Back");
    }

    public void Finish()
    {
        LoadSceneSafe(finishScene, "Finish");
    }

    private void LoadSceneSafe(string sceneName, string actionLabel)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            Debug.LogWarning($"[AchievementsFlow] {actionLabel}: scene name is empty!");
            return;
        }

        Debug.Log($"[AchievementsFlow] {actionLabel}: Loading scene '{sceneName}'...");
        SceneManager.LoadScene(sceneName);
    }
}
