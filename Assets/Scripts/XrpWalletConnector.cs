using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using UnityEngine.UI;
using TMPro;
using ZXing;
using ZXing.QrCode;

/// <summary>
/// Manages XRP Ledger wallet connectivity via the Xaman app,
/// handling JWT session management and deposit transaction creation.
/// </summary>
public class XrpWalletConnector : MonoBehaviour
{
    [Header("UI References")]
    public GameObject loginPanel;
    public GameObject sessionLoginPanel;
    public GameObject beforeLoginPanel;
    public GameObject duringLoginPanel;
    public RawImage qrCodeImage;
    public Button connectButton;
    public TextMeshProUGUI statusText;

    [Header("Server Configuration")]
    public string backendUrl;

    private string sessionToken;
    private string userAddress;
    private bool isCheckingStatus = false;

    public string GetSessionToken()
    {
        return this.sessionToken;
    }

    void Start()
    {
        backendUrl = GameConfig.Instance.backendUrl;

        loginPanel.SetActive(true);
        qrCodeImage.gameObject.SetActive(false);
        connectButton.onClick.AddListener(StartLoginProcess);
        AttemptAutoLogin();
    }

    public void UpdateSessionToken(string newToken)
    {
        this.sessionToken = newToken;
        PlayerPrefs.SetString("session_token", newToken);
        PlayerPrefs.Save();
        Debug.Log("Session token updated successfully with PlayFab ID.");
    }

    #region --- Deposit Logic ---

    public void InitiateDeposit(float amount, string playfabId)
    {
        if (string.IsNullOrEmpty(sessionToken))
        {
            Debug.LogError("Cannot initiate deposit — no active session.");
            return;
        }
        StartCoroutine(CreateDepositPayloadCoroutine(amount, playfabId));
    }

