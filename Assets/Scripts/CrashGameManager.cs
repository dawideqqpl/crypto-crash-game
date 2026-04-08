using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using PlayFab;
using PlayFab.ClientModels;

// TODO: Implement proper certificate pinning before production deployment.

/// <summary>
/// Manages game logic and UI for the Crash game,
/// integrating with the PlayFab banking system and game server.
/// </summary>
public class CrashGameManager : MonoBehaviour
{
    public static CrashGameManager _Instance;

    [Header("Bank UI References")]
    public TextMeshProUGUI balanceText;
    public TMP_InputField depositAmountInput;
    public TMP_InputField withdrawAmountInput;
    public Button depositButton;
    public Button withdrawButton;

    [Header("Game UI References")]
    public TextMeshProUGUI multiplierText;
    public TMP_InputField betInput;
    public TMP_InputField autoCashOutInput;

    public Button placeBetButton;
    public Button cashOutButton;
    public Button cancelAutoBetButton;
    public TextMeshProUGUI cashOutButtonText;

    public TextMeshProUGUI nextRoundInText;
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI serverSeedHashText;
    public GameObject verificationPanel;

    [Header("Game History UI")]
    public Transform historyContainer;
    public GameObject historyItemPrefab;

    [Header("External Components")]
    public CrashAnimator crashAnimator;
    public XrpWalletConnector walletConnector;

    [Header("Game Settings")]
    public float serverSyncInterval = 0.25f;
    public float crashDisplayDuration = 5f;

    // State variables
    private string playfabId;
    private Coroutine gameUpdateCoroutine;
    private bool isGameRunningOnClient = false;
    private float parabolicGrowthFactor = 0.03f; // Default value, overridden by server
    private float lastKnownServerTime;
    private float lastServerUpdateTime;
    private int currentBetAmount = 0;
    private float currentAutoCashOutTarget = 0f;

    private bool hasActiveBet = false;

    public GameObject loadingObject;
    private double lastKnownNextRoundStartTime = 0;

    [System.Serializable]
    private class LinkPlayFabRequest
    {
        public string playfabId;
    }

    void Awake()
    {
        _Instance = this;
        Application.runInBackground = true;
    }

    void Start()
    {
        depositButton.onClick.AddListener(OnDepositClicked);
        withdrawButton.onClick.AddListener(OnWithdrawClicked);
        placeBetButton.onClick.AddListener(OnPlaceBetClicked);
        cashOutButton.onClick.AddListener(OnCashOutClicked);
        cancelAutoBetButton.onClick.AddListener(OnCancelAutoBetClicked);
        cancelAutoBetButton.gameObject.SetActive(false);
        autoCashOutInput.onEndEdit.AddListener(ValidateAutoCashOutInput);
        ResetUIForNewRound();

        // Start server sync loop immediately in spectator mode
        gameUpdateCoroutine = StartCoroutine(UpdateGameStateFromServer());
    }

    void Update()
    {
        // Smooth multiplier interpolation while game is running
        if (isGameRunningOnClient)
        {
            float timeSinceLastUpdate = Time.realtimeSinceStartup - lastServerUpdateTime;
            float estimatedCurrentServerTime = lastKnownServerTime + timeSinceLastUpdate;
            float interpolatedMultiplier = 1.0f + this.parabolicGrowthFactor * Mathf.Pow(estimatedCurrentServerTime, 2);
            multiplierText.text = $"{interpolatedMultiplier:F2}x";
            crashAnimator.UpdateAnimation(interpolatedMultiplier, estimatedCurrentServerTime);

            if (hasActiveBet && currentAutoCashOutTarget > 1.0f && interpolatedMultiplier >= currentAutoCashOutTarget)
            {
                statusText.text = $"<color=green>Auto Cash Out!</color> @ {currentAutoCashOutTarget:F2}x";
                statusText.gameObject.SetActive(true);

                cashOutButton.gameObject.SetActive(false);
                cancelAutoBetButton.gameObject.SetActive(false);
                hasActiveBet = false;
            }

            if (hasActiveBet && currentBetAmount > 0 && cashOutButton.gameObject.activeInHierarchy)
            {
                int potentialPayout = (int)(currentBetAmount * interpolatedMultiplier);
                cashOutButtonText.text = $"Cash Out - {potentialPayout} NAMI";
            }
        }
    }

