using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI; // NavMeshAgent lives here
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Spawns agents, sends each to its nearest exit, tracks the evacuation.
[System.Serializable]
public class RunConfig
{
    public int seed = 42; // written by loop.py before each run
}

public class EvacManager : MonoBehaviour
{
    public int agentCount = 300; // how many people to spawn
    public int spawnSeed = 42;   // default seed; run_config.json overrides per run
    public float speedVar = 0f;  // agent speed variety, fraction (0 = off, 0.2 = +/-20%)
    public float maxRunTime = 300f; // auto-finish failsafe so the loop never hangs

    static bool autoMode; // Key A: loop layout_bad -> layout_next -> ...
    static readonly List<float> roundTimes = new List<float>();
    float pollTimer;
    float autoWait;
    string autoStatus = "waiting for layout_next.json (is loop.py running?)";
    int currentSeed;
    bool timedOut;

    const int MaxSpawnTries = 80;  // attempts per agent before giving up
    const float WallClearance = 0.5f;  // min meters from any wall
    const float AgentSpacing = 0.8f;   // min meters between agents
    const float GracePeriod = 4f;      // no stuck/congestion tracking right after spawn
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
    readonly Dictionary<string, HashSet<GameObject>> waitAgents = new Dictionary<string, HashSet<GameObject>>();
    float sampleTimer;

    readonly Dictionary<GameObject, float> stuckTime = new Dictionary<GameObject, float>();
    readonly Dictionary<GameObject, Material> agentMats = new Dictionary<GameObject, Material>();
    int[,] heat;
    Renderer[,] heatCells;
    Material[] heatMats;
    GameObject heatRoot;
    int heatNX, heatNZ;
    bool showHeat;
    static readonly Dictionary<string, float> sessionTimes = new Dictionary<string, float>();

    static string LabelFor(string file)
    {
        if (file == "layout_bad.json")
            return "bad";
        if (file == "layout_tight.json")
            return "tight";
        if (file == "layout_v2.json")
            return "AI v2";
        if (file.StartsWith("layout_auto_"))
            return "auto";
        if (file == "layout_next.json")
            return "next";
        return "base";
    }

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

        BuildHeatmap();

