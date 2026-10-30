using UnityEngine;

// Lives on each agent; reports Exit trigger touches to the manager.
public class EvacAgent : MonoBehaviour
{
    public EvacManager manager;

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Exit"))
            manager.Evacuate(gameObject);
    }
}
