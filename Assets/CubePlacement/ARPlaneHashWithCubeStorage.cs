using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// ARPlaneHashWithCubeStorage
/// Identifies a room by hashing AR plane IDs.
/// Saves and loads cube positions per room.
/// </summary>
public class ARPlaneHashWithCubeStorage : MonoBehaviour
{
    private ARPlaneManager arPlaneManager;
    private const string HashCountKey = "HashCount";
    private string roomDataFolderPath;
    private string currentRoomHash;
    public GameObject cubePrefab;
    private List<Transform> currentCubes = new List<Transform>();

    void Awake()
    {
        arPlaneManager = GetComponent<ARPlaneManager>();

        if (arPlaneManager == null)
            Debug.LogError("ARPlaneManager not found.");

        roomDataFolderPath = Path.Combine(Application.persistentDataPath, "RoomData");
        if (!Directory.Exists(roomDataFolderPath))
            Directory.CreateDirectory(roomDataFolderPath);
    }

    void Update()
    {
        if (arPlaneManager.trackables.count > 0 && currentRoomHash == null)
        {
            string concatenatedIds = ConcatenatePlaneIds();
            currentRoomHash = HashWithSHA256(concatenatedIds);

            if (IsHashAlreadyStored(currentRoomHash))
            {
                Debug.Log("Room recognized, loading cube data: " + currentRoomHash);
                LoadCubesForRoom(currentRoomHash);
            }
            else
            {
                Debug.Log("New room detected.");
                SaveHash(currentRoomHash);
            }
        }

        Vector3 playerPosition = Camera.main.transform.position;
        float scaleFactor = 0.1f;

        foreach (Transform cube in currentCubes)
        {
            if (cube == null) continue;

            Transform labelTransform = cube.Find("DistanceLabel");
            if (labelTransform != null)
            {
                TextMesh text = labelTransform.GetComponent<TextMesh>();
                if (text != null)
                {
                    float distance = Vector3.Distance(playerPosition, cube.position);
                    scaleFactor = Mathf.Clamp(0.1f / distance, 0.05f, 0.2f);
                    text.text = $"{distance:F2}m";
                }

                labelTransform.LookAt(playerPosition);
                labelTransform.Rotate(0, 180, 0);
                labelTransform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
            }
        }
    }

    private string ConcatenatePlaneIds()
    {
        StringBuilder concatenatedIds = new StringBuilder();
        foreach (var plane in arPlaneManager.trackables)
            concatenatedIds.Append(plane.trackableId.ToString());
        return concatenatedIds.ToString();
    }

    private string HashWithSHA256(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        using SHA256 sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(bytes);
        StringBuilder hashString = new StringBuilder();
        foreach (byte b in hashBytes)
            hashString.Append(b.ToString("x2"));
        return hashString.ToString();
    }

    private bool IsHashAlreadyStored(string hash)
    {
        int hashCount = PlayerPrefs.GetInt(HashCountKey, 0);
        for (int i = 0; i < hashCount; i++)
        {
            if (PlayerPrefs.GetString($"PlaneHash_{i}", "") == hash)
                return true;
        }
        return false;
    }

    private void SaveHash(string hash)
    {
        int hashCount = PlayerPrefs.GetInt(HashCountKey, 0);
        PlayerPrefs.SetString($"PlaneHash_{hashCount}", hash);
        PlayerPrefs.SetInt(HashCountKey, hashCount + 1);
        PlayerPrefs.Save();
    }

    public void OnSaveButtonPressed()
    {
        if (currentRoomHash != null)
        {
            SaveRoomData(currentRoomHash, currentCubes);
            Debug.Log("Cube data saved for room: " + currentRoomHash);
        }
    }

    private void SaveRoomData(string roomHash, List<Transform> cubeTransforms)
    {
        RoomData roomData = new RoomData
        {
            RoomID = roomHash,
            Cubes = CollectCubeData(cubeTransforms)
        };

        string filePath = Path.Combine(roomDataFolderPath, roomHash + ".json");
        File.WriteAllText(filePath, JsonUtility.ToJson(roomData));
    }

