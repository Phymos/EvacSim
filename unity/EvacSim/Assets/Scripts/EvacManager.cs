using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI; // NavMeshAgent lives here
using UnityEngine.SceneManagement;

// Spawns agents, sends each to its nearest exit, tracks the evacuation.
public class EvacManager : MonoBehaviour
{
    public int agentCount = 250; // how many people to spawn

    const int MaxSpawnTries = 50;  // attempts per agent before giving up
    const float WallClearance = 0.5f;  // min meters from any wall
    const float AgentSpacing = 0.6f;   // min meters between agents
    const float StuckSpeed = 0.3f;     // slower than this = stuck
    const float SampleInterval = 0.5f; // congestion sampling period
    const float BottleneckRadius = 2f; // stuck attributed within this range

    static readonly string[] Zones = { "Room 1", "Room 2", "Room 3", "Room 4", "Corridor" };

    LayoutLoader loader;
    readonly List<Vector3> spawnedAt = new List<Vector3>();
    public IReadOnlyList<Vector3> SpawnedPositions => spawnedAt; // for tests/debug
    int spawned, evacuated;
    float startTime, elapsed;
    bool finished;

    readonly List<GameObject> agents = new List<GameObject>();
    readonly Dictionary<GameObject, Vector3> lastPos = new Dictionary<GameObject, Vector3>();
    readonly Dictionary<string, int> zoneTotal = new Dictionary<string, int>();
    readonly Dictionary<string, int> zoneStuck = new Dictionary<string, int>();
    readonly Dictionary<string, float> waitTotal = new Dictionary<string, float>();
    readonly Dictionary<string, HashSet<int>> waitAgents = new Dictionary<string, HashSet<int>>();
    float sampleTimer;

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

        foreach (string z in Zones)
        {
            zoneTotal[z] = 0;
            zoneStuck[z] = 0;
        }
        for (int i = 0; i < loader.DoorCenters.Count; i++)
            ResetBottleneck("door " + i);
        for (int i = 0; i < loader.ExitCenters.Count; i++)
            ResetBottleneck("exit " + i);

        for (int i = 0; i < agentCount; i++)
            if (TrySpawn("Person " + i))
                spawned++;
        startTime = Time.time;
    }

    void ResetBottleneck(string id)
    {
        waitTotal[id] = 0f;
        waitAgents[id] = new HashSet<int>();
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
        agent.radius = 0.3f;
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
        agents.Add(go);
        lastPos[go] = go.transform.position;
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
        agents.Remove(agent);
        lastPos.Remove(agent);
        Destroy(agent);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.L))
        {
            LayoutLoader.NextLayout = "layout_v2.json";
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (Input.GetKeyDown(KeyCode.R))
        {
            LayoutLoader.NextLayout = "layout_tight.json";
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (finished)
            return;
        elapsed = Time.time - startTime;
        SampleCongestion();
        if (spawned > 0 && evacuated >= spawned)
        {
            finished = true;
            WriteResults();
            Debug.Log("Evacuation complete: " + evacuated + " agents in " + elapsed.ToString("F1") + " s");
        }
    }

    void SampleCongestion()
    {
        sampleTimer += Time.deltaTime;
        if (sampleTimer < SampleInterval || agents.Count == 0)
            return;
        float dt = sampleTimer;
        sampleTimer = 0f;
        // Iterate over a copy: agents can be destroyed mid-sample.
        foreach (GameObject go in new List<GameObject>(agents))
        {
            if (go == null)
                continue;
            Vector3 pos = go.transform.position;
            Vector3 prev;
            if (!lastPos.TryGetValue(go, out prev))
            {
                lastPos[go] = pos;
                continue;
            }
            float speed = Vector3.Distance(pos, prev) / Mathf.Max(dt, 0.001f);
            lastPos[go] = pos;
            string zone = loader.GetZone(pos);
            zoneTotal[zone]++;
            if (speed >= StuckSpeed)
                continue;
            zoneStuck[zone]++;
            string near = NearestBottleneck(pos);
            if (near != null)
            {
                waitTotal[near] += dt;
                waitAgents[near].Add(go.GetInstanceID());
            }
        }
    }

    string NearestBottleneck(Vector3 pos)
    {
        string best = null;
        float bestD = BottleneckRadius;
        for (int i = 0; i < loader.DoorCenters.Count; i++)
        {
            float d = Vector3.Distance(pos, loader.DoorCenters[i]);
            if (d < bestD)
            {
                bestD = d;
                best = "door " + i;
            }
        }
        for (int i = 0; i < loader.ExitCenters.Count; i++)
        {
            float d = Vector3.Distance(pos, loader.ExitCenters[i]);
            if (d < bestD)
            {
                bestD = d;
                best = "exit " + i;
            }
        }
        return best;
    }

    void WriteResults()
    {
        List<KeyValuePair<string, float>> ranked = new List<KeyValuePair<string, float>>();
        foreach (KeyValuePair<string, float> kv in waitTotal)
        {
            int n = Mathf.Max(1, waitAgents[kv.Key].Count);
            ranked.Add(new KeyValuePair<string, float>(kv.Key, kv.Value / n));
        }
        ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"evacuation_time_s\": ");
        sb.Append(elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\n  \"congestion_by_zone\": {");
        for (int i = 0; i < Zones.Length; i++)
        {
            float c = zoneTotal[Zones[i]] > 0
                ? (float)zoneStuck[Zones[i]] / zoneTotal[Zones[i]]
                : 0f;
            if (i > 0)
                sb.Append(",");
            sb.Append("\n    \"");
            sb.Append(Zones[i]);
            sb.Append("\": ");
            sb.Append(c.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append("\n  },\n  \"bottlenecks\": [");
        for (int i = 0; i < Mathf.Min(3, ranked.Count); i++)
        {
            if (i > 0)
                sb.Append(",");
            sb.Append("\n    {\"rank\": ");
            sb.Append(i + 1);
            sb.Append(", \"id\": \"");
            sb.Append(ranked[i].Key);
            sb.Append("\", \"avg_wait_s\": ");
            sb.Append(ranked[i].Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("}");
        }
        sb.Append("\n  ]\n}\n");
        string path = Path.Combine(Application.streamingAssetsPath, "results.json");
        File.WriteAllText(path, sb.ToString());
        Debug.Log("Saved " + path);
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 240, 130), "Evacuation", GUI.skin.window);
        GUILayout.Label("Time: " + elapsed.ToString("F1") + " s");
        GUILayout.Label("Evacuated: " + evacuated);
        GUILayout.Label("Remaining: " + (spawned - evacuated));
        GUILayout.Label("[L] layout_v2  [R] layout_tight");
        GUILayout.EndArea();
    }
}
