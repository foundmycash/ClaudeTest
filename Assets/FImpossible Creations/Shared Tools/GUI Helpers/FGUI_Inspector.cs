#if UNITY_EDITOR

using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using System.Collections;
using System.Text;

namespace FIMSpace.FEditor
{
    public static class FGUI_Inspector
    {
        public static readonly GUILayoutOption[] _button_h34 = new GUILayoutOption[] { GUILayout.Height(34) };
        public static readonly GUILayoutOption[] _button_h22 = new GUILayoutOption[] { GUILayout.Height(22) };
        public static readonly GUILayoutOption[] _button_h18 = new GUILayoutOption[] { GUILayout.Height(18) };
        public static readonly GUILayoutOption[] _button_w18h18 = new GUILayoutOption[] { GUILayout.Width(18), GUILayout.Height(18) };
        public static readonly GUILayoutOption[] _button_w20h18 = new GUILayoutOption[] { GUILayout.Width(20), GUILayout.Height(18) };
        public static readonly GUILayoutOption[] _button_w20h20 = new GUILayoutOption[] { GUILayout.Width(20), GUILayout.Height(20) };
        public static readonly GUILayoutOption[] _button_w22h18 = new GUILayoutOption[] { GUILayout.Width(22), GUILayout.Height(18) };
        public static readonly GUILayoutOption[] _button_w22h20 = new GUILayoutOption[] { GUILayout.Width(22), GUILayout.Height(20) };
        public static readonly GUILayoutOption[] _button_w22h22 = new GUILayoutOption[] { GUILayout.Width(22), GUILayout.Height(22) };
        public static readonly GUILayoutOption[] _button_w20h16 = new GUILayoutOption[] { GUILayout.Width(20), GUILayout.Height(16) };
        public static readonly GUILayoutOption[] _button_w22h16 = new GUILayoutOption[] { GUILayout.Width(22), GUILayout.Height(16) };
        public static readonly GUILayoutOption[] _button_w19h15 = new GUILayoutOption[] { GUILayout.Width(19), GUILayout.Height(15) };
        public static readonly GUILayoutOption[] _button_w14h14 = new GUILayoutOption[] { GUILayout.Width(14), GUILayout.Height(14) };

        public static readonly RectOffset ZeroOffset = new RectOffset(0, 0, 0, 0);
        public static Object LastObjSelected;
        public static GameObject LastGameObjectSelected;

        static readonly StringBuilder SharedStringBuilder = new StringBuilder();

        static StringBuilder sb => SharedStringBuilder;
        public static string BuildString(string s1, string s2) { sb.Clear(); sb.Append(s1); sb.Append(s2); return sb.ToString(); }
        public static string BuildString(string s1, float v2) { sb.Clear(); sb.Append(s1); sb.Append(v2); return sb.ToString(); }
        public static string BuildString(string s1, double v2) { sb.Clear(); sb.Append(s1); sb.Append(v2); return sb.ToString(); }
        public static string BuildString(string s1, string s2, string s3) { sb.Clear(); sb.Append(s1); sb.Append(s2); sb.Append(s3); return sb.ToString(); }

        /// <summary> Since remembering in which EditorGUI, EditorGUILayout, or EditorGUIUtility, or  GUILayoutUtility ahhh... in which if these classes you will find the desired variable is so confusing ¯\_(ツ)_/¯ each time when trying finding it, ending in googling for forums post with it </summary>
        public static float InspectorViewWidth()
        {
#if UNITY_EDITOR
            return EditorGUIUtility.currentViewWidth;
#else
            return 0f;
#endif

        }

        public static bool IsRightMouseButton()
        {
            if (UnityEngine.Event.current == null) return false;

            if (UnityEngine.Event.current.type == UnityEngine.EventType.Used)
                if (UnityEngine.Event.current.button == 1 || UnityEngine.Event.current.control)
                    return true;

            return false;
        }

