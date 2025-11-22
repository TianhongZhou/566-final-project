using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MaterialMapPainter))]
public class MaterialMapPainterEditor : Editor
{
    MaterialMapPainter painter;

    void OnEnable()
    {
        painter = (MaterialMapPainter)target;
        SceneView.duringSceneGui += OnSceneGUIHandler;
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUIHandler;
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.HelpBox(
            "Hold and drag Ctrl + left click on scene, draw Rock/Soil.Snow",
            MessageType.Info);
    }

    void OnSceneGUIHandler(SceneView sceneView)
    {
        if (!painter || !painter.paintingEnabled)
            return;
        if (!painter.targetTerrain)
            return;

        Event e = Event.current;

        bool isPaintEvent =
            (e.button == 0) &&
            (e.modifiers & EventModifiers.Control) != 0 &&
            (e.type == EventType.MouseDrag || e.type == EventType.MouseDown);

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (Physics.Raycast(ray, out var hit))
        {
            Handles.color = Color.yellow;
            Handles.DrawWireDisc(hit.point, Vector3.up, painter.brushWorldRadius);

            if (isPaintEvent)
            {
                Undo.RecordObject(painter.pipeline.materialMaps, "Paint Material Map");
                painter.PaintAtWorldPos(hit.point);
                e.Use();
            }
        }

        if (isPaintEvent)
            sceneView.Repaint();
    }
}
