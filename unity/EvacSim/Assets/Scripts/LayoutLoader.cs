using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation; // AI Navigation package (NavMeshSurface)
using UnityEngine;
using UnityEngine.InputSystem;

// JSON classes must match layout.json keys exactly (JsonUtility maps by name).
[System.Serializable]
public class IntRect
{
    public int x, y, w, h; // pixels, origin top-left
}

[System.Serializable]
public class DoorData
{
    public int x, y, width; // parsed for schema fidelity; no objects built
}

[System.Serializable]
public class LayoutData
{
    public int[] image_size; // [width, height] in pixels
    public IntRect[] walls;
    public IntRect[] exits;
    public DoorData[] doors;
}

[System.Serializable]
public class ChangeGeom
{
    public int width;
    public int x, y, w, h;
}

[System.Serializable]
public class ChangeItem
{
    public string type;
    public string target_id;
    public ChangeGeom new_geometry;
    public string reason;
}

[System.Serializable]
public class ChangesData
{
    public ChangeItem[] changes;
    public string expected_effect;
}

// Builds the 3D floor plan from layout.json, then bakes the NavMesh.
public class LayoutLoader : MonoBehaviour
{
    const float PxPerMeter = 40f; // scale: 40 px = 1 m
    const float WallHeight = 3f;  // wall cubes are 3 m tall

    public static string NextLayout = "layout.json"; // set by L/R keys before reload

    public List<Vector3> ExitCenters { get; private set; } // walkable exit points
    public List<Vector3> DoorCenters { get; private set; } // door i in layout order
    public float MidX { get; private set; }
    public float CorridorZMin { get; private set; }
    public float CorridorZMax { get; private set; }

    // Room 1/2 top-left/right, Room 3/4 bottom-left/right, else Corridor.
    public string GetZone(Vector3 p)
    {
        if (p.z <= CorridorZMax && p.z >= CorridorZMin)
            return "Corridor";
        if (p.z > CorridorZMax)
            return p.x < MidX ? "Room 1" : "Room 2";
        return p.x < MidX ? "Room 3" : "Room 4";
    }
    public Vector2 BuildingSize { get; private set; }      // (width, depth) in meters
    public Vector3 BuildingCenter { get; private set; }
    public Vector2 InteriorMin { get; private set; } // inside-outer-walls rect,
    public Vector2 InteriorMax { get; private set; } // as world (x, z) corners
    public List<Bounds> WallBounds { get; private set; } // world-space wall boxes
    public Material AgentMaterial { get; private set; }  // shared agent color
    public bool IsBuilt { get; private set; }

    Material exitMat;
    Camera simCam;
    DoorData[] doors;
    ChangesData v2changes;
    float buildTime;

    void Start()
    {
        BuildLayout();
    }

