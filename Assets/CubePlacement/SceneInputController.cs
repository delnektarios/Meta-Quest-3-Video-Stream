using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SceneInputController
/// Button A → spawn cube 0.6m in front of user
/// Button B → show save menu in front of user
/// </summary>
public class SceneInputController : MonoBehaviour
{
    [SerializeField] private InputActionReference _pressA;
    [SerializeField] private InputActionReference _pressB;
    [SerializeField] private GameObject cubePrefab;
    [SerializeField] private Vector3 spawnPosition = new Vector3(0, 1.20f, 0.6f);
    [SerializeField] private GameObject application_controls;

    private ARPlaneHashWithCubeStorage roomManager;
    private List<GameObject> spawnedCubes = new List<GameObject>();

    void Start()
    {
        roomManager = GetComponent<ARPlaneHashWithCubeStorage>();

        if (roomManager == null)
            Debug.LogError("ARPlaneHashWithCubeStorage component not found.");

        _pressA.action.performed += OnPressAAction;
        _pressB.action.performed += OnPressBAction;
        application_controls.SetActive(false);
    }

    private void OnPressAAction(InputAction.CallbackContext obj)
    {
        Debug.Log("Button A pressed — spawning cube");
        SpawnCube();
    }

    private void OnPressBAction(InputAction.CallbackContext obj)
    {
        Debug.Log("Button B pressed — showing menu");

        Vector3 playerPosition = Camera.main.transform.position;
        Vector3 controlsPosition = playerPosition + Camera.main.transform.forward * 1.0f;

        application_controls.transform.position = controlsPosition;
        application_controls.transform.LookAt(Camera.main.transform);
        application_controls.transform.Rotate(0, 180f, 0);
        application_controls.SetActive(true);
    }

    private void SpawnCube()
    {
        spawnPosition = Camera.main.transform.position;
        spawnPosition += Camera.main.transform.forward * 0.6f;
        GameObject newCube = Instantiate(cubePrefab, spawnPosition, Quaternion.identity);
        Debug.Log("Cube spawned at: " + newCube.transform.position);
        spawnedCubes.Add(newCube);
    }

    public void SaveAllCubes()
    {
        if (roomManager != null)
        {
            foreach (GameObject cube in spawnedCubes)
            {
                Vector3 position = cube.transform.position;
                Quaternion rotation = cube.transform.rotation;
                roomManager.OnPlaceCube(cube, position, rotation);
            }
            roomManager.OnSaveButtonPressed();
        }
    }

    private void OnDestroy()
    {
        _pressA.action.performed -= OnPressAAction;
        _pressB.action.performed -= OnPressBAction;
    }
}
