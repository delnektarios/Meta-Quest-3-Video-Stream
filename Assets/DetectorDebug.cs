using UnityEngine;

public class DetectorDebug : MonoBehaviour
{
    public QuestImageDetector_directCameraAccess Detector;
    public UnityEngine.UI.RawImage SourceRawImage;

    void Update()
    {
        if (SourceRawImage != null)
        {
            Debug.Log($"[DEBUG] Passthrough texture: " +
                      $"{(SourceRawImage.texture != null ? SourceRawImage.texture.width + "x" + SourceRawImage.texture.height : "NULL")}");
        }
    }
}