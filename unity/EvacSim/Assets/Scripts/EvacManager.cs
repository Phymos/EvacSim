using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI; // NavMeshAgent lives here

// Spawns agents, sends each to its nearest exit, tracks the evacuation.
public class EvacManager : MonoBehaviour
{
    public int agentCount = 60; // how many people to spawn

    const int MaxSpawnTries = 50;  // attempts per agent before giving up
    const float WallClearance = 0.5f;  // min meters from any wall
    const float AgentSpacing = 0.6f;   // min meters between agents

    LayoutLoader loader;
    readonly List<Vector3> spawnedAt = new List<Vector3>();
    public IReadOnlyList<Vector3> SpawnedPositions => spawnedAt; // for tests/debug
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

    // Random point strictly inside the building, snapped to the NavMesh.
    bool TrySpawn(string agentName)
    {
        for (int attempt = 0; attempt < MaxSpawnTries; attempt++)
        {
            Vector3 p = new Vector3(
                Random.Range(loader.InteriorMin.x, loader.InteriorMax.x), 0f,
                Random.Range(loader.InteriorMin.y, loader.InteriorMax.y));
            // Must be on walkable ground (0.5 m snap at most).
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 0.5f, NavMesh.AllAreas))
                continue;
            // Must keep clear of walls ...
            bool nearWall = false;
            foreach (Bounds wall in loader.WallBounds)
                if (Vector3.Distance(wall.ClosestPoint(hit.position), hit.position) < WallClearance)
                    nearWall = true;
            if (nearWall)
                continue;
            // ... and of already-spawned agents.
            bool crowded = false;
            foreach (Vector3 other in spawnedAt)
                if ((other - hit.position).sqrMagnitude < AgentSpacing * AgentSpacing)
                    crowded = true;
            if (crowded)
                continue;
            SpawnAt(agentName, hit.position);
            spawnedAt.Add(hit.position);
            return true;
        }
        return false; // unlucky too many times: skip this agent
    }

    void SpawnAt(string agentName, Vector3 feet)
    {
        // Capsule pivot is at its center, so lift it 1 m above the feet.
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = agentName;
        go.transform.parent = transform;
        go.transform.position = feet + Vector3.up * 1f;
        go.GetComponent<Renderer>().material = loader.AgentMaterial;

        NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
        agent.radius = 0.25f;
        agent.speed = 1.5f;
        agent.height = 2f;
        agent.baseOffset = -1f; // pivot is 1 m up; agent feet stay on ground
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        agent.destination = BestExit(feet);

        // Kinematic body: lets Exit triggers fire without fighting the agent.
        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        go.AddComponent<EvacAgent>().manager = this;
    }

    // Exit with the shortest WALKING path (not straight-line distance).
    Vector3 BestExit(Vector3 from)
    {
        Vector3 best = loader.ExitCenters[0];
        float bestLen = float.MaxValue;
        foreach (Vector3 exit in loader.ExitCenters)
        {
            NavMeshPath path = new NavMeshPath();
            if (NavMesh.CalculatePath(from, exit, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete)
            {
                float len = PathLength(path);
                if (len < bestLen)
                {
                    bestLen = len;
                    best = exit;
                }
            }
        }
        return best; // falls back to exit 0 if nothing routes (should not happen)
    }

    // Length of a path = sum of its corner-to-corner segments.
    static float PathLength(NavMeshPath path)
    {
        float len = 0f;
        for (int i = 1; i < path.corners.Length; i++)
            len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return len;
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