    // Flat unlit material in one color (walls stay readable in any light).
    static Material Solid(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            Debug.LogError("URP Unlit shader not found!");
            shader = Shader.Find("Unlit/Color"); // last-resort fallback
        }
        return new Material(shader) { color = color };
    }

    // Flat look: this renderer neither casts nor receives shadows.
    static void NoShadows(GameObject go)
    {
        Renderer r = go.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    // Public + runs once, so EvacManager can force it first if needed.
    public void BuildLayout()
    {
        if (IsBuilt)
            return;

        // 1. Load the JSON from StreamingAssets (copied from the repo root).
        string path = Path.Combine(Application.streamingAssetsPath, NextLayout);
        if (!File.Exists(path))
        {
            Debug.LogError("Missing " + path + " (copy layout.json into StreamingAssets).");
            return;
        }
        LayoutData layout = JsonUtility.FromJson<LayoutData>(File.ReadAllText(path));
        IsBuilt = true;

        float imgW = layout.image_size[0] / PxPerMeter; // 1200 px -> 30 m
        float imgH = layout.image_size[1] / PxPerMeter; // 800 px -> 20 m
        BuildingSize = new Vector2(imgW, imgH);
        BuildingCenter = new Vector3(imgW / 2f, 0f, -imgH / 2f);

        // Flat lights: unlit materials ignore light anyway; this is belt-and-braces.
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (light.type == LightType.Directional)
                light.shadows = LightShadows.None;

        Material wallMat = Solid(new Color(0.16f, 0.16f, 0.20f));   // dark walls
        Material floorMat = Solid(new Color(0.83f, 0.83f, 0.85f));  // light floor
        exitMat = Solid(new Color(0.10f, 0.80f, 0.25f));   // green exits
        AgentMaterial = Solid(new Color(0.15f, 0.85f, 0.30f)); // green agents (red when stuck)

        // 2. Floor plane covering the whole image (a Unity Plane is 10x10 m).
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.parent = transform;
        floor.transform.position = BuildingCenter;
        floor.transform.localScale = new Vector3(imgW / 10f, 1f, imgH / 10f);
        floor.GetComponent<Renderer>().material = floorMat;
        NoShadows(floor);

        // 3. One thin cube per wall rect (pixel x -> world x, pixel y -> world -z).
        // Any overlap with an exit is cut out so the exit is a real opening.
        WallBounds = new List<Bounds>();
        IntRect top = null, bottom = null, left = null, right = null; // outer walls
        for (int i = 0; i < layout.walls.Length; i++)
        {
            IntRect r = layout.walls[i];
            List<IntRect> pieces = SubtractExits(r, layout.exits);
            for (int k = 0; k < pieces.Count; k++)
            {
                IntRect p = pieces[k];
                GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = pieces.Count > 1 ? "Wall " + i + "_" + k : "Wall " + i;
                wall.transform.parent = transform;
                wall.transform.localScale = new Vector3(p.w / PxPerMeter, WallHeight, p.h / PxPerMeter);
                wall.transform.position = new Vector3(
                    (p.x + p.w / 2f) / PxPerMeter, WallHeight / 2f, -(p.y + p.h / 2f) / PxPerMeter);
                wall.GetComponent<Renderer>().material = wallMat;
                NoShadows(wall);
                WallBounds.Add(new Bounds(wall.transform.position, wall.transform.localScale));
            }
            // Track the extreme runs: they frame the building footprint.
            if (r.w >= r.h) // horizontal run
            {
                if (top == null || r.y < top.y) top = r;
                if (bottom == null || r.y > bottom.y) bottom = r;
            }
            else // vertical run
            {
                if (left == null || r.x < left.x) left = r;
                if (right == null || r.x > right.x) right = r;
            }
        }
        // No door objects: the gaps between wall cubes ARE the doors.
        DoorCenters = new List<Vector3>();
        foreach (DoorData d in layout.doors)
            DoorCenters.Add(new Vector3(d.x / PxPerMeter, 0f, -d.y / PxPerMeter));
        doors = layout.doors;

        // Interior = inside the inner edges of the four outer walls.
        InteriorMin = new Vector2((left.x + left.w) / PxPerMeter, -bottom.y / PxPerMeter);
        InteriorMax = new Vector2(right.x / PxPerMeter, -(top.y + top.h) / PxPerMeter);

        // Corridor band = between the two middle horizontal wall rows.
        MidX = (InteriorMin.x + InteriorMax.x) / 2f;
        List<IntRect> horiz = new List<IntRect>();
        foreach (IntRect r in layout.walls)
            if (r.w >= r.h && r != top && r != bottom)
                horiz.Add(r);
        horiz.Sort((a, b) => a.y.CompareTo(b.y));
        if (horiz.Count >= 2)
        {
            int mid = horiz.Count / 2;
            int upperBot = int.MinValue, lowerTop = int.MaxValue;
            for (int i = 0; i < mid; i++)
                upperBot = Mathf.Max(upperBot, horiz[i].y + horiz[i].h);
            for (int i = mid; i < horiz.Count; i++)
                lowerTop = Mathf.Min(lowerTop, horiz[i].y);
            CorridorZMax = -upperBot / PxPerMeter;
            CorridorZMin = -lowerTop / PxPerMeter;
        }
        else // fallback: middle 20% of the interior depth
        {
            float span = InteriorMax.y - InteriorMin.y;
            CorridorZMax = InteriorMin.y + span * 0.6f;
            CorridorZMin = InteriorMin.y + span * 0.4f;
        }

        // 4. One trigger zone per exit rect, with a green pad so you can see it.
        ExitCenters = new List<Vector3>();
        for (int i = 0; i < layout.exits.Length; i++)
        {
            IntRect r = layout.exits[i];
            GameObject zone = new GameObject("Exit " + i);
            zone.tag = "Exit"; // add this tag under Project Settings > Tags and Layers
            zone.transform.parent = transform;
            zone.transform.position = new Vector3(
                (r.x + r.w / 2f) / PxPerMeter, 0f, -(r.y + r.h / 2f) / PxPerMeter);
            BoxCollider trigger = zone.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 1f, 0f);
            trigger.size = new Vector3(r.w / PxPerMeter, 2f, r.h / PxPerMeter);
            GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Pad";
            pad.transform.parent = zone.transform;
            pad.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            pad.transform.localScale = new Vector3(r.w / PxPerMeter, 0.12f, r.h / PxPerMeter);
            pad.GetComponent<Renderer>().material = exitMat;
            NoShadows(pad);
            Destroy(pad.GetComponent<BoxCollider>()); // pad is only paint, not physics
            ExitCenters.Add(new Vector3(zone.transform.position.x, 0f, zone.transform.position.z));
        }

        // 5. Tilted camera (~55 degrees) framing the whole building.
        Camera cam = Camera.main;
        if (cam == null)
            cam = new GameObject("TopDownCamera").AddComponent<Camera>();
        const float tilt = 55f;
        cam.transform.rotation = Quaternion.Euler(tilt, 0f, 0f);
        cam.transform.position = BuildingCenter - cam.transform.forward * 40f;
        cam.orthographic = true;
        // Fit the tilted footprint (ground depth + wall height) plus a margin.
        float rad = tilt * Mathf.Deg2Rad;
        float needH = imgH * Mathf.Sin(rad) + WallHeight * Mathf.Cos(rad) + 4f;
        cam.orthographicSize = Mathf.Max(needH / 2f, (imgW + 4f) / 2f / cam.aspect);
        simCam = cam;

        // 6. Bake the NavMesh at runtime from everything just built.
        gameObject.AddComponent<NavMeshSurface>().BuildNavMesh();

        buildTime = Time.time;
        if (NextLayout == "layout_v2.json")
            LoadV2Changes();
    }

    void Update()
    {
        if (exitMat != null)
        {
            float k = 0.5f + 0.5f * Mathf.Sin(Time.time * 3f);
            exitMat.color = Color.Lerp(new Color(0.08f, 0.65f, 0.2f), new Color(0.35f, 1f, 0.45f), k);
        }
        MoveCamera();
    }

    // WASD/arrows pan, Q/E or right-drag orbit, PgUp/PgDn or right-drag tilt, wheel or +/- zoom.
    void MoveCamera()
    {
        if (simCam == null)
            return;
        Vector2 pan = Vector2.zero;
        float zoomKey = 0f;
        float yawKey = 0f;
        float tiltKey = 0f;
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)
                pan.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)
                pan.y -= 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)
                pan.x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed)
                pan.x += 1f;
            if (kb.equalsKey.isPressed)
                zoomKey += 1f;
            if (kb.minusKey.isPressed)
                zoomKey -= 1f;
            if (kb.qKey.isPressed)
                yawKey -= 1f;
            if (kb.eKey.isPressed)
                yawKey += 1f;
            if (kb.pageUpKey.isPressed)
                tiltKey += 1f;
            if (kb.pageDownKey.isPressed)
                tiltKey -= 1f;
        }
        float wheel = 0f;
        float yawDrag = 0f;
        float tiltDrag = 0f;
        Mouse m = Mouse.current;
        if (m != null)
        {
            wheel = m.scroll.ReadValue().y / 120f;
            if (m.rightButton.isPressed)
            {
                Vector2 md = m.delta.ReadValue();
                yawDrag = -md.x * 0.3f;
                tiltDrag = -md.y * 0.25f;
            }
        }
        // Orbit/tilt around whatever the camera is looking at on the ground.
        Vector3 look = simCam.transform.position;
        float fy = simCam.transform.forward.y;
        if (Mathf.Abs(fy) > 0.01f)
            look += simCam.transform.forward * (-look.y / fy);
        float yaw = yawKey * 60f * Time.deltaTime + yawDrag;
        if (yaw != 0f)
            simCam.transform.RotateAround(look, Vector3.up, yaw);
        float tiltDelta = tiltKey * 40f * Time.deltaTime + tiltDrag;
        if (tiltDelta != 0f)
        {
            float cur = Mathf.Asin(Mathf.Clamp(-simCam.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float want = Mathf.Clamp(cur + tiltDelta, 20f, 85f);
            if (want != cur)
                simCam.transform.RotateAround(look, simCam.transform.right, want - cur);
        }
        Vector3 fwd = simCam.transform.forward;
        fwd.y = 0f;
        fwd.Normalize();
        Vector3 right = simCam.transform.right;
        right.y = 0f;
        right.Normalize();
        float panSpeed = simCam.orthographicSize * 1.2f;
        Vector3 p = simCam.transform.position + (fwd * pan.y + right * pan.x) * panSpeed * Time.deltaTime;
        p.x = Mathf.Clamp(p.x, BuildingCenter.x - 25f, BuildingCenter.x + 25f);
        p.z = Mathf.Clamp(p.z, BuildingCenter.z - 25f, BuildingCenter.z + 25f);
        simCam.transform.position = p;
        float size = simCam.orthographicSize - zoomKey * 12f * Time.deltaTime - wheel * 2f;
        simCam.orthographicSize = Mathf.Clamp(size, 4f, 40f);
    }

    void OnGUI()
    {
        if (v2changes == null || v2changes.changes == null || Time.time - buildTime > 10f)
            return;
        GUI.skin.label.fontSize = 18;
        GUI.skin.window.fontSize = 18;
        int h = 60 + (v2changes.changes.Length + 1) * 30;
        GUILayout.BeginArea(new Rect(10, Screen.height - h - 10, 780, h),
            "Spark proposal", GUI.skin.window);
        foreach (ChangeItem c in v2changes.changes)
            GUILayout.Label(c.type + " " + c.target_id + ": " + c.reason);
        GUILayout.Label("Expected: " + v2changes.expected_effect);
        GUILayout.EndArea();
    }

    void LoadV2Changes()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "layout_v2_changes.json");
        if (!File.Exists(path))
        {
            Debug.LogWarning("Missing " + path + " (no Spark reasons to show).");
            return;
        }
        v2changes = JsonUtility.FromJson<ChangesData>(File.ReadAllText(path));
        HighlightV2();
    }

    // Widened doors get a yellow disc, added exits a yellow rim.
    void HighlightV2()
    {
        if (v2changes == null || v2changes.changes == null)
            return;
        Material yellow = Solid(Color.yellow);
        foreach (ChangeItem c in v2changes.changes)
        {
            if (c.type == "widen_door" && c.target_id.StartsWith("door "))
            {
                int i;
                if (!int.TryParse(c.target_id.Substring(5), out i) || i < 0 || i >= DoorCenters.Count)
                    continue;
                float r = Mathf.Max(doors[i].width / PxPerMeter / 2f, 0.4f);
                GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                disc.name = "Widened " + i;
                disc.transform.parent = transform;
                disc.transform.position = new Vector3(DoorCenters[i].x, 0.1f, DoorCenters[i].z);
                disc.transform.localScale = new Vector3(r * 2f, 0.03f, r * 2f);
                disc.GetComponent<Renderer>().material = yellow;
                NoShadows(disc);
                Destroy(disc.GetComponent<CapsuleCollider>());
            }
            else if (c.type == "add_exit" && c.new_geometry != null)
            {
                ChangeGeom g = c.new_geometry;
                GameObject rim = GameObject.CreatePrimitive(PrimitiveType.Cube);
                rim.name = "NewExit";
                rim.transform.parent = transform;
                rim.transform.localScale = new Vector3(g.w / PxPerMeter + 0.4f, 0.1f, g.h / PxPerMeter + 0.4f);
                rim.transform.position = new Vector3(
                    (g.x + g.w / 2f) / PxPerMeter, 0.05f, -(g.y + g.h / 2f) / PxPerMeter);
                rim.GetComponent<Renderer>().material = yellow;
                NoShadows(rim);
                Destroy(rim.GetComponent<BoxCollider>());
            }
        }
    }

    static bool Overlaps(IntRect a, IntRect b)
    {
        return a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
    }

    // Wall rect minus every exit rect, as 0+ disjoint pixel rects.
    static List<IntRect> SubtractExits(IntRect wall, IntRect[] exits)
    {
        List<IntRect> rects = new List<IntRect> { wall };
        foreach (IntRect ex in exits)
        {
            List<IntRect> next = new List<IntRect>();
            foreach (IntRect rc in rects)
            {
                if (!Overlaps(rc, ex))
                {
                    next.Add(rc);
                    continue;
                }
                if (ex.x > rc.x)
                    next.Add(new IntRect { x = rc.x, y = rc.y, w = ex.x - rc.x, h = rc.h });
                if (ex.x + ex.w < rc.x + rc.w)
                    next.Add(new IntRect { x = ex.x + ex.w, y = rc.y, w = rc.x + rc.w - ex.x - ex.w, h = rc.h });
                int mx0 = Mathf.Max(rc.x, ex.x), mx1 = Mathf.Min(rc.x + rc.w, ex.x + ex.w);
                if (mx1 > mx0)
                {
                    if (ex.y > rc.y)
                        next.Add(new IntRect { x = mx0, y = rc.y, w = mx1 - mx0, h = ex.y - rc.y });
                    if (ex.y + ex.h < rc.y + rc.h)
                        next.Add(new IntRect { x = mx0, y = ex.y + ex.h, w = mx1 - mx0, h = rc.y + rc.h - ex.y - ex.h });
                }
            }
            rects = next;
        }
        rects.RemoveAll(q => q.w <= 0 || q.h <= 0);
        return rects;
    }
}
