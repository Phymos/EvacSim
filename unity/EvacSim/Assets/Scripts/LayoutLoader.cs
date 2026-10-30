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

    public List<Vector3> ExitCenters { get; private set; } // walkable exit points
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
        string path = Path.Combine(Application.streamingAssetsPath, "layout.json");
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
        WallBounds = new List<Bounds>();
        IntRect top = null, bottom = null, left = null, right = null; // outer walls
        for (int i = 0; i < layout.walls.Length; i++)
        {
            IntRect r = layout.walls[i];
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall " + i;
            wall.transform.parent = transform;
            wall.transform.localScale = new Vector3(r.w / PxPerMeter, WallHeight, r.h / PxPerMeter);
            wall.transform.position = new Vector3(
                (r.x + r.w / 2f) / PxPerMeter, WallHeight / 2f, -(r.y + r.h / 2f) / PxPerMeter);
            wall.GetComponent<Renderer>().material = wallMat;
            NoShadows(wall);
            WallBounds.Add(new Bounds(wall.transform.position, wall.transform.localScale));
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

        // Interior = inside the inner edges of the four outer walls.
        InteriorMin = new Vector2((left.x + left.w) / PxPerMeter, -bottom.y / PxPerMeter);
        InteriorMax = new Vector2(right.x / PxPerMeter, -(top.y + top.h) / PxPerMeter);

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
}
