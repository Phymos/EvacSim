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
    public bool IsBuilt { get; private set; }

    void Start()
    {
        BuildLayout();
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

        // 2. Floor plane covering the whole image (a Unity Plane is 10x10 m).
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.parent = transform;
        floor.transform.position = BuildingCenter;
        floor.transform.localScale = new Vector3(imgW / 10f, 1f, imgH / 10f);

        // 3. One thin cube per wall rect (pixel x -> world x, pixel y -> world -z).
        for (int i = 0; i < layout.walls.Length; i++)
        {
            IntRect r = layout.walls[i];
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall " + i;
            wall.transform.parent = transform;
            wall.transform.localScale = new Vector3(r.w / PxPerMeter, WallHeight, r.h / PxPerMeter);
            wall.transform.position = new Vector3(
                (r.x + r.w / 2f) / PxPerMeter, WallHeight / 2f, -(r.y + r.h / 2f) / PxPerMeter);
        }
        // No door objects: the gaps between wall cubes ARE the doors.

        // 4. One invisible trigger zone per exit rect.
        ExitCenters = new List<Vector3>();
        for (int i = 0; i < layout.exits.Length; i++)
        {
            IntRect r = layout.exits[i];
            GameObject zone = new GameObject("Exit " + i);
            zone.tag = "Exit"; // add this tag under Project Settings > Tags and Layers
            zone.transform.parent = transform;
            zone.transform.position = new Vector3(
                (r.x + r.w / 2f) / PxPerMeter, 1f, -(r.y + r.h / 2f) / PxPerMeter);
            BoxCollider trigger = zone.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(r.w / PxPerMeter, 2f, r.h / PxPerMeter);
            ExitCenters.Add(new Vector3(zone.transform.position.x, 0f, zone.transform.position.z));
        }

        // 5. Top-down camera framing the building (reuses Main Camera if any).
        Camera cam = Camera.main;
        if (cam == null)
            cam = new GameObject("TopDownCamera").AddComponent<Camera>();
        cam.transform.position = BuildingCenter + Vector3.up * 40f;
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        cam.orthographic = true;
        // Fit width and height for any aspect ratio, plus a small margin.
        cam.orthographicSize = Mathf.Max(imgH / 2f + 2f, (imgW / 2f + 2f) / cam.aspect);

        // 6. Bake the NavMesh at runtime from everything just built.
        gameObject.AddComponent<NavMeshSurface>().BuildNavMesh();
    }
}