        public static void HeaderBox(ref bool foldout, string title, bool frame, Texture icon = null, int height = 20, int iconsSize = 19, bool big = false)
        {
            if (frame) EditorGUILayout.BeginHorizontal(FGUI_Resources.HeaderBoxStyle); else EditorGUILayout.BeginHorizontal();
            string f = FGUI_Resources.GetFoldSimbol(foldout);

            GUILayout.Label(new GUIContent(" "), GUILayout.Width(1));
            if (icon != null) if (GUILayout.Button(new GUIContent(icon), EditorStyles.label, new GUILayoutOption[2] { GUILayout.Width(iconsSize), GUILayout.Height(iconsSize) })) { foldout = !foldout; }
            if (GUILayout.Button(f + "     " + title + "     " + f, big ? FGUI_Resources.HeaderStyleBig : FGUI_Resources.HeaderStyle, GUILayout.Height(height))) foldout = !foldout;
            if (icon != null) if (GUILayout.Button(new GUIContent(icon), EditorStyles.label, new GUILayoutOption[2] { GUILayout.Width(iconsSize), GUILayout.Height(iconsSize) })) { foldout = !foldout; }
            GUILayout.Label(new GUIContent(" "), GUILayout.Width(1));

            EditorGUILayout.EndHorizontal();
        }

