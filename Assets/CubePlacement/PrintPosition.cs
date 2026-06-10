using UnityEngine;

public class PrintPosition : MonoBehaviour
{
    public void PrintCubePosition()
    {
        Debug.Log("Cube Position: " + transform.position);
    }
}