    #region --- Bank and PlayFab Logic ---

    // Called by XrpWalletConnector after a successful login
    public void OnWalletConnected(string walletAddress)
    {
        loadingObject.gameObject.SetActive(true);

        if (string.IsNullOrEmpty(PlayFabSettings.staticSettings.TitleId))
        {
            PlayFabSettings.staticSettings.TitleId = GameConfig.Instance.playfabTitleId;
        }
        var request = new LoginWithCustomIDRequest { CustomId = walletAddress, CreateAccount = true };
        PlayFabClientAPI.LoginWithCustomID(request, OnPlayFabLoginSuccess, OnPlayFabError);
    }

    public void OnCancelAutoBetClicked()
    {
        cancelAutoBetButton.interactable = false;
        StartCoroutine(RequestCancelAutoBetCoroutine());
    }

    private IEnumerator RequestCancelAutoBetCoroutine()
    {
        string sessionToken = walletConnector.GetSessionToken();
        if (string.IsNullOrEmpty(sessionToken)) { yield break; }

        using (UnityWebRequest www = new UnityWebRequest(walletConnector.backendUrl + "/api/cancel-auto-cashout", "POST"))
        {
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Authorization", "Bearer " + sessionToken);

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                statusText.text = "Auto cash out cancelled.";
                cancelAutoBetButton.gameObject.SetActive(false);
                autoCashOutInput.interactable = true;
            }
            else
            {
                Debug.LogError("Failed to cancel auto cash-out: " + www.downloadHandler.text);
                cancelAutoBetButton.interactable = true;
            }
        }
    }

    private void OnPlayFabLoginSuccess(LoginResult result)
    {
        Debug.Log("PlayFab login successful. PlayFabID: " + result.PlayFabId);
        this.playfabId = result.PlayFabId;

        StartCoroutine(LinkPlayFabIdToServerCoroutine(result.PlayFabId));

        FetchPlayerBalance();
        FetchCrashHistory();
    }

    private IEnumerator LinkPlayFabIdToServerCoroutine(string playfabId)
    {
        string currentToken = walletConnector.GetSessionToken();
        if (string.IsNullOrEmpty(currentToken))
        {
            Debug.LogError("Cannot link PlayFab ID to session — missing token.");
            yield break;
        }

        var requestData = new LinkPlayFabRequest { playfabId = playfabId };
        string jsonBody = JsonUtility.ToJson(requestData);

        using (UnityWebRequest www = new UnityWebRequest(walletConnector.backendUrl + "/api/link-playfab", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + currentToken);

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<LinkPlayFabResponse>(www.downloadHandler.text);
                if (response.success && !string.IsNullOrEmpty(response.sessionToken))
                {
                    walletConnector.UpdateSessionToken(response.sessionToken);
                }
                else
                {
                    Debug.LogError("Server did not return a new token after linking PlayFab ID.");
                }
            }
            else
            {
                Debug.LogError("Failed to link PlayFab ID to server session: " + www.downloadHandler.text);
            }
        }
    }

    public void FetchPlayerBalance()
    {
        PlayFabClientAPI.GetUserInventory(new GetUserInventoryRequest(), (result) => {
            if (result.VirtualCurrency.TryGetValue("NA", out int balance))
            {
                UpdateBalanceUI(balance);
            }
        }, OnPlayFabError);
    }

    private void UpdateBalanceUI(long newBalance)
    {
        balanceText.text = $"{FormatBalance(newBalance)} NAMI";
    }

    private string FormatBalance(long balance)
    {
        if (balance >= 1_000_000_000_000L)
            return (balance / 1_000_000_000_000f).ToString("F2") + "T";
        if (balance >= 1_000_000_000L)
            return (balance / 1_000_000_000f).ToString("F2") + "B";
        if (balance >= 1_000_000)
            return (balance / 1_000_000f).ToString("F2") + "M";
        if (balance >= 1_000)
            return (balance / 1_000f).ToString("F2") + "K";
        return balance.ToString();
    }

    private void OnDepositClicked()
    {
        if (float.TryParse(depositAmountInput.text, out float amount) && amount > 0)
        {
            walletConnector.InitiateDeposit(amount, this.playfabId);
        }
    }

    private void OnWithdrawClicked()
    {
        if (float.TryParse(withdrawAmountInput.text, out float amount) && amount > 0)
        {
            StartCoroutine(RequestWithdrawCoroutine(amount));
        }
    }

    private IEnumerator RequestWithdrawCoroutine(float amount)
    {
        withdrawButton.interactable = false;
        statusText.text = "Processing withdrawal...";

        string sessionToken = walletConnector.GetSessionToken();
        if (string.IsNullOrEmpty(sessionToken))
        {
            Debug.LogError("No session token — cannot request withdrawal.");
            statusText.text = "Error: No session.";
            withdrawButton.interactable = true;
            yield break;
        }

        string jsonBody = JsonUtility.ToJson(new BetRequest { amount = amount });

        using (UnityWebRequest www = new UnityWebRequest(walletConnector.backendUrl + "/api/withdraw", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + sessionToken);

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Withdrawal request sent successfully.");
                statusText.text = "Withdrawal initiated! Funds on the way.";
                Invoke(nameof(FetchPlayerBalance), 5f);
            }
            else
            {
                Debug.LogError("Withdrawal request failed: " + www.downloadHandler.text);
                statusText.text = "Withdrawal error: " + www.downloadHandler.text;
            }
        }
        withdrawButton.interactable = true;
    }

    private void FetchCrashHistory()
    {
        PlayFabClientAPI.GetTitleData(new GetTitleDataRequest(), OnHistoryReceived, OnPlayFabError);
    }

    private void OnHistoryReceived(GetTitleDataResult result)
    {
        loadingObject.gameObject.SetActive(false);
        if (result.Data != null && result.Data.ContainsKey("CrashHistory"))
        {
            DisplayCrashHistory(result.Data["CrashHistory"]);
        }
    }

    private void DisplayCrashHistory(string jsonHistory)
    {
        foreach (Transform child in historyContainer) { Destroy(child.gameObject); }
        string[] values = jsonHistory.Replace("[", "").Replace("]", "").Replace("\"", "").Split(',');
        foreach (string value in values)
        {
            if (float.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float crashValue))
            {
                GameObject itemGO = Instantiate(historyItemPrefab, historyContainer);
                itemGO.GetComponent<Image>().color = crashValue < 2.0f ? Color.red : Color.green;
                TextMeshProUGUI itemText = itemGO.GetComponentInChildren<TextMeshProUGUI>();
                if (itemText != null)
                {
                    itemText.text = $"{crashValue:F2}x";
                }
            }
        }
    }

    private void OnPlayFabError(PlayFabError error)
    {
        Debug.LogError("PlayFab error: " + error.GenerateErrorReport());
        statusText.text = "PlayFab error: " + error.ErrorMessage;
    }

    #endregion

    #region --- Game Logic (Bet, Cash Out) ---

    private void OnPlaceBetClicked()
    {
        if (isGameRunningOnClient)
        {
            statusText.text = "Wait for the round to end.";
            return;
        }
        if (hasActiveBet)
        {
            statusText.text = "You already have an active bet.";
            return;
        }

        if (int.TryParse(betInput.text, out int amount) && amount > 0)
        {
            placeBetButton.interactable = false;
            statusText.gameObject.SetActive(true);
            statusText.text = "Bet waiting for server confirmation...";
            StartCoroutine(RequestPlaceBetCoroutine(amount));
        }
    }

    private IEnumerator RequestPlaceBetCoroutine(int amount)
    {
        float autoCashOutValue = 0f;
        if (!string.IsNullOrEmpty(autoCashOutInput.text))
        {
            float.TryParse(autoCashOutInput.text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out autoCashOutValue);
        }

        string sessionToken = walletConnector.GetSessionToken();
        string jsonBody = JsonUtility.ToJson(new BetRequest { amount = amount, autoCashOut = autoCashOutValue });

        using (UnityWebRequest www = new UnityWebRequest(walletConnector.backendUrl + "/api/place-bet", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + sessionToken);

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Bet placed successfully.");
                statusText.text = "Bet placed!";
                hasActiveBet = true;
                placeBetButton.gameObject.SetActive(false);
                autoCashOutInput.interactable = false;
                this.currentBetAmount = amount;
                this.currentAutoCashOutTarget = autoCashOutValue;

                if (autoCashOutValue > 0)
                {
                    cancelAutoBetButton.gameObject.SetActive(true);
                }

                FetchPlayerBalance();
            }
            else
            {
                Debug.LogError("Failed to place bet: " + www.downloadHandler.text);
                var errorResponse = JsonUtility.FromJson<ErrorResponse>(www.downloadHandler.text);
                statusText.text = "Bet place error: " + (errorResponse?.error ?? www.downloadHandler.text);
                placeBetButton.interactable = true;
            }
        }
    }

    [System.Serializable]
    public class ErrorResponse { public string error; }

    private void OnCashOutClicked()
    {
        cashOutButton.interactable = false;
        StartCoroutine(RequestCashOutCoroutine());
    }

    private IEnumerator RequestCashOutCoroutine()
    {
        string sessionToken = walletConnector.GetSessionToken();
        if (string.IsNullOrEmpty(sessionToken))
        {
            yield break;
        }

        using (UnityWebRequest www = new UnityWebRequest(walletConnector.backendUrl + "/api/cash-out", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes("{}");
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Authorization", "Bearer " + sessionToken);
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<PayoutResponse>(www.downloadHandler.text);
                hasActiveBet = false;
                statusText.gameObject.SetActive(true);
                statusText.text = $"<color=green>Cashed out!</color> @ {response.multiplier:F2}x";
                isGameRunningOnClient = false;
                cashOutButton.gameObject.SetActive(false);
                crashAnimator.EndAnimation(false);
                FetchPlayerBalance();
            }
            else
            {
                Debug.LogError($"Cash-out failed: {www.error}. Response: {www.downloadHandler.text}");
                cashOutButton.interactable = true;
            }
        }
    }

    #endregion

    #region --- Server Synchronization ---

    private IEnumerator UpdateGameStateFromServer()
    {
        while (true)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(walletConnector.backendUrl + "/api/game-state"))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    var response = JsonUtility.FromJson<GameStateResponse>(www.downloadHandler.text);
                    this.parabolicGrowthFactor = response.parabolicGrowthFactor;

                    if (response.isRunning)
                    {
                        if (!isGameRunningOnClient)
                        {
                            HandleGameRunning(response);
                        }
                        lastKnownNextRoundStartTime = 0;
                    }
                    else
                    {
                        if (isGameRunningOnClient)
                        {
                            HandleCrash(response);
                        }
                        else if (lastKnownNextRoundStartTime > 0 && response.nextRoundStartTime > lastKnownNextRoundStartTime)
                        {
                            CrashAnimator._Instance.waveImage.gameObject.SetActive(true);
                            multiplierText.gameObject.SetActive(true);
                            HandleCrash(response);
                        }

                        lastKnownNextRoundStartTime = response.nextRoundStartTime;
                    }
                }
                else
                {
                    Debug.LogError($"Failed to fetch game state: {www.error}");
                    statusText.text = "Connection error...";
                    yield return new WaitForSecondsRealtime(3f);
                }
            }
            yield return new WaitForSecondsRealtime(serverSyncInterval);
        }
    }

    private void HandleGameRunning(GameStateResponse state)
    {
        isGameRunningOnClient = true;

        nextRoundInText.gameObject.SetActive(false);
        multiplierText.gameObject.SetActive(true);

        if (hasActiveBet)
        {
            cashOutButton.gameObject.SetActive(true);
            cashOutButton.interactable = true;
        }

        lastKnownServerTime = state.timeElapsed;
        lastServerUpdateTime = Time.realtimeSinceStartup;
        serverSeedHashText.text = $"Server Seed Hash: {state.serverSeedHash}";
    }

    private void HandleCrash(GameStateResponse finalState)
    {
        isGameRunningOnClient = false;
        hasActiveBet = false;
        if (gameUpdateCoroutine != null)
        {
            StopCoroutine(gameUpdateCoroutine);
            gameUpdateCoroutine = null;
        }

        statusText.gameObject.SetActive(true);
        statusText.text = $"<color=red>CRASH!</color>";
        multiplierText.text = $"{finalState.crashPoint:F2}x";
        cashOutButton.gameObject.SetActive(false);
        crashAnimator.EndAnimation(true);

        verificationPanel.SetActive(true);

        StartCoroutine(CrashDisplayAndCountdownSequence(finalState));
    }

    private IEnumerator CrashDisplayAndCountdownSequence(GameStateResponse finalState)
    {
        yield return new WaitForSecondsRealtime(crashDisplayDuration);
        FetchCrashHistory();

        StartCoroutine(CountdownToNextRound(finalState.nextRoundStartTime, finalState.serverTime, crashDisplayDuration));
    }

    /// <summary>
    /// Validates the auto cash-out input, clamping the value to the range 1.01–100.00
    /// and formatting it to two decimal places.
    /// </summary>
    private void ValidateAutoCashOutInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string sanitizedText = text.Replace(',', '.');

        if (float.TryParse(sanitizedText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float value))
        {
            float clampedValue = Mathf.Clamp(value, 1.01f, 100.00f);
            float roundedValue = (float)System.Math.Round(clampedValue, 2);
            autoCashOutInput.text = roundedValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private IEnumerator CountdownToNextRound(double nextRoundTimestamp, double serverTimestamp, float timeAlreadyWaited)
    {
        ResetUIForNewRound();
        float remainingTime = (float)(((nextRoundTimestamp - 1) - serverTimestamp) / 1000.0) - timeAlreadyWaited;
        statusText.gameObject.SetActive(false);

        while (remainingTime > 0)
        {
            if (isGameRunningOnClient) yield break;
            multiplierText.gameObject.SetActive(false);
            nextRoundInText.gameObject.SetActive(true);
            nextRoundInText.text = $"Next round in\n{remainingTime:F1}";
            remainingTime -= Time.deltaTime;

            // Disable bet button just before round starts to prevent late bets
            placeBetButton.interactable = remainingTime >= 1.5f;

            yield return null;
        }

        statusText.text = "";
        gameUpdateCoroutine = StartCoroutine(UpdateGameStateFromServer());
    }

    private void ResetUIForNewRound()
    {
        cancelAutoBetButton.interactable = true;
        placeBetButton.gameObject.SetActive(true);
        placeBetButton.interactable = true;
        cashOutButton.gameObject.SetActive(false);
        cancelAutoBetButton.gameObject.SetActive(false);
        autoCashOutInput.interactable = true;
        verificationPanel.SetActive(false);
        crashAnimator.ResetAnimation();

        lastKnownServerTime = 0f;
        lastServerUpdateTime = Time.realtimeSinceStartup;

        currentBetAmount = 0;
        currentAutoCashOutTarget = 0f;

        if (cashOutButtonText != null)
        {
            cashOutButtonText.text = "Cash Out";
        }
    }

    #endregion
}

[System.Serializable]
public class BetRequest
{
    public float amount;
    public float autoCashOut;
}

[System.Serializable]
public class GameStateResponse
{
    public bool isRunning;
    public float multiplier;
    public float timeElapsed;
    public string serverSeedHash;
    public float crashPoint;
    public string serverSeed;
    public string clientSeed;
    public double nextRoundStartTime;
    public double serverTime;
    public float parabolicGrowthFactor;
}

[System.Serializable]
public class PayoutResponse
{
    public bool success;
    public float amount;
    public float multiplier;
}

[System.Serializable]
public class LinkPlayFabResponse
{
    public bool success;
    public string sessionToken;
}
