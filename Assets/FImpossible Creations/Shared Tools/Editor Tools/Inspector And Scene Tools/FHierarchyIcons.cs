#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

/* Code made by LaneFox from Unity Community Forum */

#if UNITY_EDITOR
[InitializeOnLoad]
#endif
public class FHierarchyIcons
{

#if UNITY_6000_4_OR_NEWER
    static FHierarchyIcons()
    {
#if UNITY_EDITOR
        EditorApplication.hierarchyWindowItemByEntityIdOnGUI += EvaluateIcons;
#endif
    }

    private static void EvaluateIcons(EntityId instanceId, Rect selectionRect)
    {
#if UNITY_EDITOR
        GameObject go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
        if (go == null) return;

        IFHierarchyIcon slotCon = go.GetComponent<IFHierarchyIcon>();
        if (slotCon != null) DrawIcon(slotCon.EditorIconPath, selectionRect);
#endif
    }

#else

    static FHierarchyIcons()
    {
#if UNITY_EDITOR
        EditorApplication.hierarchyWindowItemOnGUI += EvaluateIcons;
#endif
    }

    private static void EvaluateIcons(int instanceId, Rect selectionRect)
    {
#if UNITY_EDITOR
        GameObject go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
        if (go == null) return;

        IFHierarchyIcon slotCon = go.GetComponent<IFHierarchyIcon>();
        if (slotCon != null) DrawIcon(slotCon.EditorIconPath, selectionRect);
#endif
    }

#endif

    private static void DrawIcon(string texName, Rect rect)
    {
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(texName)) return;
        Rect r = new Rect(rect.x + rect.width - 16f, rect.y, 16f, 16f);
        GUI.DrawTexture(r, GetTex(texName));
#endif
    }

    private static Texture2D GetTex(string name)
    {
#if UNITY_EDITOR
        return (Texture2D)Resources.Load(name);
#else
        return null;
#endif
    }

}

public interface IFHierarchyIcon
{
    string EditorIconPath { get; }
}

