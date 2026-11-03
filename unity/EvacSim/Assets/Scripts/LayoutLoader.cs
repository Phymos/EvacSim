using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation; // AI Navigation package (NavMeshSurface)
using UnityEngine;

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
        Material exitMat = Solid(new Color(0.10f, 0.80f, 0.25f));   // green exits
        AgentMaterial = Solid(new Color(1f, 0.40f, 0.10f));         // orange agents

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

        // 5. Top-down camera (90 degrees) looking at the building center.
        Camera cam = Camera.main;
        if (cam == null)
            cam = new GameObject("TopDownCamera").AddComponent<Camera>();
        const float tilt = 90f;
        cam.transform.rotation = Quaternion.Euler(tilt, 0f, 0f);
        cam.transform.position = BuildingCenter - cam.transform.forward * 40f;
        cam.orthographic = true;
        // Fit the tilted footprint (ground depth + wall height) plus a margin.
        float rad = tilt * Mathf.Deg2Rad;
        float needH = imgH * Mathf.Sin(rad) + WallHeight * Mathf.Cos(rad) + 4f;
        cam.orthographicSize = Mathf.Max(needH / 2f, (imgW + 4f) / 2f / cam.aspect);

        // 6. Bake the NavMesh at runtime from everything just built.
        gameObject.AddComponent<NavMeshSurface>().BuildNavMesh();
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