    private IEnumerator CreateDepositPayloadCoroutine(float amount, string playfabId)
    {
        statusText.text = "Preparing deposit...";

        var requestData = new DepositRequest { amount = amount, playfabId = playfabId };
        string jsonBody = JsonUtility.ToJson(requestData);

        using (UnityWebRequest www = new UnityWebRequest(backendUrl + "/api/deposit", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + sessionToken);

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Server error creating deposit: {www.error}. Body: {www.downloadHandler.text}");
                statusText.text = "Server error. Please try again.";
            }
            else
            {
                var response = JsonUtility.FromJson<XummPayloadResponse>(www.downloadHandler.text);

                if (!string.IsNullOrEmpty(response.qrLink))
                {
                    statusText.text = "Scan the QR code to complete your deposit.";
                    GenerateQrCode(response.qrLink);
                }
                else
                {
                    statusText.text = "Check the Xaman app on your phone to complete your deposit.";
                    qrCodeImage.gameObject.SetActive(false);
                }

                StartCoroutine(CheckPayloadStatus(response.uuid, (addr) => {
                    Debug.Log($"Deposit from {addr} signed successfully!");
                    statusText.text = "Deposit confirmed! Refreshing balance...";
                    CrashGameManager._Instance.Invoke(nameof(CrashGameManager.FetchPlayerBalance), 3f);
                }));
            }
        }
    }

    #endregion

    #region --- Login and Session Logic ---

    public void StartLoginProcess()
    {
        if (isCheckingStatus) return;
        StartCoroutine(GetLoginPayload());
    }

    private void AttemptAutoLogin()
    {
        string savedToken = PlayerPrefs.GetString("session_token", null);
        if (!string.IsNullOrEmpty(savedToken))
        {
            loginPanel.gameObject.SetActive(false);
            sessionLoginPanel.gameObject.SetActive(true);
            statusText.text = "Verifying saved session...";
            StartCoroutine(VerifyTokenOnServer(savedToken));
        }
    }

    private IEnumerator VerifyTokenOnServer(string token)
    {
        using (UnityWebRequest www = UnityWebRequest.Get(backendUrl + "/api/verify-token"))
        {
            www.SetRequestHeader("Authorization", "Bearer " + token);
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<LoginStatusResponse>(www.downloadHandler.text);
                if (response.isLoggedIn) OnLoginSuccess(response.userAddress, token);
            }
            else
            {
                sessionLoginPanel.gameObject.SetActive(false);
                loginPanel.gameObject.SetActive(true);
                statusText.text = "Session expired. Please log in again.";
                PlayerPrefs.DeleteKey("session_token");
            }
        }
    }

    private IEnumerator GetLoginPayload()
    {
        isCheckingStatus = true;
        connectButton.interactable = false;
        statusText.text = "Generating QR code...";

        using (UnityWebRequest www = UnityWebRequest.PostWwwForm(backendUrl + "/api/login", ""))
        {
            yield return www.SendWebRequest();
            if (www.result != UnityWebRequest.Result.Success)
            {
                statusText.text = "Server error. Please try again.";
                connectButton.interactable = true;
                beforeLoginPanel.SetActive(true);
                duringLoginPanel.SetActive(false);
            }
            else
            {
                var response = JsonUtility.FromJson<XummPayloadResponse>(www.downloadHandler.text);
                GenerateQrCode(response.qrLink);
                statusText.text = "Scan the QR code with the Xaman app.";
                beforeLoginPanel.SetActive(false);
                duringLoginPanel.SetActive(true);
                StartCoroutine(CheckPayloadStatus(response.uuid, null));
            }
        }
    }

    private void OnLoginSuccess(string loggedInAddress, string token)
    {
        this.sessionToken = token;
        this.userAddress = loggedInAddress;
        sessionLoginPanel.gameObject.SetActive(false);
        loginPanel.SetActive(false);
        statusText.text = $"Logged in as: {loggedInAddress.Substring(0, 6)}...";
        CrashGameManager._Instance.OnWalletConnected(loggedInAddress);
    }

    #endregion

    #region --- Helper Functions ---

    private IEnumerator CheckPayloadStatus(string uuid, System.Action<string> onPayloadSuccess)
    {
        isCheckingStatus = true;
        while (isCheckingStatus)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(backendUrl + "/api/login-status/" + uuid))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success)
                {
                    var response = JsonUtility.FromJson<LoginStatusResponse>(www.downloadHandler.text);
                    if (response.isLoggedIn)
                    {
                        isCheckingStatus = false;
                        qrCodeImage.gameObject.SetActive(false);

                        if (!string.IsNullOrEmpty(response.sessionToken))
                        {
                            PlayerPrefs.SetString("session_token", response.sessionToken);
                            PlayerPrefs.Save();
                            OnLoginSuccess(response.userAddress, response.sessionToken);
                        }

                        onPayloadSuccess?.Invoke(response.userAddress);

                        yield break;
                    }
                }
            }
            yield return new WaitForSeconds(2);
        }
    }

    private void GenerateQrCode(string text)
    {
        int width = 256;
        int height = 256;

        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Height = height,
                Width = width,
                Margin = 1
            }
        };

        var pixelData = writer.Write(text);

        Color32[] colorArray = new Color32[width * height];
        byte[] raw = pixelData.Pixels;

        for (int i = 0; i < colorArray.Length; i++)
        {
            byte value = raw[i * 4]; // RGBA format
            colorArray[i] = new Color32(value, value, value, 255);
        }

        var texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
        texture.SetPixels32(colorArray);
        texture.Apply();

        qrCodeImage.texture = texture;
        qrCodeImage.gameObject.SetActive(true);
    }

    #endregion
}

// JSON serialization classes
[System.Serializable] public class DepositRequest { public float amount; public string playfabId; }
[System.Serializable] public class XummPayloadResponse { public string uuid; public string qrLink; }
[System.Serializable] public class LoginStatusResponse { public bool isLoggedIn; public string userAddress; public string sessionToken; }
