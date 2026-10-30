using UnityEngine;
using UnityEngine.AI; // NavMeshAgent lives here

// Spawns agents, sends each to its nearest exit, tracks the evacuation.
public class EvacManager : MonoBehaviour
{
    public int agentCount = 60; // how many people to spawn

    LayoutLoader loader;
    int spawned, evacuated;
    float startTime, elapsed;
    bool finished;

    void Start()
    {
        // Layout (and its NavMesh) must exist before agents spawn.
        loader = FindFirstObjectByType<LayoutLoader>();
        if (loader == null)
        {
            Debug.LogError("No LayoutLoader in the scene.");
            enabled = false;
            return;
        }
        loader.BuildLayout(); // runs once; no-op if its Start() built already
        if (!loader.IsBuilt)
        {
            enabled = false; // LayoutLoader already logged the reason
            return;
        }

        for (int i = 0; i < agentCount; i++)
            if (TrySpawn("Person " + i))
                spawned++;
        startTime = Time.time;
    }

    // Random point in the building, snapped to the NavMesh (= walkable).
    bool TrySpawn(string agentName)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Vector3 p = new Vector3(
                Random.Range(0f, loader.BuildingSize.x), 0f,
                -Random.Range(0f, loader.BuildingSize.y));
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 1f, NavMesh.AllAreas))
                continue;
            SpawnAt(agentName, hit.position);
            return true;
        }
        return false; // unlucky 30 times: skip this agent
    }

    void SpawnAt(string agentName, Vector3 feet)
    {
        // Capsule pivot is at its center, so lift it 1 m above the feet.
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = agentName;
        go.transform.parent = transform;
        go.transform.position = feet + Vector3.up * 1f;

        NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
        agent.radius = 0.25f;
        agent.speed = 1.5f;
        agent.height = 2f;
        agent.baseOffset = -1f; // pivot is 1 m up; agent feet stay on ground
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        agent.destination = NearestExit(feet);

        // Kinematic body: lets Exit triggers fire without fighting the agent.
        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        go.AddComponent<EvacAgent>().manager = this;
    }

    Vector3 NearestExit(Vector3 from)
    {
        Vector3 best = loader.ExitCenters[0];
        foreach (Vector3 exit in loader.ExitCenters)
            if ((exit - from).sqrMagnitude < (best - from).sqrMagnitude)
                best = exit;
        return best;
    }

    // Called by EvacAgent when it walks into an Exit trigger.
    public void Evacuate(GameObject agent)
    {
        evacuated++;
        Destroy(agent);
    }

    void Update()
    {
        if (finished)
            return;
        elapsed = Time.time - startTime;
        if (spawned > 0 && evacuated >= spawned)
        {
            finished = true;
            Debug.Log("Evacuation complete: " + evacuated + " agents in " + elapsed.ToString("F1") + " s");
        }
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 220, 90), "Evacuation", GUI.skin.window);
        GUILayout.Label("Time: " + elapsed.ToString("F1") + " s");
        GUILayout.Label("Evacuated: " + evacuated);
        GUILayout.Label("Remaining: " + (spawned - evacuated));
        GUILayout.EndArea();
    }
}
