using System.Text;
using QuestOptimization;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Adds the forest's own numbers to the Performance Lab panel (hold both grips + left menu in the headset, F10 in the
    /// editor): what the instanced renderer drew last frame, how many lamps are lit and the dread at the camera. Use it with
    /// the panel's frame times when testing on Quest (see the README's performance test).
    /// </summary>
    public static class ForestPerformanceStats
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            PerformanceLab.ExtraStats -= Append;
            PerformanceLab.ExtraStats += Append;
        }

        static void Append(StringBuilder text)
        {
            ForestWorld world = ForestWorld.Active;
            if (world == null) return;
            var r = world.GetComponent<ForestInstancedRenderer>();
            text.Append("forest  draws ").Append(r != null ? r.LastDrawCalls : 0)
                .Append("   objects ").Append(r != null ? r.LastInstances : 0)
                .Append("   tris ").Append(r != null ? (r.LastTriangles / 1000).ToString() + "k" : "0");
            int lit = 0;
            foreach (ForestLamp l in ForestLampManager.Lamps) if (l.Level > 0.01f) lit++;
            Camera cam = Camera.main;
            text.Append("   lamps lit ").Append(lit).Append("   dread ")
                .Append(cam != null ? world.DreadAt(cam.transform.position).ToString("0.00") : "-")
                .Append("   detail ").Append(ForestDetail.Lite ? "lite" : "full").Append('\n');
        }
    }
}
