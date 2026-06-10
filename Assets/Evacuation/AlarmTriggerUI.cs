using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// AlarmTriggerUI
///
/// Temporary test UI for triggering the evacuation alarm.
/// Shows:
///   - QR scan status
///   - Trigger alarm button
///   - Stop evacuation button
///
/// SETUP:
///   Attach to a World Space Canvas with:
///   ├── StatusLabel (TMP)
///   ├── TriggerAlarmButton (Button)
///   ├── StopButton (Button)
///   └── RescanButton (Button)
///
/// Replace with LLM-based remote trigger later.
/// </summary>
public class AlarmTriggerUI : MonoBehaviour
{
    [Header("References")]
    public EvacuationManager EvacuationManager;
    public EvacuationQRScanner QRScanner;

    [Header("UI")]
    public TextMeshProUGUI StatusLabel;
    public Button TriggerAlarmButton;
    public Button StopButton;
    public Button RescanButton;

    private void Start()
    {
        if (TriggerAlarmButton != null)
            TriggerAlarmButton.onClick.AddListener(OnTriggerAlarm);

        if (StopButton != null)
            StopButton.onClick.AddListener(OnStop);

        if (RescanButton != null)
            RescanButton.onClick.AddListener(OnRescan);

        UpdateStatus("Waiting for QR scan...");

        // Listen to QR events
        if (QRScanner != null)
        {
            QRScanner.OnQRDetected.AddListener(OnQRDetected);
            QRScanner.OnQRInvalid.AddListener(OnQRInvalid);
        }

        if (EvacuationManager != null)
        {
            EvacuationManager.OnAlarmTriggered.AddListener(() =>
                UpdateStatus("🚨 EVACUATION IN PROGRESS"));
            EvacuationManager.OnEvacuationComplete.AddListener(() =>
                UpdateStatus("✅ Evacuation complete!"));
            EvacuationManager.OnQRNotScanned.AddListener(() =>
                UpdateStatus("⚠️ Please scan QR first!"));
        }
    }

    private void OnTriggerAlarm()
    {
        if (EvacuationManager != null)
            EvacuationManager.TriggerAlarm();
    }

    private void OnStop()
    {
        if (EvacuationManager != null)
            EvacuationManager.StopEvacuation();
        UpdateStatus("Evacuation stopped.");
    }

    private void OnRescan()
    {
        if (QRScanner != null)
            QRScanner.StartScanning();
        UpdateStatus("Scanning for QR...");
    }

    private void OnQRDetected()
    {
        UpdateStatus("✅ QR Scanned! Ready for alarm.");
    }

    private void OnQRInvalid()
    {
        UpdateStatus("⚠️ Invalid QR. Try again.");
    }

    private void UpdateStatus(string msg)
    {
        if (StatusLabel != null)
            StatusLabel.text = msg;
        Debug.Log($"[AlarmUI] {msg}");
    }
}