        currentSeed = spawnSeed;
        string cfg = Path.Combine(Application.streamingAssetsPath, "run_config.json");
        if (File.Exists(cfg))
        {
            try { currentSeed = JsonUtility.FromJson<RunConfig>(File.ReadAllText(cfg)).seed; }
            catch { currentSeed = spawnSeed; }
        }
        UnityEngine.Random.InitState(currentSeed);
        for (int i = 0; i < agentCount; i++)
            if (TrySpawn("Person " + i))
                spawned++;
        startTime = Time.time;
        WriteStatus("running");
    }

    void ResetBottleneck(string id)
    {
        waitTotal[id] = 0f;
        waitAgents[id] = new HashSet<GameObject>();
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
        Material mat = new Material(loader.AgentMaterial);
        go.GetComponent<Renderer>().material = mat;
        agentMats[go] = mat;
        stuckTime[go] = 0f;

        NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
        agent.radius = 0.3f;
        agent.speed = speedVar > 0f
            ? 1.5f * (1f + UnityEngine.Random.Range(-speedVar, speedVar))
            : 1.5f;
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
        stuckTime.Remove(agent);
        agentMats.Remove(agent);
        Destroy(agent);
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.oKey.wasPressedThisFrame)
        {
            autoMode = true;
            roundTimes.Clear();
            LayoutLoader.NextLayout = "layout_bad.json";
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (kb != null && kb.lKey.wasPressedThisFrame)
        {
            autoMode = false;
            LayoutLoader.NextLayout = "layout_v2.json";
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (kb != null && kb.rKey.wasPressedThisFrame)
        {
            autoMode = false;
            LayoutLoader.NextLayout = "layout_tight.json";
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (kb != null && kb.bKey.wasPressedThisFrame)
        {
            autoMode = false;
            LayoutLoader.NextLayout = "layout_bad.json";
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (kb != null && kb.hKey.wasPressedThisFrame)
            SetHeat(!showHeat);
        if (finished)
        {
            if (autoMode)
                PollNextLayout();
            return;
        }
        elapsed = Time.time - startTime;
        SampleCongestion();
        if (spawned > 0 && evacuated >= spawned)
            FinishRun(false);
        else if (elapsed > maxRunTime)
            FinishRun(true);
    }

    void SampleCongestion()
    {
        if (elapsed < GracePeriod)
            return;
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
            HeatAdd(pos);
            string zone = loader.GetZone(pos);
            zoneTotal[zone]++;
            if (speed >= StuckSpeed)
            {
                stuckTime[go] = 0f;
                SetTint(agentMats[go], ColorForStuck(0f));
                continue;
            }
            stuckTime[go] += dt;
            SetTint(agentMats[go], ColorForStuck(stuckTime[go]));
            zoneStuck[zone]++;
            string near = NearestBottleneck(pos);
            if (near != null)
            {
                waitTotal[near] += dt;
                waitAgents[near].Add(go);
            }
        }
        for (int ix = 0; ix < heatNX; ix++)
            for (int iz = 0; iz < heatNZ; iz++)
                heat[ix, iz] = (int)(heat[ix, iz] * 0.99f);
        UpdateHeatColors();
    }

    // Green when flowing, through yellow to red after ~8 s stuck.
    static Color ColorForStuck(float t)
    {
        float k = Mathf.Clamp01(t / 8f);
        if (k < 0.5f)
            return Color.Lerp(new Color(0.15f, 0.85f, 0.3f), Color.yellow, k * 2f);
        return Color.Lerp(Color.yellow, Color.red, (k - 0.5f) * 2f);
    }

    static void SetTint(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", c);
        else
            m.color = c;
    }

    // Normalized heat 0..1 -> transparent -> yellow -> orange -> red, max 55% opaque.
    static Color HeatColor(float t)
    {
        t = Mathf.Clamp01(t);
        Color c = t < 0.5f
            ? Color.Lerp(Color.yellow, new Color(1f, 0.5f, 0f), t * 2f)
            : Color.Lerp(new Color(1f, 0.5f, 0f), Color.red, (t - 0.5f) * 2f);
        c.a = Mathf.Min(0.55f, t);
        return c;
    }

    static Material HeatMaterial(Color c)
    {
        Shader shader = Shader.Find("EvacSim/HeatCell");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        Material m = new Material(shader);
        SetTint(m, c);
        if (shader.name != "EvacSim/HeatCell")
        {
            // Fallback: force the stock shader transparent.
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        return m;
    }

    // One 1 m quad per cell over the building footprint, hidden until H.
    void BuildHeatmap()
    {
        heatNX = Mathf.CeilToInt(loader.BuildingSize.x);
        heatNZ = Mathf.CeilToInt(loader.BuildingSize.y);
        heat = new int[heatNX, heatNZ];
        heatCells = new Renderer[heatNX, heatNZ];
        heatMats = new Material[10];
        for (int i = 0; i < 10; i++)
            heatMats[i] = HeatMaterial(HeatColor(i / 9f));
        heatRoot = new GameObject("Heatmap");
        heatRoot.transform.parent = transform;
        for (int ix = 0; ix < heatNX; ix++)
            for (int iz = 0; iz < heatNZ; iz++)
            {
                GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                q.name = "H";
                q.transform.parent = heatRoot.transform;
                q.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                q.transform.position = new Vector3(ix + 0.5f, 0.02f, -(iz + 0.5f));
                Destroy(q.GetComponent<MeshCollider>());
                Renderer r = q.GetComponent<Renderer>();
                r.sharedMaterial = heatMats[0];
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = false;
                heatCells[ix, iz] = r;
            }
        heatRoot.SetActive(false);
    }

    void HeatAdd(Vector3 pos)
    {
        int ix = Mathf.Clamp(Mathf.FloorToInt(pos.x), 0, heatNX - 1);
        int iz = Mathf.Clamp(Mathf.FloorToInt(-pos.z), 0, heatNZ - 1);
        heat[ix, iz] = Mathf.Min(heat[ix, iz] + 1, 1000);
    }

    void UpdateHeatColors()
    {
        int max = 1;
        for (int ix = 0; ix < heatNX; ix++)
            for (int iz = 0; iz < heatNZ; iz++)
                if (heat[ix, iz] > max)
                    max = heat[ix, iz];
        for (int ix = 0; ix < heatNX; ix++)
            for (int iz = 0; iz < heatNZ; iz++)
            {
                int c = heat[ix, iz];
                Renderer r = heatCells[ix, iz];
                if (c <= 0)
                {
                    r.enabled = false;
                    continue;
                }
                r.enabled = true;
                r.sharedMaterial = heatMats[Mathf.Clamp(Mathf.CeilToInt(9f * c / max), 1, 9)];
            }
    }

    void SetHeat(bool on)
    {
        showHeat = on;
        if (on)
            UpdateHeatColors();
        else if (heatCells != null)
        {
            for (int ix = 0; ix < heatNX; ix++)
                for (int iz = 0; iz < heatNZ; iz++)
                    heatCells[ix, iz].enabled = false;
        }
        if (heatRoot != null)
            heatRoot.SetActive(on);
        Debug.Log("Heatmap " + (on ? "ON" : "OFF"));
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

    void FinishRun(bool timeout)
    {
        finished = true;
        timedOut = timeout;
        autoWait = 0f;
        if (autoMode)
            roundTimes.Add(elapsed);
        sessionTimes[LabelFor(LayoutLoader.NextLayout)] = elapsed;
        WriteResults();
        SaveHeatmapPng();
        WriteStatus("done");
        Debug.Log(timeout
            ? "TIMEOUT: " + evacuated + "/" + spawned + " evacuated in " + elapsed.ToString("F1") + " s"
            : "Evacuation complete: " + evacuated + " agents in " + elapsed.ToString("F1") + " s");
    }

    void WriteStatus(string state)
    {
        string p = Path.Combine(Application.streamingAssetsPath, "status.json");
        File.WriteAllText(p, "{\"state\": \"" + state + "\", \"layout\": \""
            + LayoutLoader.NextLayout + "\", \"seed\": " + currentSeed + "}\n");
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
        sb.Append(",\n  \"timed_out\": ");
        sb.Append(timedOut ? "true" : "false");
        sb.Append(",\n  \"agents_evacuated\": ");
        sb.Append(evacuated);
        sb.Append(",\n  \"agents_total\": ");
        sb.Append(spawned);
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

    // Cheap top-down heat image for the Python loop (white -> yellow -> red).
    void SaveHeatmapPng()
    {
        if (heat == null || heatNX <= 0 || heatNZ <= 0)
            return;
        int max = 1;
        for (int ix = 0; ix < heatNX; ix++)
            for (int iz = 0; iz < heatNZ; iz++)
                if (heat[ix, iz] > max)
                    max = heat[ix, iz];
        Texture2D tex = new Texture2D(heatNX, heatNZ, TextureFormat.RGB24, false);
        for (int ix = 0; ix < heatNX; ix++)
            for (int iz = 0; iz < heatNZ; iz++)
            {
                Color hc = HeatColor((float)heat[ix, iz] / max);
                tex.SetPixel(ix, iz, new Color(
                    hc.r * hc.a + (1f - hc.a),
                    hc.g * hc.a + (1f - hc.a),
                    hc.b * hc.a + (1f - hc.a)));
            }
        tex.Apply();
        string png = Path.Combine(Application.streamingAssetsPath, "heatmap.png");
        File.WriteAllBytes(png, tex.EncodeToPNG());
        Destroy(tex);
        Debug.Log("Saved " + png);
    }

    // Auto mode: every 0.5 s, pick up a layout_next.json newer than results.json.
    void PollNextLayout()
    {
        pollTimer += Time.deltaTime;
        autoWait += Time.deltaTime;
        if (pollTimer < 0.5f)
            return;
        pollTimer = 0f;
        string dir = Application.streamingAssetsPath;
        string next = Path.Combine(dir, "layout_next.json");
        string res = Path.Combine(dir, "results.json");
        if (!File.Exists(next) || !File.Exists(res))
        {
            autoStatus = "waiting " + (int)autoWait + " s for layout_next.json (is loop.py running?)";
            return;
        }
        if (File.GetLastWriteTime(next) < File.GetLastWriteTime(res))
        {
            autoStatus = "layout_next.json is older than results.json (stale)";
            return;
        }
        autoStatus = "found, loading...";
        string auto = "layout_auto_" + roundTimes.Count + ".json";
        File.Copy(next, Path.Combine(dir, auto), true);
        File.Delete(next);
        LayoutLoader.NextLayout = auto;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    void OnGUI()
    {
        GUI.skin.label.fontSize = 20;
        GUI.skin.window.fontSize = 20;
        GUILayout.BeginArea(new Rect(10, 10, 340, 300), "Evacuation", GUI.skin.window);
        GUILayout.Label("Layout: " + LabelFor(LayoutLoader.NextLayout));
        GUILayout.Label("Seed: " + currentSeed);
        GUILayout.Label("Time: " + elapsed.ToString("F1") + " s");
        GUILayout.Label("Evacuated: " + evacuated + " / " + spawned);
        GUILayout.Label("Remaining: " + (spawned - evacuated));
        GUILayout.Label("[O] auto [L] v2 [R] tight [B] bad");
        GUILayout.Label("[H] heat: " + (showHeat ? "ON" : "off") + " | WASD pan, QE orbit, drag tilt, wheel zoom");
        GUILayout.EndArea();

        if (autoMode && roundTimes.Count > 0)
        {
            float t0 = roundTimes[0];
            GUILayout.BeginArea(new Rect(10, 320, 460, 120 + roundTimes.Count * 30),
                "Rounds", GUI.skin.window);
            for (int i = 0; i < roundTimes.Count; i++)
            {
                string s = "Round " + i + ": " + roundTimes[i].ToString("F1") + " s";
                if (i > 0 && t0 > 0f)
                {
                    float pct = (t0 - roundTimes[i]) / t0 * 100f;
                    s += " (" + (pct >= 0f ? "-" : "+") + Mathf.Abs(pct).ToString("F0") + "% vs r0)";
                }
                GUILayout.Label(s);
            }
            float bestT = Mathf.Min(roundTimes.ToArray());
            GUILayout.Label("Best: Round " + roundTimes.IndexOf(bestT) + ": " + bestT.ToString("F1") + " s");
            GUILayout.Label("Auto: " + (finished ? autoStatus : "running..."));
            GUILayout.EndArea();
        }

        if (!autoMode && sessionTimes.Count > 0)
        {
            GUILayout.BeginArea(new Rect(10, 320, 340, 60 + sessionTimes.Count * 30),
                "Runs", GUI.skin.window);
            float best = float.MaxValue, worst = 0f;
            foreach (KeyValuePair<string, float> kv in sessionTimes)
            {
                GUILayout.Label(kv.Key + ": " + kv.Value.ToString("F1") + " s");
                best = Mathf.Min(best, kv.Value);
                worst = Mathf.Max(worst, kv.Value);
            }
            if (sessionTimes.Count > 1 && worst > 0f)
                GUILayout.Label("Improvement: " + ((worst - best) / worst * 100f).ToString("F0") + "%");
            GUILayout.EndArea();
        }
    }
}