        public static void HeaderBox(string title, bool frame, Texture icon = null, int height = 20, int iconsSize = 19, bool big = false)
        {
            if (frame) EditorGUILayout.BeginHorizontal(FGUI_Resources.HeaderBoxStyle); else EditorGUILayout.BeginHorizontal();

            GUILayout.Label(new GUIContent(" "), GUILayout.Width(1));
            if (icon != null) if (GUILayout.Button(new GUIContent(icon), EditorStyles.label, new GUILayoutOption[2] { GUILayout.Width(iconsSize), GUILayout.Height(iconsSize) })) { }
            if (GUILayout.Button(title, big ? FGUI_Resources.HeaderStyleBig : FGUI_Resources.HeaderStyle, GUILayout.Height(height))) { }
            if (icon != null) if (GUILayout.Button(new GUIContent(icon), EditorStyles.label, new GUILayoutOption[2] { GUILayout.Width(iconsSize), GUILayout.Height(iconsSize) })) { }
            GUILayout.Label(new GUIContent(" "), GUILayout.Width(1));

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// GUILayout.EndVertical(); after foldout
        /// </summary>
        public static void FoldHeaderStart(ref bool foldout, string title, GUIStyle style = null, Texture icon = null, int height = 22)
        {
            FoldHeaderStart(ref foldout, new GUIContent(title), FGUI_Resources.FoldStyle, style, icon, height);
        }

        /// <summary>
        /// GUILayout.EndVertical(); after foldout
        /// </summary>
        public static void FoldHeaderStart(ref bool foldout, GUIContent label, GUIStyle style = null, Texture icon = null, int height = 22)
        {
            FoldHeaderStart(ref foldout, label, FGUI_Resources.FoldStyle, style, icon, height);
        }

        public static void FoldHeaderStart(ref bool foldout, GUIContent title, GUIStyle textStyle, GUIStyle vertStyle, Texture icon = null, int height = 22)
        {
            if (vertStyle != null) GUILayout.BeginVertical(vertStyle);
            if (GUILayout.Button(new GUIContent("   " + FGUI_Resources.GetFoldSimbol(foldout, 10, "►") + "  " + title.text, icon, title.tooltip), textStyle, GUILayout.Height(height))) foldout = !foldout;
        }

        /// <summary>
        /// Header for modules switch / fold
        /// </summary>
        public static void FoldSwitchableHeaderStart(ref bool enable, ref bool foldout, string title, GUIStyle style = null, Texture icon = null, int height = 22, string tooltip = "", bool big = false)
        {
            if (style != null) GUILayout.BeginVertical(style);
            GUILayout.BeginHorizontal();

            if (enable)
            {
                if (GUILayout.Button(new GUIContent("  " + FGUI_Resources.GetFoldSimbol(foldout, 10, "►") + "  " + title, icon, tooltip), big ? FGUI_Resources.FoldStyleBig : FGUI_Resources.FoldStyle, GUILayout.Height(height))) foldout = !foldout;
                enable = EditorGUILayout.Toggle(enable, GUILayout.Width(16));
            }
            else
            {
                if (GUILayout.Button(new GUIContent("  " + title, icon, tooltip), big ? FGUI_Resources.FoldStyleBig : FGUI_Resources.FoldStyle, GUILayout.Height(height))) { enable = true; }
                enable = EditorGUILayout.Toggle(enable, GUILayout.Width(16));
            }

            GUILayout.EndHorizontal();
        }

        public static void FoldSwitchableHeaderStart(ref bool enable, SerializedProperty toggle, ref bool foldout, string title, GUIStyle style = null, Texture icon = null, int height = 22, string tooltip = "", bool big = false)
        {
            if (style != null) GUILayout.BeginVertical(style);
            GUILayout.BeginHorizontal();

            if (enable)
            {
                if (GUILayout.Button(new GUIContent("  " + FGUI_Resources.GetFoldSimbol(foldout, 10, "►") + "  " + title, icon, tooltip), big ? FGUI_Resources.FoldStyleBig : FGUI_Resources.FoldStyle, GUILayout.Height(height))) foldout = !foldout;
                EditorGUILayout.PropertyField(toggle, GUIContent.none, GUILayout.Width(16));
            }
            else
            {
                if (GUILayout.Button(new GUIContent("  " + title, icon, tooltip), big ? FGUI_Resources.FoldStyleBig : FGUI_Resources.FoldStyle, GUILayout.Height(height))) { enable = true; }
                EditorGUILayout.PropertyField(toggle, GUIContent.none, GUILayout.Width(16));
            }

            GUILayout.EndHorizontal();
        }


        public static void DrawSwitchButton(ref bool switcher, Texture icon, Texture pressedIcon = null, string tooltip = "", int width = 20, int height = 20, bool reversePress = false)
        {
            bool pressed = switcher;
            if (reversePress) pressed = !pressed;
            Color c = GUI.color;

            if (pressed) GUI.color = new Color(.7f, .7f, .7f, 1f);

            if (pressedIcon != null && ((pressed && !reversePress) || (!pressed && reversePress)))
            {
                if (GUILayout.Button(new GUIContent(pressedIcon, tooltip), FGUI_Resources.ButtonStyle, new GUILayoutOption[2] { GUILayout.Width(width), GUILayout.Height(height) })) switcher = !switcher;
            }
            else
                if (GUILayout.Button(new GUIContent(icon, tooltip), FGUI_Resources.ButtonStyle, new GUILayoutOption[2] { GUILayout.Width(width), GUILayout.Height(height) })) switcher = !switcher;

            GUI.color = c;
        }

        public static void DrawSwitchButton(ref bool enable, string tooltip, Texture icon)
        {
            Color c = GUI.color;
            GUI.color = enable ? new Color(0.9f, 0.9f, 0.9f, 1f) : c;
            if (GUILayout.Button(new GUIContent(icon, tooltip), EditorStyles.miniButtonRight, new GUILayoutOption[2] { GUILayout.Width(20), GUILayout.Height(16) })) enable = !enable;
            GUI.color = c;
        }


        /// <returns> Returns true if warning was clicked </returns>
        public static bool DrawWarning(string title)
        {
            bool clicked = false;
            EditorGUILayout.BeginVertical(Style(new Color(.6f, .6f, .3f, .075f), 0));
            if (GUILayout.Button(new GUIContent(title, EditorGUIUtility.IconContent("console.warnicon.sml").image), EditorStyles.boldLabel)) clicked = true;
            //EditorGUILayout.LabelField(new GUIContent(title, EditorGUIUtility.IconContent("console.warnicon.sml").image), EditorStyles.boldLabel);
            EditorGUILayout.EndVertical();
            return clicked;
        }

        public static void DrawUILineCommon(int padding = 6, int thickness = 1, float width = 0.975f)
        {
            DrawUILine(0.35f, 0.35f, thickness, padding, width);
        }


        public static void DrawUILine(Color color, int thickness = 2, int padding = 10, float width = 1f)
        {
            Rect rect = EditorGUILayout.GetControlRect(GUILayout.Height(padding + thickness));
            float w = rect.width; float off = rect.width - rect.width * width;
            rect.height = thickness; rect.y += padding / 2; rect.x -= 2; rect.x += off / 2f; rect.width += 2; rect.width *= width;
            EditorGUI.DrawRect(rect, color);
        }

        public static void DrawUILine(float alpha, float brightness = 0.25f, int thickness = 2, int padding = 10, float width = 1f)
        {
            Rect rect = EditorGUILayout.GetControlRect(GUILayout.Height(padding + thickness));
            float w = rect.width; float off = rect.width - rect.width * width;
            rect.height = thickness; rect.y += padding / 2; rect.x -= 2; rect.x += off / 2f; rect.width += 2; rect.width *= width;
            EditorGUI.DrawRect(rect, new Color(brightness, brightness, brightness, alpha));
        }

        public static void VSpace(int space2019, int spacePre2019)
        {
#if UNITY_2019_3_OR_NEWER
            GUILayout.Space(space2019);
#else
            GUILayout.Space(spacePre2019);
#endif
        }


        private static bool displayedDPIWarning = false;
        public static GUIStyle Style(Color bgColor, int off = -1)
        {
            GUIStyle newStyle = new GUIStyle(GUI.skin.box);
            if (off < 0) { if (Screen.dpi != 120) newStyle.border = new RectOffset(off, off, off, off); else if (!displayedDPIWarning) { /*Debug.Log("<b>[HEY! UNITY DEVELOPER!]</b> It seems you have setted up incorrect DPI settings for unity editor. Check <b>Unity.exe -> Properties -> Compatibility -> Change DPI Settings -> Replace Scaling -> System / System (Upgraded)</b> And restart Unity Editor.");*/ displayedDPIWarning = true; } }
            else newStyle.border = new RectOffset(off, off, off, off);

            Color[] solidColor = new Color[1] { bgColor };

            Texture2D bg = new Texture2D(1, 1);
            bg.SetPixels(solidColor); bg.Apply();
            newStyle.normal.background = bg;

            return newStyle;
        }

        public static GUIStyle Style(RectOffset padding)
        {
            GUIStyle newStyle = new GUIStyle();
            newStyle.padding = padding;
            return newStyle;
        }

        public static GUIStyle Style(RectOffset padding, RectOffset margin, Color bgColor, Vector4 off, int zeroBorder = 0)
        {
            GUIStyle newStyle = new GUIStyle(GUI.skin.box);
            bool g = false;
            if (off.x < 0) { if (Screen.dpi == 120) { if (!displayedDPIWarning) { Debug.Log("<b>[HEY! UNITY DEVELOPER!]</b> It seems you have setted up incorrect DPI settings for unity editor. Check <b>Unity.exe -> Properties -> Compatibility -> Change DPI Settings -> Replace Scaling -> System / System (Upgraded)</b> And restart Unity Editor."); displayedDPIWarning = true; } } else g = true; } else g = true;
            if (g) newStyle.border = new RectOffset((int)off.x, (int)off.y, (int)off.z, (int)off.w);
            newStyle.margin = margin;
            newStyle.padding = padding;

            Texture2D bg;

            if (zeroBorder < 1)
            {
                bg = new Texture2D(1, 1);
                bg.SetPixels(new Color[1] { bgColor }); bg.Apply();
            }
            else
            {
                int s = 16;
                bg = new Texture2D(s, s);
                bg.filterMode = FilterMode.Point;
                Color[] c = new Color[s * s];
                for (int x = 0; x < s; x++)
                {
                    for (int y = 0; y < s; y++)
                    {
                        if (x < zeroBorder || x >= (s - zeroBorder) || y < zeroBorder || y >= (s - zeroBorder))
                            c[x + y * s] = Color.clear;
                        else
                            c[x + y * s] = bgColor;
                    }
                }

                bg.SetPixels(c); bg.Apply();
            }

            newStyle.normal.background = bg;

            return newStyle;
        }



        public static void BeginHorizontal(GUIStyle style, Color color, bool bgColor = true)
        {
            Color c = bgColor ? GUI.backgroundColor : GUI.color;
            if (bgColor) GUI.backgroundColor = color; else GUI.color = color;
            EditorGUILayout.BeginHorizontal(style);
            if (bgColor) GUI.backgroundColor = c; else GUI.color = c;
        }

        public static void BeginVertical(GUIStyle style, Color color, bool bgColor = true)
        {
            Color c = bgColor ? GUI.backgroundColor : GUI.color;
            if (bgColor) GUI.backgroundColor = color; else GUI.color = color;
            EditorGUILayout.BeginVertical(style);
            if (bgColor) GUI.backgroundColor = c; else GUI.color = c;
        }


        public static void DrawBackToGameObjectButton()
        {
            if (LastGameObjectSelected == null) return;
            if (GUILayout.Button("<- Go Back To " + LastGameObjectSelected.name, GUILayout.Height(26))) Selection.activeObject = LastGameObjectSelected;
        }

        public static void DrawBackToObjectButton()
        {
            if (LastObjSelected == null) return;
            if (GUILayout.Button("<- Go Back To " + LastObjSelected.name, GUILayout.Height(26))) Selection.activeObject = LastObjSelected;
        }


        /// <summary> 
        /// Reading custom inspector editor drawer for the provided unity monoBehaviour or scriptable object.
        /// Can be used for Editor.CreateEditor(this, GetCustomEditorType) 
        /// </summary>
        public static System.Type GetCustomEditorType(UnityEngine.Object uObject)
        {
            System.Type behaviourType = uObject.GetType();

            foreach (var customEditor in System.Attribute.GetCustomAttributes(behaviourType, typeof(CustomEditor), true))
            {
                var editor = customEditor as CustomEditor;
                if (editor != null && editor.GetType().IsAssignableFrom(typeof(Editor)))
                {
                    return editor.GetType();
                }
            }

            return null;
        }

        /// <summary> Generates Editor for provided unity object </summary>
        public static UnityEditor.Editor GetEditorOf(UnityEngine.Object obj)
        {
            System.Type customEditorType = GetCustomEditorType(obj);

            if (customEditorType != null)
                return Editor.CreateEditor(obj, customEditorType);
            else
                return Editor.CreateEditor(obj);
        }

        public static void DrawObjectProperties(SerializedObject obj, int skipFirstProperties = 0)
        {
            var props = obj.GetIterator();
            props.NextVisible(true);

            for (int s = 0; s < skipFirstProperties; s++)
            {
                if (props.NextVisible(false) == false) return; // No more properties
            }

            while (props.NextVisible(false))
            {
                EditorGUILayout.PropertyField(props);
            }
        }

        #region UI Scale DPI

        private static float _editorUiScaling;
        public static float EditorUIScale { get { return GetEditorUIScale(); } }

        public static float GetEditorUIScale()
        {
#if UNITY_EDITOR
            if (_editorUiScaling > 0.1f) return _editorUiScaling;

            System.Reflection.PropertyInfo p = typeof(GUIUtility).GetProperty("pixelsPerPoint", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

            if (p != null)
                _editorUiScaling = (float)p.GetValue(null, null);
            else
                _editorUiScaling = 1f;
            return
                _editorUiScaling;
#else
return 1f;
#endif
        }

        #endregion


        public static void UnfocusControl()
        {
#if UNITY_EDITOR
            GUI.FocusControl(null);
#endif
        }

        public static readonly Color Color_RemoveRed = new Color(1f, 0.6f, 0.6f, 1f);
        public static void RedGUIBackground()
        {
            GUI.backgroundColor = Color_RemoveRed;
        }

        public static void RestoreGUIBackground()
        {
            GUI.backgroundColor = Color.white;
        }

        public static void RestoreGUIColor()
        {
            GUI.color = Color.white;
        }

        // <author>
        //   douduck08: https://github.com/douduck08
        //   Use Reflection to get instance of Unity's SerializedProperty in Custom Editor.
        //   Modified codes from 'Unity Answers', in order to apply on nested List<T> or Array. 
        //   
        //   Original author: HiddenMonk & Johannes Deml
        //   Ref: http://answers.unity3d.com/questions/627090/convert-serializedproperty-to-custom-class.html
        // </author>

        public static T GetEditorPropertyValue<T>(this SerializedProperty property) where T : class
        {
            object obj = property.serializedObject.targetObject;
            string path = property.propertyPath.Replace(".Array.data", "");
            string[] fieldStructure = path.Split('.');
            Regex rgx = new Regex(@"\[\d+\]");
            for (int i = 0; i < fieldStructure.Length; i++)
            {
                if (fieldStructure[i].Contains("["))
                {
                    int index = System.Convert.ToInt32(new string(fieldStructure[i].Where(c => char.IsDigit(c)).ToArray()));
                    obj = GetEditorPropertyFieldValueWithIndex(rgx.Replace(fieldStructure[i], ""), obj, index);
                }
                else
                {
                    obj = GetEditorPropertyFieldValue(fieldStructure[i], obj);
                }
            }
            return (T)obj;
        }

        public static bool SetEditorPropertyValue<T>(this SerializedProperty property, T value) where T : class
        {
            object obj = property.serializedObject.targetObject;
            string path = property.propertyPath.Replace(".Array.data", "");
            string[] fieldStructure = path.Split('.');
            Regex rgx = new Regex(@"\[\d+\]");
            for (int i = 0; i < fieldStructure.Length - 1; i++)
            {
                if (fieldStructure[i].Contains("["))
                {
                    int index = System.Convert.ToInt32(new string(fieldStructure[i].Where(c => char.IsDigit(c)).ToArray()));
                    obj = GetEditorPropertyFieldValueWithIndex(rgx.Replace(fieldStructure[i], ""), obj, index);
                }
                else
                {
                    obj = GetEditorPropertyFieldValue(fieldStructure[i], obj);
                }
            }

            string fieldName = fieldStructure.Last();
            if (fieldName.Contains("["))
            {
                int index = System.Convert.ToInt32(new string(fieldName.Where(c => char.IsDigit(c)).ToArray()));
                return SetEditorPropertyFieldValueWithIndex(rgx.Replace(fieldName, ""), obj, index, value);
            }
            else
            {
                Debug.Log(value);
                return SetEditorPropertyFieldValue(fieldName, obj, value);
            }
        }

        private static object GetEditorPropertyFieldValue(string fieldName, object obj, BindingFlags bindings = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        {
            FieldInfo field = obj.GetType().GetField(fieldName, bindings);
            if (field != null)
            {
                return field.GetValue(obj);
            }
            return default(object);
        }

        private static object GetEditorPropertyFieldValueWithIndex(string fieldName, object obj, int index, BindingFlags bindings = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        {
            FieldInfo field = obj.GetType().GetField(fieldName, bindings);
            if (field != null)
            {
                object list = field.GetValue(obj);
                if (list.GetType().IsArray)
                {
                    return ((object[])list)[index];
                }
                else if (list is IEnumerable)
                {
                    return ((IList)list)[index];
                }
            }
            return default(object);
        }

        public static bool SetEditorPropertyFieldValue(string fieldName, object obj, object value, bool includeAllBases = false, BindingFlags bindings = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        {
            FieldInfo field = obj.GetType().GetField(fieldName, bindings);
            if (field != null)
            {
                field.SetValue(obj, value);
                return true;
            }
            return false;
        }

        public static bool SetEditorPropertyFieldValueWithIndex(string fieldName, object obj, int index, object value, bool includeAllBases = false, BindingFlags bindings = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        {
            FieldInfo field = obj.GetType().GetField(fieldName, bindings);
            if (field != null)
            {
                object list = field.GetValue(obj);
                if (list.GetType().IsArray)
                {
                    ((object[])list)[index] = value;
                    return true;
                }
                else if (value is IEnumerable)
                {
                    ((IList)list)[index] = value;
                    return true;
                }
            }
            return false;
        }

        public static bool IsSerializedPropertyValid(SerializedProperty sp)
        {
            if (sp == null) return false;
            if (sp.serializedObject == null) return false;

            try
            {
                // Try because on line below unity can throw null ref exception ¯\_(ツ)_/¯
                if (sp.serializedObject.targetObject == null) return false;
            }
            catch (System.Exception) { return false; }

            return true;
        }

        public static void DrawCategoryButton<T>(ref T current, T target, string title, GUIStyle style) where T : System.Enum
        {
            if (current.Equals(target)) GUI.backgroundColor = Color.green;
            if (GUILayout.Button(title, style)) { current = target; }
            GUI.backgroundColor = Color.white;
        }

    }
}

#endif
