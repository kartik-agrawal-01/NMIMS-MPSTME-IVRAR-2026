using System.Collections.Generic;
using UnityEngine;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Builds a boardroom around the existing scene layout so the demo reads as a
    /// real secure meeting room rather than a grey studio: a titled back wall behind
    /// the confidential whiteboard, a walnut conference table with six chairs,
    /// emissive ceiling panels, frosted glass side partitions and carpet.
    ///
    /// Everything is primitives placed in the measured room frame, and
    /// <b>every collider is stripped</b> - the XRI ray interactors must still reach
    /// the PIN keypad canvas and the OTP token through all of it.
    /// </summary>
    public class ProceduralBoardroom : MonoBehaviour
    {
        [Header("Back wall (behind the whiteboard)")]
        public float wallForward = 3.2f;
        public float wallWidth = 7.0f;
        public float wallHeight = 3.0f;

        [Header("Conference table")]
        public float tableForward = 2.15f;
        public float tableRight = 2.05f;
        public float tableTopHeight = 0.74f;
        public Vector2 tableSize = new Vector2(1.15f, 2.4f);   // (across, along the user's forward)

        [Header("Lighting and partitions")]
        public float ceilingHeight = 2.78f;
        public float partitionRight = 2.75f;

        private readonly List<Material> _materials = new();
        private Transform _root;

        private void Update()
        {
            if (_root != null) return;
            if (XrRigLocator.Origin == null && Camera.main == null) return;
            Build();
        }

        private void OnDestroy() => BoardroomMaterials.DestroyAll(_materials);

        private void Build()
        {
            var go = new GameObject("ProceduralBoardroom");
            go.transform.SetParent(transform, false);
            _root = go.transform;

            BuildCarpet();
            BuildBackWall();
            BuildTableAndChairs();
            BuildCeilingPanels();
            BuildPartitions();

            // Belt and braces: nothing built here may carry a collider.
            SpatialUiFactory.StripColliders(go);
        }

        // ---------------- pieces ----------------

        private void BuildCarpet()
        {
            var carpet = Track(BoardroomMaterials.Lit(new Color(0.10f, 0.11f, 0.14f), 0.05f));
            var inlay = Track(BoardroomMaterials.Lit(new Color(0.16f, 0.13f, 0.12f), 0.08f));

            // Four large floor quads, lifted a centimetre above the template floor grid.
            for (int i = 0; i < 4; i++)
            {
                float f = 0.4f + (i % 2) * 2.6f;
                float r = -1.3f + (i / 2) * 2.6f;
                Box("Carpet" + i, f, r, 0.012f, new Vector3(2.6f, 0.01f, 2.6f), carpet);
            }
            // A warmer inlay strip running under the table, like a rug.
            Box("CarpetInlay", tableForward, tableRight, 0.02f, new Vector3(1.9f, 0.01f, 3.0f), inlay);
        }

        private void BuildBackWall()
        {
            var wallMat = Track(BoardroomMaterials.Lit(new Color(0.17f, 0.19f, 0.24f), 0.15f));
            var trimMat = Track(BoardroomMaterials.Emissive(new Color(0.25f, 0.55f, 0.85f), 1.1f));

            Box("BackWall", wallForward, 0f, wallHeight * 0.5f, new Vector3(wallWidth, wallHeight, 0.08f), wallMat);
            Box("BackWallTrim", wallForward - 0.05f, 0f, 0.07f, new Vector3(wallWidth, 0.03f, 0.03f), trimMat);

            // Room title, sitting proud of the wall and reading towards the user.
            Vector3 titlePos = XrRigLocator.Place(wallForward - 0.07f, 0f, 2.35f);
            var titleGo = new GameObject("BoardroomTitle");
            titleGo.transform.SetParent(_root, false);
            titleGo.transform.position = titlePos;
            titleGo.transform.rotation = XrRigLocator.FacingOrigin(titlePos, 1.6f);

            var title = SpatialUiFactory.Text(titleGo.transform, "Title", Vector3.zero, 0.115f,
                new Color(0.85f, 0.92f, 1f));
            title.text = "BOARDROOM XR";

            var sub = SpatialUiFactory.Text(titleGo.transform, "Subtitle", new Vector3(0f, -0.13f, 0f), 0.062f,
                new Color(0.95f, 0.72f, 0.35f));
            sub.text = "BOARD MEETING  ·  CONFIDENTIAL";
        }

        private void BuildTableAndChairs()
        {
            var walnut = Track(BoardroomMaterials.Lit(new Color(0.20f, 0.11f, 0.07f), 0.55f, 0.05f));
            var steel = Track(BoardroomMaterials.Lit(new Color(0.32f, 0.33f, 0.36f), 0.6f, 0.4f));
            var fabric = Track(BoardroomMaterials.Lit(new Color(0.13f, 0.15f, 0.19f), 0.1f));

            // Table top and two plinths.
            Box("TableTop", tableForward, tableRight, tableTopHeight,
                new Vector3(tableSize.x, 0.06f, tableSize.y), walnut);
            Box("TablePlinthA", tableForward - tableSize.y * 0.3f, tableRight, tableTopHeight * 0.5f,
                new Vector3(tableSize.x * 0.45f, tableTopHeight, 0.12f), steel);
            Box("TablePlinthB", tableForward + tableSize.y * 0.3f, tableRight, tableTopHeight * 0.5f,
                new Vector3(tableSize.x * 0.45f, tableTopHeight, 0.12f), steel);

            // Six chairs: three down each long side.
            float side = tableSize.x * 0.5f + 0.34f;
            for (int i = 0; i < 6; i++)
            {
                int row = i % 3;
                float f = tableForward + (row - 1) * 0.78f;
                float r = tableRight + (i < 3 ? -side : side);
                BuildChair($"Chair{i}", f, r, fabric, steel, i < 3);
            }
        }

        private void BuildChair(string name, float f, float r, Material fabric, Material steel, bool facingRight)
        {
            // Seat, back and a simple pedestal: enough to read as a chair at a glance.
            Box(name + "Seat", f, r, 0.46f, new Vector3(0.46f, 0.07f, 0.46f), fabric);
            float backOffset = facingRight ? -0.21f : 0.21f;
            Box(name + "Back", f, r + backOffset, 0.72f, new Vector3(0.06f, 0.46f, 0.46f), fabric);
            Box(name + "Post", f, r, 0.23f, new Vector3(0.07f, 0.46f, 0.07f), steel);
            Box(name + "Base", f, r, 0.03f, new Vector3(0.40f, 0.04f, 0.40f), steel);
        }

        private void BuildCeilingPanels()
        {
            var panel = Track(BoardroomMaterials.Emissive(new Color(0.92f, 0.95f, 1f), 1.6f));
            var housing = Track(BoardroomMaterials.Lit(new Color(0.22f, 0.23f, 0.26f), 0.3f));

            for (int i = 0; i < 3; i++)
            {
                float f = 0.9f + i * 1.05f;
                Box($"CeilingHousing{i}", f, 0.3f, ceilingHeight + 0.03f, new Vector3(1.9f, 0.05f, 0.62f), housing);
                Box($"CeilingPanel{i}", f, 0.3f, ceilingHeight, new Vector3(1.8f, 0.02f, 0.55f), panel);
            }
        }

        private void BuildPartitions()
        {
            // Frosted glass: translucent and unlit, so it never goes black in the headset.
            var glass = Track(BoardroomMaterials.Translucent(new Color(0.65f, 0.80f, 0.90f, 0.13f)));
            var frame = Track(BoardroomMaterials.Lit(new Color(0.28f, 0.30f, 0.34f), 0.5f, 0.3f));

            for (int s = 0; s < 2; s++)
            {
                float r = s == 0 ? -partitionRight : partitionRight;
                for (int i = 0; i < 3; i++)
                {
                    float f = 0.7f + i * 1.1f;
                    Box($"Partition{s}_{i}", f, r, 1.15f, new Vector3(0.025f, 2.3f, 1.02f), glass);
                    Box($"PartitionFrame{s}_{i}", f + 0.55f, r, 1.15f, new Vector3(0.05f, 2.3f, 0.05f), frame);
                }
                Box($"PartitionRail{s}", 1.8f, r, 2.32f, new Vector3(0.06f, 0.06f, 3.3f), frame);
            }
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// A box in the measured room frame. <paramref name="size"/> is (across, up,
        /// along-forward) in metres, so layout reads the same way the room was measured.
        /// </summary>
        private GameObject Box(string name, float forward, float right, float height, Vector3 size, Material material)
        {
            Vector3 pos = XrRigLocator.Place(forward, right, height);
            var go = SpatialUiFactory.Primitive(PrimitiveType.Cube, _root, name, Vector3.zero,
                new Vector3(size.x, size.y, size.z), Quaternion.identity, material);
            go.transform.position = pos;
            // Local +Z points along the user's forward, +X along their right.
            go.transform.rotation = Quaternion.LookRotation(XrRigLocator.Forward, Vector3.up);
            return go;
        }

        private Material Track(Material m)
        {
            if (m != null) _materials.Add(m);
            return m;
        }
    }
}
