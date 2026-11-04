using UnityEngine;
using System.Collections;


public class CarMovement : MonoBehaviour
{
    public GameObject myObject;
    public Vector3 direction;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        myObject.transform.position = direction;
    }

    // Update is called once per frame
    void Update()
    {
        myObject.transform.position += Vector3.right * Time.deltaTime;
    }
}
