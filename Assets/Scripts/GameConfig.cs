using UnityEngine;

/// <summary>
/// Holds runtime configuration values loaded from Assets/Resources/config.json.
/// Copy config.example.json to config.json and fill in your values.
/// config.json is excluded from version control via .gitignore.
/// </summary>
[System.Serializable]
public class GameConfig
{
    private static GameConfig _instance;
    public static GameConfig Instance
    {
        get
        {
            if (_instance == null) _instance = Load();
            return _instance;
        }
    }

    public string playfabTitleId;
    public string backendUrl;

    private static GameConfig Load()
    {
        TextAsset configFile = Resources.Load<TextAsset>("config");
        if (configFile == null)
        {
            Debug.LogError("[GameConfig] config.json not found in Assets/Resources/. " +
                           "Copy config.example.json to config.json and fill in your values.");
            return new GameConfig();
        }
        return JsonUtility.FromJson<GameConfig>(configFile.text);
    }
}
