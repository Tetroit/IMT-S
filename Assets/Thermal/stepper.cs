using UnityEngine;

public class stepper : MonoBehaviour
{
    private float distance = 0.0005f;

    // Update is called once per frame
    void Update()
    {
        transform.Translate(0,0,distance);
    }
}
