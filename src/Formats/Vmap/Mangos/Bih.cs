using System.IO;
using MaNGOS.Extractor.Formats.Vmap.Mangos.G3d;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Faithful 1:1 C# port of <c>VMAP::BIH</c> (mangostwo-server/src/game/vmap/BIH.{h,cpp}).
///
/// The Bounding Interval Hierarchy used by the Mangos vmap-extractor to index
/// every ModelSpawn of a map inside the <c>.vmtree</c> "NODE" chunk. The
/// server-side <c>StaticMapTree</c> reads it back to accelerate ray / point
/// queries against the per-map model placements.
///
/// Why a literal port instead of a clean re-implementation: the validation
/// contract is byte-for-byte equality with the reference C++ extractor output
/// (see <c>C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a original\vmaps\</c>).
/// The tree layout, node encoding (axis&lt;&lt;30 | BVH2-bit | child index),
/// empty-state (<see cref="InitEmpty"/> produces treeSize=3 + [0xC0000000,0,0])
/// and serialization order (bounds.lo, bounds.hi, treeSize, tree[], count,
/// objects[]) must all match exactly.
///
/// Source correspondence:
///   - build()                ↔ BIH.h:133-174
///   - init_empty()           ↔ BIH.h:108-115
///   - buildHierarchy()       ↔ BIH.cpp:34-46
///   - subdivide()            ↔ BIH.cpp:61-314
///   - createNode() (leaf)    ↔ BIH.h:585-590
///   - WriteToFile()          ↔ BIH.cpp:322-338
///   - ReadFromFile()         ↔ BIH.cpp:346-365
///   - BuildStats             ↔ BIH.h:515-566, BIH.cpp:373-408 (stats are not
///     serialized; methods are kept because Subdivide calls them, but they are
///     left as no-ops since the on-disk bytes don't depend on them).
/// </summary>
public sealed class Bih
{
    /// <summary>G3D <c>#define MAX_STACK_SIZE 64</c> (BIH.h:40).</summary>
    private const int MaxStackSize = 64;

    // ─────────────────────────────────────────────────────────────────────
    //  State (BIH.h:487-489 protected members)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>BIH.h:487 — the node array (interior nodes, BVH2 nodes, leaves).</summary>
    private readonly List<uint> _tree = new();

    /// <summary>BIH.h:488 — primitive indices reordered by the build (leaf payloads).</summary>
    private readonly List<uint> _objects = new();

    /// <summary>BIH.h:489 — global AABB over all primitives.</summary>
    private G3dAaBox _bounds;

    public Bih()
    {
        InitEmpty();
    }

    /// <summary>Number of primitives in the tree (BIH.h:181 primCount()).</summary>
    public int PrimCount => _objects.Count;

    /// <summary>Read-only access to the serialized bounds (used by tests / debug).</summary>
    public G3dAaBox Bounds => _bounds;

    // ─────────────────────────────────────────────────────────────────────
    //  init_empty — BIH.h:108-115
    //  A "BIH vide" is NOT treeSize=0 — it's a single dummy leaf node so the
    //  reader never sees an empty tree array. Signature of the bug we fixed.
    // ─────────────────────────────────────────────────────────────────────