    private Transform FindClosestPlane(Vector3 position)
    {
        float minDistance = float.MaxValue;
        Transform closestPlane = null;

        foreach (var plane in arPlaneManager.trackables)
        {
            float distance = Vector3.Distance(position, plane.transform.position);
            if (distance < minDistance)
            {
                minDistance = distance;
                closestPlane = plane.transform;
            }
        }
        return closestPlane;
    }

    public void OnPlaceCube(GameObject cube, Vector3 position, Quaternion rotation)
    {
        cube.tag = "Cube";
        AddDistanceLabel(cube);

        Transform closestPlane = FindClosestPlane(position);
        if (closestPlane != null)
            cube.transform.SetParent(closestPlane);

        currentCubes.Add(cube.transform);
    }

    private void AddDistanceLabel(GameObject cube)
    {
        GameObject label = new GameObject("DistanceLabel");
        label.transform.SetParent(cube.transform);
        label.transform.localPosition = new Vector3(0, 0.9f, 0);
        label.transform.localScale = new Vector3(0.05f, 0.05f, 0.05f);

        TextMesh text = label.AddComponent<TextMesh>();
        text.text = "0m";
        text.fontSize = 30;
        text.alignment = TextAlignment.Center;
        text.anchor = TextAnchor.MiddleCenter;
        text.color = Color.black;
    }

    private List<CubeData> CollectCubeData(List<Transform> cubeTransforms)
    {
        List<CubeData> cubeDataList = new List<CubeData>();

        foreach (Transform cube in cubeTransforms)
        {
            if (cube == null) continue;

            string parentPlaneId = null;
            if (cube.parent != null)
            {
                ARPlane parentPlane = cube.parent.GetComponent<ARPlane>();
                if (parentPlane != null)
                    parentPlaneId = parentPlane.trackableId.ToString();
            }

            if (string.IsNullOrEmpty(parentPlaneId))
                parentPlaneId = "Unattached";

            cubeDataList.Add(new CubeData
            {
                position = cube.localPosition,
                rotation = cube.localRotation,
                parentPlaneId = parentPlaneId
            });
        }
        return cubeDataList;
    }

    private void LoadCubesForRoom(string roomHash)
    {
        string filePath = Path.Combine(roomDataFolderPath, roomHash + ".json");

        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            RoomData roomData = JsonUtility.FromJson<RoomData>(json);

            foreach (CubeData cubeData in roomData.Cubes)
            {
                GameObject newCube = Instantiate(cubePrefab);

                if (!string.IsNullOrEmpty(cubeData.parentPlaneId) &&
                    cubeData.parentPlaneId != "Unattached")
                {
                    Transform parentPlane = FindPlaneById(cubeData.parentPlaneId);
                    if (parentPlane != null)
                    {
                        newCube.transform.SetParent(parentPlane);
                        newCube.transform.localPosition = cubeData.position;
                        newCube.transform.localRotation = cubeData.rotation;
                    }
                    else
                    {
                        newCube.transform.position = cubeData.position;
                        newCube.transform.rotation = cubeData.rotation;
                    }
                }
                else
                {
                    newCube.transform.position = cubeData.position;
                    newCube.transform.rotation = cubeData.rotation;
                }

                newCube.tag = "Cube";
                currentCubes.Add(newCube.transform);
            }
        }
        else
        {
            Debug.Log("No saved cube data for this room.");
        }
    }

    private Transform FindPlaneById(string planeId)
    {
        foreach (var plane in arPlaneManager.trackables)
            if (plane.trackableId.ToString() == planeId)
                return plane.transform;
        return null;
    }

    public void OnResetButtonPressed()
    {
        if (!string.IsNullOrEmpty(currentRoomHash))
        {
            string filePath = Path.Combine(roomDataFolderPath, currentRoomHash + ".json");
            if (File.Exists(filePath))
                File.WriteAllText(filePath, string.Empty);
        }

        foreach (Transform cube in currentCubes)
            if (cube != null) Destroy(cube.gameObject);

        currentCubes.Clear();
        Debug.Log("Cubes reset for current room.");
    }
}

[System.Serializable]
public class CubeData
{
    public Vector3 position;
    public Quaternion rotation;
    public string parentPlaneId;
}

[System.Serializable]
public class RoomData
{
    public string RoomID;
    public List<CubeData> Cubes = new List<CubeData>();
}
