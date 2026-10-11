using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    public enum ForestTrailKind
    {
        /// <summary>The backbone: the trail from the trailhead and the trails that link every clearing.</summary>
        Main,
        /// <summary>Extra links that make loops.</summary>
        Path,
        /// <summary>A narrow path the monster cannot follow (only when Player Only Paths is on).</summary>
        PlayerOnly,
        /// <summary>A short trail that ends at a landmark.</summary>
        DeadEnd
    }

    public enum ForestNodeKind
    {
        Trailhead,
        Clearing,
        DeadEnd
    }

    /// <summary>One trail: a centre line in the forest's local XZ plane, every ~1.5 m.</summary>
    [System.Serializable]
    public class ForestTrail
    {
        public ForestTrailKind kind;
        public float width;
        public bool monsterAccessible = true;
        public int fromNode;
        public int toNode;
        public float length;
        public List<Vector2> points = new List<Vector2>();

        [Tooltip("0..1 per point: how deep in the forest (along the trails from the trailhead) this point is.")]
        public List<float> dread = new List<float>();

        public float HalfWidth { get { return width * 0.5f; } }
    }

    /// <summary>An open clearing (the trailhead counts as one).</summary>
    [System.Serializable]
    public class ForestClearing
    {
        public Vector2 center;
        public float radius;
        public float floorHeight;
        public float irregularity;
        public int shapeSeed;
        public bool trailhead;
        public bool heart;
        public float dread;

        /// <summary>The radius in a given direction (clearings are not perfect circles).</summary>
        public float RadiusAt(float angle)
        {
            float n = ForestNoise.Gradient(Mathf.Cos(angle) * 1.3f, Mathf.Sin(angle) * 1.3f, shapeSeed) * 0.75f
                    + ForestNoise.Gradient(Mathf.Cos(angle) * 2.9f, Mathf.Sin(angle) * 2.9f, shapeSeed + 17) * 0.25f;
            return radius * (1f + irregularity * n);
        }

        /// <summary>Distance from p to the clearing's (irregular) edge: positive inside.</summary>
        public float Inside(Vector2 p)
        {
            Vector2 d = p - center;
            return RadiusAt(Mathf.Atan2(d.y, d.x)) - d.magnitude;
        }
    }

    [System.Serializable]
    public class ForestNode
    {
        public ForestNodeKind kind;
        public Vector2 position;
        public int clearing = -1;
        public float distance;      // along the trails from the trailhead, in metres
        public int degree;
    }

    public enum ForestLandmarkSiteKind
    {
        InClearing,
        DeadEnd,
        Trailside,
        OffTrail
    }

    /// <summary>A spot reserved for a landmark. The scatter step puts one of the Landmark assets here.</summary>
    [System.Serializable]
    public struct ForestLandmarkSite
    {
        public Vector2 position;
        public float yaw;
        public ForestLandmarkSiteKind kind;
        public float dread;
    }

    /// <summary>A zone region's centre and which zone it is.</summary>
    [System.Serializable]
    public struct ForestZoneSite
    {
        public Vector2 position;
        public int zone;
    }

    /// <summary>The abstract plan of the forest: outline, trails, clearings, landmarks, zones and how deep everything is.</summary>
    public sealed class ForestLayout
    {
        public ForestShape shape;
        public readonly List<ForestNode> nodes = new List<ForestNode>();
        public readonly List<ForestTrail> trails = new List<ForestTrail>();
        public readonly List<ForestClearing> clearings = new List<ForestClearing>();
        public readonly List<ForestLandmarkSite> landmarks = new List<ForestLandmarkSite>();
        public readonly List<ForestZoneSite> zoneSites = new List<ForestZoneSite>();

        public Vector2 spawnPosition;
        public float spawnYaw;              // degrees, compass (0 = +Z)
        public int heartClearing = -1;

        public ForestTrailField trailField;
        public ForestZoneMap zoneMap;

        /// <summary>0..1 how deep in the forest each spot is (4 m grid).</summary>
        public ForestGrid dread;

        public float TotalTrailLength
        {
            get
            {
                float s = 0f;
                for (int i = 0; i < trails.Count; i++) s += trails[i].length;
                return s;
            }
        }
    }
}