    private void InitEmpty()
    {
        _tree.Clear();
        _objects.Clear();
        // Create space for the first node (dummy leaf)
        _tree.Add(3u << 30);
        _tree.Add(0u);
        _tree.Add(0u);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  build — BIH.h:133-174 (template unrolled around a getBounds callback)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the BIH from a list of primitives. Mirrors the C++ template
    /// <c>build(const PrimArray&amp;, BoundsFunc&amp;, leafSize, printStats)</c>
    /// with the bounds accessor supplied as a delegate (C++
    /// <c>BoundsTrait&lt;ModelSpawn*&gt;::getBounds</c>).
    /// </summary>
    /// <typeparam name="T">Primitive type (e.g. <see cref="MangosModelSpawn"/>).</typeparam>
    /// <param name="primitives">Primitive array (must outlive this call).</param>
    /// <param name="getBounds">Returns the AABB of one primitive into <c>out</c>.</param>
    /// <param name="leafSize">Max primitives per leaf (C++ default 3).</param>
    public void Build<T>(IReadOnlyList<T> primitives, GetBounds<T> getBounds, uint leafSize = 3)
    {
        if (primitives.Count == 0)
        {
            InitEmpty();
            return;
        }

        var dat = new BuildData
        {
            MaxPrims = (int)leafSize,
            NumPrims = primitives.Count,
            Indices = new uint[primitives.Count],
            PrimBound = new G3dAaBox[primitives.Count],
        };

        // BIH.h:147 — initialize bounds from primitives[0] BEFORE the merge loop.
        getBounds(primitives[0], out _bounds);

        for (int i = 0; i < dat.NumPrims; i++)
        {
            dat.Indices[i] = (uint)i;
            getBounds(primitives[i], out dat.PrimBound[i]);
            _bounds.Merge(dat.PrimBound[i]);
        }

        var tempTree = new List<uint>();
        var stats = new BuildStats();
        BuildHierarchy(tempTree, dat, stats);

        _objects.Clear();
        for (int i = 0; i < dat.NumPrims; i++)
        {
            _objects.Add(dat.Indices[i]);
        }

        _tree.Clear();
        _tree.AddRange(tempTree);
    }

    public delegate void GetBounds<in T>(T primitive, out G3dAaBox outBounds);

    // ─────────────────────────────────────────────────────────────────────
    //  buildHierarchy — BIH.cpp:34-46
    // ─────────────────────────────────────────────────────────────────────

    private void BuildHierarchy(List<uint> tempTree, BuildData dat, BuildStats stats)
    {
        // Create space for the first node (dummy leaf)
        tempTree.Add(3u << 30);
        tempTree.Add(0u);
        tempTree.Add(0u);

        // Seed bounding box
        AABound gridBox = new() { Lo = _bounds.Lo, Hi = _bounds.Hi };
        AABound nodeBox = gridBox;

        // Seed subdivide function
        Subdivide(0, dat.NumPrims - 1, tempTree, dat, gridBox, nodeBox, 0, 1, stats);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  subdivide — BIH.cpp:61-314 (literal line-by-line port)
    // ─────────────────────────────────────────────────────────────────────

    private void Subdivide(int left, int right, List<uint> tempTree, BuildData dat,
        AABound gridBox, AABound nodeBox, int nodeIndex, int depth, BuildStats stats)
    {
        // Check if we should create a leaf node
        if ((right - left + 1) <= dat.MaxPrims || depth >= MaxStackSize)
        {
            stats.UpdateLeaf(depth, right - left + 1);
            CreateNode(tempTree, nodeIndex, left, right);
            return;
        }

        // Initialize variables for partitioning
        int axis = -1, rightOrig = 0;
        float clipL = 0f, clipR = 0f;
        float prevClip = G3dMath.FNaN;
        float split = G3dMath.FNaN;
        bool wasLeft = true;

        while (true)
        {
            int prevAxis = axis;
            float prevSplit = split;

            // Perform quick consistency checks
            G3dVector3 d = new(
                gridBox.Hi.X - gridBox.Lo.X,
                gridBox.Hi.Y - gridBox.Lo.Y,
                gridBox.Hi.Z - gridBox.Lo.Z);
            if (d[0] < 0 || d[1] < 0 || d[2] < 0)
            {
                throw new InvalidOperationException("negative node extents");
            }
            for (int i = 0; i < 3; i++)
            {
                if (nodeBox.Hi[i] < gridBox.Lo[i] || nodeBox.Lo[i] > gridBox.Hi[i])
                {
                    throw new InvalidOperationException("invalid node overlap");
                }
            }

            // Find the longest axis
            axis = (int)d.PrimaryAxis();
            split = 0.5f * (gridBox.Lo[axis] + gridBox.Hi[axis]);

            // Partition L/R subsets
            clipL = -(float)G3dMath.Inf;  // -G3D::inf() promoted to float
            clipR = (float)G3dMath.Inf;
            rightOrig = right;
            float nodeL = (float)G3dMath.Inf;
            float nodeR = -(float)G3dMath.Inf;

            for (int i = left; i <= right;)
            {
                int obj = (int)dat.Indices[i];
                float minb = dat.PrimBound[obj].Lo[axis];
                float maxb = dat.PrimBound[obj].Hi[axis];
                float center = (minb + maxb) * 0.5f;

                if (center <= split)
                {
                    // Stay left
                    ++i;
                    if (clipL < maxb)
                    {
                        clipL = maxb;
                    }
                }
                else
                {
                    // Move to the right
                    uint t = dat.Indices[i];
                    dat.Indices[i] = dat.Indices[right];
                    dat.Indices[right] = t;
                    --right;
                    if (clipR > minb)
                    {
                        clipR = minb;
                    }
                }
                nodeL = Math.Min(nodeL, minb);
                nodeR = Math.Max(nodeR, maxb);
            }

            // Check for empty space
            if (nodeL > nodeBox.Lo[axis] && nodeR < nodeBox.Hi[axis])
            {
                float nodeBoxW = nodeBox.Hi[axis] - nodeBox.Lo[axis];
                float nodeNewW = nodeR - nodeL;

                // Node box is too big compared to space occupied by primitives
                if (1.3f * nodeNewW < nodeBoxW)
                {
                    stats.UpdateBVH2();
                    int nextIndex = tempTree.Count;

                    // Allocate child
                    tempTree.Add(0u);
                    tempTree.Add(0u);
                    tempTree.Add(0u);

                    // Write BVH2 clip node
                    stats.UpdateInner();
                    tempTree[nodeIndex + 0] = ((uint)axis << 30) | (1u << 29) | (uint)nextIndex;
                    tempTree[nodeIndex + 1] = FloatToRawIntBits(nodeL);
                    tempTree[nodeIndex + 2] = FloatToRawIntBits(nodeR);

                    // Update node box and recurse
                    nodeBox.Lo = WithAxis(nodeBox.Lo, axis, nodeL);
                    nodeBox.Hi = WithAxis(nodeBox.Hi, axis, nodeR);
                    Subdivide(left, rightOrig, tempTree, dat, gridBox, nodeBox, nextIndex, depth + 1, stats);
                    return;
                }
            }

            // Ensure we are making progress in the subdivision
            if (right == rightOrig)
            {
                // All left
                if (prevAxis == axis && G3dMath.FuzzyEq(prevSplit, split))
                {
                    // We are stuck here - create a leaf
                    stats.UpdateLeaf(depth, right - left + 1);
                    CreateNode(tempTree, nodeIndex, left, right);
                    return;
                }
                if (clipL <= split)
                {
                    // Keep looping on left half
                    gridBox.Hi = WithAxis(gridBox.Hi, axis, split);
                    prevClip = clipL;
                    wasLeft = true;
                    continue;
                }
                gridBox.Hi = WithAxis(gridBox.Hi, axis, split);
                prevClip = G3dMath.FNaN;
            }
            else if (left > right)
            {
                // All right
                right = rightOrig;
                if (prevAxis == axis && G3dMath.FuzzyEq(prevSplit, split))
                {
                    // We are stuck here - create a leaf
                    stats.UpdateLeaf(depth, rightOrig - left + 1);
                    CreateNode(tempTree, nodeIndex, left, rightOrig);
                    return;
                }
                if (clipR >= split)
                {
                    // Keep looping on right half
                    gridBox.Lo = WithAxis(gridBox.Lo, axis, split);
                    prevClip = clipR;
                    wasLeft = false;
                    continue;
                }
                gridBox.Lo = WithAxis(gridBox.Lo, axis, split);
                prevClip = G3dMath.FNaN;
            }
            else
            {
                // We are actually splitting stuff
                if (prevAxis != -1 && !float.IsNaN(prevClip))
                {
                    // Second time through - create the previous split since it produced empty space
                    int nextIndex = tempTree.Count;

                    // Allocate child node
                    tempTree.Add(0u);
                    tempTree.Add(0u);
                    tempTree.Add(0u);

                    if (wasLeft)
                    {
                        // Create a node with a left child
                        stats.UpdateInner();
                        tempTree[nodeIndex + 0] = ((uint)prevAxis << 30) | (uint)nextIndex;
                        tempTree[nodeIndex + 1] = FloatToRawIntBits(prevClip);
                        tempTree[nodeIndex + 2] = FloatToRawIntBits((float)G3dMath.Inf);
                    }
                    else
                    {
                        // Create a node with a right child
                        stats.UpdateInner();
                        tempTree[nodeIndex + 0] = ((uint)prevAxis << 30) | (uint)(nextIndex - 3);
                        tempTree[nodeIndex + 1] = FloatToRawIntBits(-(float)G3dMath.Inf);
                        tempTree[nodeIndex + 2] = FloatToRawIntBits(prevClip);
                    }

                    // Count stats for the unused leaf
                    ++depth;
                    stats.UpdateLeaf(depth, 0);

                    // Now we keep going as we are, with a new nodeIndex
                    nodeIndex = nextIndex;
                }
                break;
            }
        }

        // Compute index of child nodes
        int nextIndex2 = tempTree.Count;

        // Allocate left node
        int nl = right - left + 1;
        int nr = rightOrig - (right + 1) + 1;
        if (nl > 0)
        {
            tempTree.Add(0u);
            tempTree.Add(0u);
            tempTree.Add(0u);
        }
        else
        {
            nextIndex2 -= 3;
        }

        // Allocate right node
        if (nr > 0)
        {
            tempTree.Add(0u);
            tempTree.Add(0u);
            tempTree.Add(0u);
        }

        // Write leaf node
        stats.UpdateInner();
        tempTree[nodeIndex + 0] = ((uint)axis << 30) | (uint)nextIndex2;
        tempTree[nodeIndex + 1] = FloatToRawIntBits(clipL);
        tempTree[nodeIndex + 2] = FloatToRawIntBits(clipR);

        // Prepare L/R child boxes
        AABound gridBoxL = gridBox, gridBoxR = gridBox;
        AABound nodeBoxL = nodeBox, nodeBoxR = nodeBox;
        gridBoxL.Hi = WithAxis(gridBoxL.Hi, axis, split);
        gridBoxR.Lo = WithAxis(gridBoxR.Lo, axis, split);
        nodeBoxL.Hi = WithAxis(nodeBoxL.Hi, axis, clipL);
        nodeBoxR.Lo = WithAxis(nodeBoxR.Lo, axis, clipR);

        // Recurse
        if (nl > 0)
        {
            Subdivide(left, right, tempTree, dat, gridBoxL, nodeBoxL, nextIndex2, depth + 1, stats);
        }
        else
        {
            stats.UpdateLeaf(depth + 1, 0);
        }
        if (nr > 0)
        {
            Subdivide(right + 1, rightOrig, tempTree, dat, gridBoxR, nodeBoxR, nextIndex2 + 3, depth + 1, stats);
        }
        else
        {
            stats.UpdateLeaf(depth + 1, 0);
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="v"/> with component <paramref name="axis"/>
    /// replaced by <paramref name="value"/>. Matches the C++ <c>gridBox.hi[axis] = split</c>
    /// mutation pattern (G3D Vector3 is mutable in C++; G3dVector3 here is readonly).
    /// </summary>
    private static G3dVector3 WithAxis(G3dVector3 v, int axis, float value)
        => axis switch
        {
            0 => new G3dVector3(value, v.Y, v.Z),
            1 => new G3dVector3(v.X, value, v.Z),
            2 => new G3dVector3(v.X, v.Y, value),
            _ => v,
        };

    // ─────────────────────────────────────────────────────────────────────
    //  createNode — BIH.h:585-590 (leaf node)
    // ─────────────────────────────────────────────────────────────────────

    private static void CreateNode(List<uint> tempTree, int nodeIndex, int left, int right)
    {
        // Write leaf node
        tempTree[nodeIndex + 0] = (3u << 30) | (uint)left;
        tempTree[nodeIndex + 1] = (uint)(right - left + 1);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  WriteToFile — BIH.cpp:322-338
    //  EXACT serialization order, no chunk size prefix (the "NODE" tag is
    //  written by the caller; this method only writes the BIH payload).
    // ─────────────────────────────────────────────────────────────────────

    public void WriteToFile(BinaryWriter wf)
    {
        uint treeSize = (uint)_tree.Count;
        uint count = (uint)_objects.Count;

        wf.Write(_bounds.Lo.X); wf.Write(_bounds.Lo.Y); wf.Write(_bounds.Lo.Z);
        wf.Write(_bounds.Hi.X); wf.Write(_bounds.Hi.Y); wf.Write(_bounds.Hi.Z);
        wf.Write(treeSize);
        for (int i = 0; i < treeSize; i++) wf.Write(_tree[i]);
        wf.Write(count);
        for (int i = 0; i < count; i++) wf.Write(_objects[i]);
    }

    /// <summary>
    /// Read a BIH payload previously written by <see cref="WriteToFile"/>.
    /// Mirrors BIH.cpp:346-365 (ReadFromFile). Useful for round-trip tests
    /// and for the mmap-extractor to parse the .vmtree back.
    /// </summary>
    public void ReadFromFile(BinaryReader rf)
    {
        G3dVector3 lo = new(rf.ReadSingle(), rf.ReadSingle(), rf.ReadSingle());
        G3dVector3 hi = new(rf.ReadSingle(), rf.ReadSingle(), rf.ReadSingle());
        _bounds.Set(lo, hi);

        uint treeSize = rf.ReadUInt32();
        _tree.Clear();
        for (int i = 0; i < treeSize; i++) _tree.Add(rf.ReadUInt32());

        uint count = rf.ReadUInt32();
        _objects.Clear();
        for (int i = 0; i < count; i++) _objects.Add(rf.ReadUInt32());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  floatToRawIntBits / intBitsToFloat — BIH.h:58-84
    // ─────────────────────────────────────────────────────────────────────

    private static uint FloatToRawIntBits(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));

    // ─────────────────────────────────────────────────────────────────────
    //  Internal helper types (BIH.h:486-510)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>G3D <c>AABound</c> (BIH.h:89-92) — mutable axis-aligned box used by Subdivide.</summary>
    private struct AABound
    {
        public G3dVector3 Lo;
        public G3dVector3 Hi;
    }

    /// <summary>G3D <c>BIH::buildData</c> (BIH.h:494-500).</summary>
    private sealed class BuildData
    {
        public uint[] Indices = Array.Empty<uint>();   // BIH.h:496
        public G3dAaBox[] PrimBound = Array.Empty<G3dAaBox>(); // BIH.h:497
        public int NumPrims;                            // BIH.h:498
        public int MaxPrims;                            // BIH.h:499
    }

    /// <summary>
    /// G3D <c>BIH::BuildStats</c> (BIH.h:515-566). Not serialized — methods are
    /// kept because Subdivide calls them, but they intentionally do nothing.
    /// The tree bytes depend only on the tempTree mutations, never on these
    /// counters (verified by diff against BIH.cpp subdivide).
    /// </summary>
    private sealed class BuildStats
    {
        public void UpdateInner() { }
        public void UpdateBVH2() { }
        public void UpdateLeaf(int depth, int n) { }
    }
}
