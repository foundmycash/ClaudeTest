using System.Collections.Generic;
using UnityEngine;

namespace HavocCave
{
    /// <summary>
    /// A hole in the spawn cave's wall, floor or ceiling: a round tube starting inside the cave and reaching out through the rock.
    /// Positions are in metres from the middle of the spawn cave's floor (which is the Cave Generator's position).
    /// </summary>
    [System.Serializable]
    public class CaveSpawnHole
    {
        [Tooltip("Where the hole starts, inside the spawn cave. Move it with the handle in the Scene view.")]
        public Vector3 position = new Vector3(0f, 1.45f, 4.8f);

        [Tooltip("Which way the hole points, in degrees around the up axis. 0 = along the generator's forward (+Z), 90 = to its right.")]
        [Range(-180f, 180f)] public float yaw = 0f;

        [Tooltip("Tilt of the hole in degrees. 0 = level, positive = climbs, negative = drops. -90 makes a hole in the floor.")]
        [Range(-90f, 90f)] public float pitch = 0f;

        [Tooltip("Half the width of the hole in metres.")]
        [Min(0.8f)] public float radius = 1.7f;

        [Tooltip("How far the hole reaches from its start, in metres. It has to be long enough to get out through the wall.")]
        [Min(1f)] public float length = 6f;

        [Tooltip("On: the hole sits on the floor (its height is set for you). Off: use the height in Position.")]
        public bool snapToFloor = true;

        [Tooltip("On: a tunnel of the generated cave starts at the far end of this hole. The first one is the main tunnel, the others are side tunnels. Off: a dead-end hole (a niche).")]
        public bool leadsToCave = true;

        /// <summary>The start of the hole, in spawn-cave space.</summary>
        public Vector3 Start { get { return new Vector3(position.x, snapToFloor ? radius * 0.85f - 0.02f : position.y, position.z); } }

        /// <summary>Unit vector the hole points along.</summary>
        public Vector3 Direction
        {
            get
            {
                float y = yaw * Mathf.Deg2Rad;
                float p = pitch * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
            }
        }

        /// <summary>The far end of the hole, in spawn-cave space.</summary>
        public Vector3 End { get { return Start + Direction * length; } }
    }

    /// <summary>
    /// A ledge, or a flight of steps, of solid rock standing on the spawn cave's floor.
    /// With Steps = 1 it is a single block. With more, it is a staircase that climbs along its depth.
    /// </summary>
    [System.Serializable]
    public class CaveSpawnLedge
    {
        [Tooltip("Where the middle of the ledge is on the floor, in metres from the middle of the floor: X is right, Y is forward.")]
        public Vector2 position = new Vector2(-3.4f, 0f);

        [Tooltip("Turns the ledge around the up axis, in degrees. The steps climb towards the ledge's front (0 = towards +Z).")]
        [Range(-180f, 180f)] public float yaw = -90f;

        [Tooltip("Width and depth of the ledge in metres. For steps, depth is the total length of the staircase.")]
        public Vector2 size = new Vector2(2.2f, 2f);

        [Tooltip("Height of one step in metres. The rock mesh is only as sharp as its voxel size, so steps of 0.4 m or more come out crisp; smaller ones get rounded off.")]
        [Min(0.1f)] public float height = 0.4f;

        [Tooltip("1 = one block. More = a staircase: every step is one Height taller than the one before.")]
        [Range(1, 12)] public int steps = 4;

        public float TotalHeight { get { return height * steps; } }
    }

    /// <summary>
    /// The spawn cave: a room you design yourself that every generated cave starts from. Its shape is the same for every seed.
    /// The rest of the cave grows out of the holes you mark as leading to the cave.
    /// </summary>
    [System.Serializable]
    public class CaveSpawnCave
    {
        [Tooltip("Off: the spawn is a random room like all the others.")]
        public bool enabled = true;

        [Tooltip("Half width (X), height (Y) and half depth (Z) of the room in metres. Drag the handles in the Scene view.")]
        public Vector3 size = new Vector3(6f, 5f, 6f);

        [Tooltip("0 = round, 1 = rounded square.")]
        [Range(0f, 1f)] public float squareness = 0.3f;

        [Tooltip("How uneven the outline and ceiling are. 0 = a clean oval.")]
        [Range(0f, 0.4f)] public float lumpiness = 0.12f;

        [Tooltip("Changes the bumps of the walls. Any number; the same number always gives the same walls.")]
        public int shapeSeed = 7;

        [Tooltip("Where you start on the floor, in metres from the middle: X is right, Y is forward.")]
        public Vector2 spawnPoint = Vector2.zero;

        [Tooltip("Holes in the walls, floor or ceiling. Tick 'Leads To Cave' on the ones the generated cave should grow out of.")]
        public List<CaveSpawnHole> holes = new List<CaveSpawnHole> { new CaveSpawnHole() };

        [Tooltip("Ledges and steps standing on the floor.")]
        public List<CaveSpawnLedge> ledges = new List<CaveSpawnLedge> { new CaveSpawnLedge() };

        /// <summary>A deep copy, safe to hand to a worker thread.</summary>
        public CaveSpawnCave Clone()
        {
            return JsonUtility.FromJson<CaveSpawnCave>(JsonUtility.ToJson(this));
        }
    }
}
