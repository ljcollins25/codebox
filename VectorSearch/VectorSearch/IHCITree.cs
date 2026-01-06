#nullable enable
using System.Numerics.Tensors;
using static VectorSearch.Helpers;

namespace VectorSearch;

/// <summary>
/// IHCI Tree - Incremental Hierarchical Clustering Index.
/// A spatial partitioning data structure for approximate nearest-neighbor search
/// over high-dimensional vector embeddings.
/// </summary>
public sealed class IHCITree
{
    private readonly IMetricModel _metric;
    private readonly IReadOnlyVectorStore _store;

    public int LeafCapacity { get; }
    public int RoutingMaxChildren { get; }
    public int LeafNeighborCount { get; }

    // Repair triggering
    public int RepairEveryInserts { get; }
    public int RepairQueueHighWatermark { get; }

    private int _insertCount;
    private int _nodeIdGen;

    private readonly ArrayBuilder<LeafNode> _leaves = new();
    private Node _root;
    private readonly RepairQueue _repairQueue = new();

    public IHCITree(
        IMetricModel metric,
        IReadOnlyVectorStore store,
        int leafCapacity = 128,
        int routingMaxChildren = 16,
        int leafNeighborCount = 8,
        int repairEveryInserts = 0,
        int repairQueueHighWatermark = 0)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        if (leafCapacity <= 1) throw new ArgumentOutOfRangeException(nameof(leafCapacity));
        if (routingMaxChildren < 2) throw new ArgumentOutOfRangeException(nameof(routingMaxChildren));
        if (leafNeighborCount < 0) throw new ArgumentOutOfRangeException(nameof(leafNeighborCount));

        LeafCapacity = leafCapacity;
        RoutingMaxChildren = routingMaxChildren;
        LeafNeighborCount = leafNeighborCount;

        RepairEveryInserts = repairEveryInserts > 0 ? repairEveryInserts : leafCapacity;
        RepairQueueHighWatermark = repairQueueHighWatermark > 0 ? repairQueueHighWatermark : (routingMaxChildren * 8);

        // Start with single leaf root
        var leaf = NewLeaf(parent: null, Array.Empty<float>());
        _root = leaf;
    }

    public IEnumerable<LeafNode> Leaves() => _leaves.Segment.Where(l => !l.IsDisposed);

    public Dictionary<VectorId, Node> GetVectorNodeMap()
    {
        var result = new Dictionary<VectorId, Node>();
        foreach (var leaf in _leaves.Span)
        {
            if (leaf.IsDisposed) continue;

            foreach (var vector in leaf.Vectors)
            {
                result[vector] = leaf;
            }
        }

        return result;
    }

    // ----------------------------
    // Public API
    // ----------------------------

    /// <summary>
    /// Inserts a vector ID into the tree.
    /// </summary>
    public void Insert(VectorId id)
    {
        if (!id.IsValid) throw new ArgumentException("Invalid VectorId", nameof(id));

        // Descend to a leaf (single-best path)
        LeafNode leaf = DescendToLeafForInsert(_root, id);

        // Add vector id
        leaf.AddVector(id);

        // Ensure radius bound for leaf and propagate radius growth upward if needed
        leaf.UpdateRadiusUpperBoundForNewVector(_store, _metric);
        PropagateContainmentUpward(leaf);

        // Split if necessary
        if (leaf.CountVectors > LeafCapacity)
        {
            SplitLeaf(leaf);
        }

        // Schedule leaf for repair
        EnqueueRepair(leaf);

        // Opportunistic repairs
        _insertCount++;
        if ((_insertCount % RepairEveryInserts) == 0)
        {
            RepairOneNode();
        }
        else if (_repairQueue.Count > RepairQueueHighWatermark)
        {
            // Backpressure: repair 1 extra node
            RepairOneNode();
        }
    }

    /// <summary>
    /// Queries for the top-K nearest neighbors to the given query vector.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="k">Number of nearest neighbors to return.</param>
    /// <param name="routingWidth">Number of candidate nodes to maintain at each routing level during descent (default 2). Higher values explore more regions but increase cost.</param>
    /// <param name="seenLeaves">Optional dictionary to track visited leaves and their distances.</param>
    public (VectorId Id, float Distance)[] Query(ReadOnlySpan<float> query, int k, int routingWidth = 2, Dictionary<LeafNode, float>? seenLeaves = null)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        if (routingWidth < 1) throw new ArgumentOutOfRangeException(nameof(routingWidth));
        if (query.Length != _store.Dimensions) throw new ArgumentException("Query dimension mismatch", nameof(query));

        // Bounded top-k as a max-heap by distance
        var best = new BoundedMaxK(k);

        // Multi-candidate descent: find multiple candidate leaves
        var candidateLeaves = DescendToLeavesForQuery(_root, query, routingWidth, best);

        // Visited set for neighbor expansion
        var visitedLeaves = new HashSet<LeafNode>(capacity: candidateLeaves.Count + LeafNeighborCount * candidateLeaves.Count);

        // Process each candidate leaf
        foreach (var leaf in candidateLeaves)
        {
            if (!visitedLeaves.Add(leaf)) continue;

            // Scan this leaf's vectors
            ScanLeafVectors(leaf, query, best);
            seenLeaves?[leaf] = leaf.Center.Length > 0 ? _metric.Distance(query, leaf.Center) : 0;

            // Expand to leaf neighbors
            if (LeafNeighborCount > 0)
            {
                foreach (var neighborNode in leaf.Neighbors.Span)
                {
                    if (neighborNode is not LeafNode neighbor) continue;
                    if (!visitedLeaves.Add(neighbor)) continue;

                    // Prune by sphere lower bound if we have a worst already
                    if (best.HasWorst && neighbor.Center.Length > 0)
                    {
                        float lb = LowerBoundDistanceToSphere(query, neighbor.Center, neighbor.Radius, new(out var distance));
                        seenLeaves?[neighbor] = distance;
                        if (lb > best.WorstDistance)
                            continue;
                    }

                    ScanLeafVectors(neighbor, query, best);
                }
            }
        }

        return best.ToSortedArray();
    }

    /// <summary>
    /// Performs one repair operation from the repair queue.
    /// </summary>
    public bool RepairOneNode()
    {
        if (!_repairQueue.TryDequeue(out var node))
            return false;

        node.Repair(this);

        // After repair, parent may need repair if containment invalidated
        if (node.Parent is not null)
        {
            if (!SphereContainsSphere(node.Parent.Center, node.Parent.Radius, node.Center, node.Radius, _metric))
            {
                float needed = _metric.Distance(node.Parent.Center, node.Center) + node.Radius;
                if (needed > node.Parent.Radius)
                    node.Parent.Radius = needed;

                EnqueueRepair(node.Parent);
            }
        }

        return true;
    }

    /// <summary>
    /// Repairs all nodes in the repair queue.
    /// </summary>
    public void RepairAll()
    {
        while (RepairOneNode()) { }
    }

    // ----------------------------
    // Node types
    // ----------------------------

    public record struct NodeId(int Id)
    {
        public static implicit operator int(NodeId n) => n.Id;
    }

    public abstract class Node : IComparable<Node>
    {
        public NodeId NodeId;
        public RoutingNode? Parent;
        public int IndexInParent = -1;

        public string Path => field ??= string.Join("/", Parent?.Path, NodeId.Id);

        public override string ToString()
        {
            return Path;
        }

        // Center is the current stored representative (stale between repairs).
        // Radius is always an upper bound around Center.
        public float[] Center = Array.Empty<float>();
        public float Radius;

        // Dedupe flag for repair queue
        public bool InRepairQueue;

        // Descendant vector count
        public int DescCount;

        public abstract bool IsLeaf { get; }

        public abstract void Repair(IHCITree tree);

        public abstract (Node left, Node right) NewSplitNodes(IHCITree tree, float distance, ReadOnlySpan<float> leftCenter, ReadOnlySpan<int> leftChildIndices, ReadOnlySpan<float> rightCenter, ReadOnlySpan<int> rightChildIndices);

        public abstract int ChildVectorLength { get; }

        public abstract ReadOnlySpan<float> GetChildVector(IHCITree tree, int index);

        public abstract void ForEachOutgoing<TData>(IHCITree tree, TData data, Action<int, Node, TData> handle)
            where TData : allows ref struct;

        public abstract void ForEachVector<TData>(IHCITree tree, TData data, Action<int, ReadOnlySpan<float>, TData> handle)
            where TData : allows ref struct;

        public int CompareTo(Node? other)
        {
            return 0;
        }
    }

    public sealed class LeafNode : Node
    {
        public override bool IsLeaf => true;

        // Store vector ids only
        private VectorId[] _vectors;
        private int _count;

        // Leaf neighbors (directed)
        public StructArrayBuilder<LeafNode?> Neighbors;
        public StructArrayBuilder<float> NeighborDistances;

        public bool IsDisposed;

        public LeafNode(NodeId nodeId, int neighborCount)
        {
            NodeId = nodeId;
            _vectors = new VectorId[64];
            _count = 0;
            DescCount = 0;
            Neighbors = new(0);
            NeighborDistances = new(0);
        }

        public int CountVectors => _count;

        public ReadOnlySpan<VectorId> Vectors => _vectors.AsSpan(0, _count);

        public void AddVector(VectorId id)
        {
            if (_count == _vectors.Length)
                Array.Resize(ref _vectors, _vectors.Length * 2);

            _vectors[_count++] = id;
            DescCount = _count;
        }

        public void ClearAndTakeFrom(ReadOnlySpan<VectorId> ids)
        {
            _count = 0;
            if (_vectors.Length < ids.Length)
                _vectors = new VectorId[Math.Max(ids.Length, 64)];

            ids.CopyTo(_vectors);
            _count = ids.Length;
            DescCount = _count;
        }

        public void UpdateRadiusUpperBoundForNewVector(IReadOnlyVectorStore store, IMetricModel metric)
        {
            if (Center.Length == 0) return;

            var v = store.GetVector(_vectors[_count - 1]);
            float d = metric.Distance(Center, v);
            if (d > Radius) Radius = d;
        }

        public override void Repair(IHCITree tree)
        {
            EnsureCenterAllocated(tree._store.Dimensions);

            IMetricModel metric = tree._metric;
            ComputeLeafCenterAndRadius(tree._store, metric, Vectors, Center, out float radius);
            Radius = radius;

            var center = Center.AsSpan();

            bool needsSort = false;
            float lastDistance = 0;
            for (int i = 0; i < Neighbors.Length; i++)
            {
                var neighbor = Neighbors[i];
                var distance = metric.Distance(center, neighbor!.Center);
                NeighborDistances[i] = distance;

                if (lastDistance > distance)
                {
                    needsSort = true;
                }

                lastDistance = distance;
            }

            if (needsSort)
            {
                SortNeighbors();
            }

            // Repair neighbors: find closest leaves
            //if (tree.LeafNeighborCount > 0)
            //{
            //    var nearest = tree.FindNearestLeavesForRepair(this, tree.LeafNeighborCount);
            //    Neighbors.Reset();
            //    foreach (var n in nearest)
            //    {
            //        Neighbors.Add(n);
            //    }
            //}
        }

        private void EnsureCenterAllocated(int dim)
        {
            if (Center.Length != dim)
                Center = new float[dim];
        }

        public override void ForEachOutgoing<TData>(IHCITree tree, TData data, Action<int, Node, TData> handle)
        {
            int index = 0;
            foreach (var neighbor in Neighbors.Span)
            {
                if (neighbor is not null)
                    handle(index++, neighbor, data);
            }
        }

        public override int ChildVectorLength => _count;

        public override ReadOnlySpan<float> GetChildVector(IHCITree tree, int index)
        {
            return tree._store.GetVector(_vectors[index]);
        }

        public void SortNeighbors()
        {
            NeighborDistances.Span.Sort(Neighbors.Span);
        }

        public override (Node left, Node right) NewSplitNodes(IHCITree tree, float distance, ReadOnlySpan<float> leftCenter, ReadOnlySpan<int> leftChildIndices, ReadOnlySpan<float> rightCenter, ReadOnlySpan<int> rightChildIndices)
        {
            IsDisposed = true;
            var leftLeaf = tree.NewLeaf(parent: null, leftCenter);
            var rightLeaf = tree.NewLeaf(parent: null, rightCenter);

            // Copy vectors to new leaves
            Span<VectorId> leftVectors = stackalloc VectorId[leftChildIndices.Length];
            for (int i = 0; i < leftChildIndices.Length; i++)
                leftVectors[i] = _vectors[leftChildIndices[i]];
            leftLeaf.ClearAndTakeFrom(leftVectors);

            Span<VectorId> rightVectors = stackalloc VectorId[rightChildIndices.Length];
            for (int i = 0; i < rightChildIndices.Length; i++)
                rightVectors[i] = _vectors[rightChildIndices[i]];
            rightLeaf.ClearAndTakeFrom(rightVectors);

            // Initialize new leaves' neighbors from source node's neighbors (excluding self)
            // and add each other as neighbors
            //leftLeaf.Neighbors.AddRange(Neighbors.Span);
            //leftLeaf.NeighborDistances.AddRange(NeighborDistances.Span);

            //rightLeaf.Neighbors.AddRange(Neighbors.Span);
            //rightLeaf.NeighborDistances.AddRange(NeighborDistances.Span);

            // Add each other as neighbors (sorted by distance)

            // Update all old neighbors: remove source node (this), add the two new leaves
            for (int i = 0; i < Neighbors.Count; i++)
            {
                var neighbor = Neighbors[i]!;

                float neighborDist = NeighborDistances[i];
                var neighborCenter = neighbor.Center.AsSpan();

                float leftNeighborDistance = tree._metric.Distance(rightCenter, neighborCenter);
                float rightNeighborDistance = tree._metric.Distance(leftCenter, neighborCenter);

                // Remove the source node from neighbor's lists
                int sourceIndex = neighbor.FindNeighborIndex(this);
                if (sourceIndex >= 0)
                {
                    neighbor.Neighbors.RemoveAt(sourceIndex);
                    neighbor.NeighborDistances.RemoveAt(sourceIndex);
                }

                leftLeaf.Neighbors.Add(neighbor);
                leftLeaf.NeighborDistances.Add(leftNeighborDistance);
                rightLeaf.Neighbors.Add(neighbor);
                rightLeaf.NeighborDistances.Add(rightNeighborDistance);

                // Add the two new leaves to this neighbor (maintaining bounded count)
                neighbor.NeighborDistances.SortedInsertWithMirror(
                    ref neighbor.Neighbors,
                    tree.LeafNeighborCount,
                    leftNeighborDistance,
                    leftLeaf);

                neighbor.NeighborDistances.SortedInsertWithMirror(
                    ref neighbor.Neighbors,
                    tree.LeafNeighborCount,
                    rightNeighborDistance,
                    rightLeaf);
            }

            leftLeaf.SortNeighbors();
            rightLeaf.SortNeighbors();

            leftLeaf.NeighborDistances.SortedInsertWithMirror(
                ref leftLeaf.Neighbors,
                tree.LeafNeighborCount,
                distance,
                rightLeaf);

            rightLeaf.NeighborDistances.SortedInsertWithMirror(
                ref rightLeaf.Neighbors,
                tree.LeafNeighborCount,
                distance,
                leftLeaf);

            return (leftLeaf, rightLeaf);
        }

        public int FindNeighborIndex(Node node)
        {
            for (int i = 0; i < Neighbors.Count; i++)
            {
                if (ReferenceEquals(Neighbors[i], node))
                    return i;
            }
            return -1;
        }

        public override void ForEachVector<TData>(IHCITree tree, TData data, Action<int, ReadOnlySpan<float>, TData> handle)
        {
            for (int i = 0; i < _count; i++)
            {
                handle(i, tree._store.GetVector(_vectors[i]), data);
            }
        }
    }

    public sealed class RoutingNode : Node
    {
        public override bool IsLeaf => false;

        public ArrayBuilder<Node> Children;
        public int ChildCount => Children.Count;

        public RoutingNode(NodeId nodeId, int maxChildren, int dim)
        {
            NodeId = nodeId;
            Children = new();
            Center = new float[dim];
            DescCount = 0;
        }

        public ReadOnlySpan<Node> ChildrenSpan => Children.Span;

        public void AddChild(Node child)
        {
            child.IndexInParent = Children.Count;
            Children.Add(child);
            child.Parent = this;
            DescCount += child.DescCount;
        }

        public int IndexOfChild(Node child)
        {
            for (int i = 0; i < ChildCount; i++)
            {
                if (ReferenceEquals(Children[i], child))
                    return i;
            }
            return -1;
        }

        public override void Repair(IHCITree tree)
        {
            int dim = tree._store.Dimensions;

            if (Center.Length != dim)
                Center = new float[dim];

            Array.Clear(Center, 0, Center.Length);

            long totalW = 0;
            for (int i = 0; i < ChildCount; i++)
            {
                var c = Children[i];
                if (c.Center.Length != dim) continue;

                int w = Math.Max(1, c.DescCount);
                totalW += w;
                AddScaled(Center, c.Center, w);
            }

            if (totalW > 0)
            {
                float inv = 1.0f / (float)totalW;
                ScaleInPlace(Center, inv);
            }

            // Desc count exact from children
            int desc = 0;
            for (int i = 0; i < ChildCount; i++) desc += Children[i].DescCount;
            DescCount = desc;

            // Radius bound
            float r = 0;
            for (int i = 0; i < ChildCount; i++)
            {
                var c = Children[i];
                if (c.Center.Length != dim) continue;

                float needed = tree._metric.Distance(Center, c.Center) + c.Radius;
                if (needed > r) r = needed;
            }
            Radius = r;
        }

        public override int ChildVectorLength => ChildCount;

        public override ReadOnlySpan<float> GetChildVector(IHCITree tree, int index)
        {
            return Children[index].Center;
        }

        public override (Node left, Node right) NewSplitNodes(IHCITree tree, float distance, ReadOnlySpan<float> leftCenter, ReadOnlySpan<int> leftChildIndices, ReadOnlySpan<float> rightCenter, ReadOnlySpan<int> rightChildIndices)
        {
            var left = tree.NewRouting(parent: null, center: leftCenter);
            var right = tree.NewRouting(parent: null, center: rightCenter);

            for (int i = 0; i < leftChildIndices.Length; i++)
                left.AddChild(Children[leftChildIndices[i]]);

            for (int i = 0; i < rightChildIndices.Length; i++)
                right.AddChild(Children[rightChildIndices[i]]);

            return (left, right);
        }

        public override void ForEachOutgoing<TData>(IHCITree tree, TData data, Action<int, Node, TData> handle)
        {
            for (int i = 0; i < ChildCount; i++)
            {
                handle(i, Children[i], data);
            }
        }

        public override void ForEachVector<TData>(IHCITree tree, TData data, Action<int, ReadOnlySpan<float>, TData> handle)
        {
            for (int i = 0; i < ChildCount; i++)
            {
                var child = Children[i];
                if (child.Center.Length > 0)
                    handle(i, child.Center, data);
            }
        }
    }

    // ----------------------------
    // Repair queue structure
    // ----------------------------

    private sealed class RepairQueue
    {
        private readonly Queue<Node> _q = new();

        public int Count => _q.Count;

        public void Enqueue(Node n)
        {
            if (n.InRepairQueue) return;
            n.InRepairQueue = true;
            _q.Enqueue(n);
        }

        public bool TryDequeue(out Node node)
        {
            if (_q.Count == 0)
            {
                node = null!;
                return false;
            }

            node = _q.Dequeue();
            node.InRepairQueue = false;
            return true;
        }
    }

    private void EnqueueRepair(Node node)
    {
        _repairQueue.Enqueue(node);
    }

    // ----------------------------
    // Descent + pruning
    // ----------------------------

    private LeafNode DescendToLeafForInsert(Node node, VectorId id)
    {
        ReadOnlySpan<float> v = _store.GetVector(id);

        while (!node.IsLeaf)
        {
            var rn = (RoutingNode)node;
            int bestIdx = 0;
            float bestDist = float.PositiveInfinity;

            for (int i = 0; i < rn.ChildCount; i++)
            {
                var c = rn.Children[i];
                if (c.Center.Length == 0)
                {
                    if (bestIdx < 0) bestIdx = i;
                    continue;
                }

                float d = _metric.Distance(v, c.Center);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            node = rn.Children[bestIdx];
        }

        return (LeafNode)node;
    }

    /// <summary>
    /// Multi-candidate descent: maintains a bounded set of best candidates at each routing level,
    /// then returns multiple candidate leaves for search.
    /// </summary>
    private List<LeafNode> DescendToLeavesForQuery(Node root, ReadOnlySpan<float> query, int routingWidth, BoundedMaxK best)
    {
        // If root is a leaf, just return it
        if (root.IsLeaf)
            return [((LeafNode)root)];

        // Current level candidates: (distance to center, node)
        var currentLevel = new List<(float Distance, Node Node)>(routingWidth * 2);
        var nextLevel = new List<(float Distance, Node Node)>(routingWidth * 2);

        currentLevel.Add((0f, root));

        // Descend through routing levels
        while (currentLevel.Count > 0)
        {
            nextLevel.Clear();

            foreach (var (_, node) in currentLevel)
            {
                if (node.IsLeaf)
                {
                    // This shouldn't happen in normal traversal, but handle it
                    nextLevel.Add((0f, node));
                    continue;
                }

                var rn = (RoutingNode)node;

                // Evaluate all children of this routing node
                for (int i = 0; i < rn.ChildCount; i++)
                {
                    var child = rn.Children[i];

                    // Compute lower bound distance for pruning
                    float centerDist;
                    if (child.Center.Length == 0)
                    {
                        centerDist = 0;
                    }
                    else
                    {
                        centerDist = _metric.Distance(query, child.Center);

                        // Prune by sphere lower bound if we have a worst result
                        if (best.HasWorst)
                        {
                            float lb = centerDist - child.Radius;
                            if (lb > 0 && lb > best.WorstDistance)
                                continue;
                        }
                    }

                    // Add to next level candidates
                    nextLevel.Add((centerDist, child));
                }
            }

            // If all candidates at next level are leaves, we're done
            if (nextLevel.Count == 0)
                break;

            // Sort by distance and keep only top routingWidth candidates
            nextLevel.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            // Check if we've reached leaf level
            bool allLeaves = true;
            foreach (var (_, n) in nextLevel)
            {
                if (!n.IsLeaf)
                {
                    allLeaves = false;
                    break;
                }
            }

            if (allLeaves)
            {
                // Collect leaf candidates (up to routingWidth^depth but bounded)
                var result = new List<LeafNode>(Math.Min(nextLevel.Count, routingWidth * 4));
                int maxLeaves = routingWidth * 4; // Allow more leaves since we want multi-start search
                for (int i = 0; i < nextLevel.Count && result.Count < maxLeaves; i++)
                {
                    var leaf = (LeafNode)nextLevel[i].Node;

                    // Final pruning check
                    if (best.HasWorst && leaf.Center.Length > 0)
                    {
                        float lb = nextLevel[i].Distance - leaf.Radius;
                        if (lb > 0 && lb > best.WorstDistance)
                            continue;
                    }

                    result.Add(leaf);
                }

                // Apply neighbor graph traversal to find better starting leaves
                if (LeafNeighborCount > 0 && result.Count > 0)
                {
                    var improved = ImproveLeavesByNeighborTraversal(result, query);
                    return improved;
                }

                return result;
            }

            // Swap and limit to routingWidth
            (currentLevel, nextLevel) = (nextLevel, currentLevel);
            if (currentLevel.Count > routingWidth)
            {
                currentLevel.RemoveRange(routingWidth, currentLevel.Count - routingWidth);
            }
        }

        // Fallback: if we somehow get here with no leaves, return first leaf found
        return [FindFirstLeaf(root)];
    }

    /// <summary>
    /// Traverses neighbor graph to potentially find better starting leaves.
    /// </summary>
    private List<LeafNode> ImproveLeavesByNeighborTraversal(List<LeafNode> startLeaves, ReadOnlySpan<float> query)
    {
        var result = new HashSet<LeafNode>(startLeaves);
        var visited = new HashSet<LeafNode>(startLeaves);

        // For each starting leaf, do a greedy neighbor walk
        foreach (var startLeaf in startLeaves)
        {
            var current = startLeaf;
            if (current.Center.Length == 0) continue;

            float currentDist = _metric.Distance(query, current.Center);

            bool improved = true;
            while (improved)
            {
                improved = false;

                foreach (var neighborNode in current.Neighbors.Span)
                {
                    if (neighborNode is not LeafNode neighbor) continue;
                    if (neighbor.Center.Length == 0) continue;
                    if (!visited.Add(neighbor)) continue;

                    float neighborDist = _metric.Distance(query, neighbor.Center);
                    if (neighborDist < currentDist)
                    {
                        currentDist = neighborDist;
                        current = neighbor;
                        improved = true;
                    }
                }
            }

            result.Add(current);
        }

        return [.. result];
    }

    private static LeafNode FindFirstLeaf(Node node)
    {
        while (!node.IsLeaf)
        {
            node = ((RoutingNode)node).Children[0];
        }
        return (LeafNode)node;
    }

    private float LowerBoundDistanceToSphere(ReadOnlySpan<float> point, ReadOnlySpan<float> center, float radius, Out<float> distance = default)
    {
        float d = _metric.Distance(point, center);
        distance.Value = d;
        float lb = d - radius;
        return lb <= 0 ? 0 : lb;
    }

    // ----------------------------
    // Leaf scanning
    // ----------------------------

    private void ScanLeafVectors(LeafNode leaf, ReadOnlySpan<float> query, BoundedMaxK best)
    {
        foreach (var id in leaf.Vectors)
        {
            var v = _store.GetVector(id);
            float d = _metric.Distance(query, v);
            best.Add(id, d);
        }
    }

    // ----------------------------
    // Splitting
    // ----------------------------

    private void SplitNode(Node node)
    {
        var length = node.ChildVectorLength;
        if (length < 2) return;

        // Seed selection: farthest-pair heuristic
        int seedA = FarthestIndexFromPoint(node, node.Center);
        ReadOnlySpan<float> leftPoint = node.GetChildVector(this, seedA);

        int seedB = FarthestIndexFromPoint(node, leftPoint, skipIndex: seedA, farthestDistance: new(out var splitDistance));
        ReadOnlySpan<float> rightPoint = node.GetChildVector(this, seedB);

        // Partition
        Span<int> leftIndicesBuffer = stackalloc int[length];
        Span<int> rightIndicesBuffer = stackalloc int[length];
        Span<bool> isLeftBuffer = stackalloc bool[length];
        var leftChildIndices = SpanList(leftIndicesBuffer);
        var rightChildIndices = SpanList(rightIndicesBuffer);
        var isLeftByIndex = SpanList(isLeftBuffer);
        isLeftByIndex.SetLength(length);

        Span<float> leftCentroidBuffer = stackalloc float[_store.Dimensions];
        Span<float> rightCentroidBuffer = stackalloc float[_store.Dimensions];
        var leftCentroidBuilder = new CentroidBuilder(leftCentroidBuffer);
        var rightCentroidBuilder = new CentroidBuilder(rightCentroidBuffer);

        for (int iteration = 0; iteration < 2; iteration++)
        {
            int mid = length / 2;

            for (int i = 0; i < length; i++)
            {
                ref bool isLeft = ref isLeftByIndex[i];
                var v = node.GetChildVector(this, i);

                if (iteration == 0)
                {
                    float dl = _metric.Distance(v, leftPoint);
                    float dr = _metric.Distance(v, rightPoint);
                    isLeft = dl <= dr;
                }
                else
                {
                    isLeft = i < mid;
                }

                if (isLeft)
                {
                    leftChildIndices.Add(i);
                    leftCentroidBuilder.Add(v);
                }
                else
                {
                    rightChildIndices.Add(i);
                    rightCentroidBuilder.Add(v);
                }
            }

            if (leftChildIndices.Count == 0 || rightChildIndices.Count == 0)
            {
                leftChildIndices.Reset();
                rightChildIndices.Reset();
                leftCentroidBuilder.Reset();
                rightCentroidBuilder.Reset();
                continue;
            }

            break;
        }

        // Create two new nodes
        var (leftNode, rightNode) = node.NewSplitNodes(
            tree: this,
            distance: splitDistance,
            leftCenter: leftCentroidBuilder.Build(),
            leftChildIndices: leftChildIndices.Span,
            rightCenter: rightCentroidBuilder.Build(),
            rightChildIndices: rightChildIndices.Span);

        // Compute radii
        var leftCentroid = leftNode.Center.AsSpan();
        var rightCentroid = rightNode.Center.AsSpan();

        for (int i = 0; i < length; i++)
        {
            bool isLeft = isLeftByIndex[i];
            var v = node.GetChildVector(this, i);
            var center = isLeft ? leftCentroid : rightCentroid;
            float dist = _metric.Distance(center, v);

            // For routing nodes, add child radius
            if (!node.IsLeaf)
            {
                var rn = (RoutingNode)node;
                dist += rn.Children[i].Radius;
            }

            var targetNode = isLeft ? leftNode : rightNode;
            targetNode.Radius.MaxWith(dist);
        }

        // Replace node in tree
        IntegrateNewSplitNodes(node, leftNode, rightNode);

        // Schedule repairs
        EnqueueRepair(leftNode);
        EnqueueRepair(rightNode);
        if (leftNode.Parent is not null) EnqueueRepair(leftNode.Parent);
    }

    private void IntegrateNewSplitNodes(Node oldNode, Node leftNode, Node rightNode)
    {
        if (oldNode.Parent is null)
        {
            // oldNode was root → create new root routing node with 2 children
            var newRoot = NewRouting(parent: null, center: default);
            newRoot.AddChild(leftNode);
            newRoot.AddChild(rightNode);
            _root = newRoot;

            leftNode.Repair(this);
            rightNode.Repair(this);
            newRoot.Repair(this);
        }
        else
        {
            var parent = oldNode.Parent;

            parent.Children[oldNode.IndexInParent] = leftNode;
            leftNode.Parent = parent;
            leftNode.IndexInParent = oldNode.IndexInParent;

            if (parent.ChildCount < RoutingMaxChildren)
            {
                parent.AddChild(rightNode);
            }
            else
            {
                // Parent is full; split parent after adding rightNode temporarily
                parent.Children.Add(rightNode);
                rightNode.Parent = parent;
                rightNode.IndexInParent = parent.ChildCount - 1;
                SplitNode(parent);
            }

            PropagateContainmentUpward(leftNode);
            PropagateContainmentUpward(rightNode);
        }
    }

    private void SplitLeaf(LeafNode leaf) => SplitNode(leaf);

    // ----------------------------
    // Neighbor search for repair
    // ----------------------------

    private List<LeafNode> FindNearestLeavesForRepair(LeafNode target, int k)
    {
        if (target.Center.Length == 0)
            return new List<LeafNode>(0);

        var result = new List<LeafNode>();
        var candidates = new List<(float Distance, LeafNode Leaf)>();

        // Collect all leaves from the tree
        CollectLeaves(_root, target, candidates);

        // Sort by distance and take top k
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        for (int i = 0; i < Math.Min(k, candidates.Count); i++)
        {
            result.Add(candidates[i].Leaf);
        }

        return result;
    }

    private void CollectLeaves(Node node, LeafNode target, List<(float Distance, LeafNode Leaf)> result)
    {
        if (node is LeafNode leaf)
        {
            if (!ReferenceEquals(leaf, target) && leaf.Center.Length > 0)
            {
                float dist = _metric.Distance(target.Center, leaf.Center);
                result.Add((dist, leaf));
            }
        }
        else if (node is RoutingNode rn)
        {
            foreach (var child in rn.Children.Span)
            {
                CollectLeaves(child, target, result);
            }
        }
    }

    // ----------------------------
    // Containment propagation
    // ----------------------------

    private void PropagateContainmentUpward(Node node)
    {
        var p = node.Parent;
        while (p is not null)
        {
            if (p.Center.Length == 0 || node.Center.Length == 0)
            {
                EnqueueRepair(p);
                p = p.Parent;
                continue;
            }

            float needed = _metric.Distance(p.Center, node.Center) + node.Radius;
            if (needed > p.Radius)
            {
                p.Radius = needed;
                EnqueueRepair(p);
                node = p;
                p = p.Parent;
                continue;
            }

            // contained => stop
            break;
        }
    }

    private static bool SphereContainsSphere(
        float[] parentCenter,
        float parentRadius,
        float[] childCenter,
        float childRadius,
        IMetricModel metric)
    {
        if (parentCenter.Length == 0 || childCenter.Length == 0) return false;
        float d = metric.Distance(parentCenter, childCenter);
        return d + childRadius <= parentRadius;
    }

    // ----------------------------
    // Leaf centroid/radius computation
    // ----------------------------

    private static void ComputeLeafCenterAndRadius(
        IReadOnlyVectorStore store,
        IMetricModel metric,
        ReadOnlySpan<VectorId> ids,
        float[] center,
        out float radius)
    {
        int dim = store.Dimensions;
        Array.Clear(center, 0, center.Length);

        if (ids.Length == 0)
        {
            radius = 0;
            return;
        }

        // center = average of vectors
        for (int i = 0; i < ids.Length; i++)
        {
            var v = store.GetVector(ids[i]);
            TensorPrimitives.Add(v, center, center);
        }

        TensorPrimitives.Divide(center, ids.Length, center);

        // radius = max distance from center
        float r = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            var v = store.GetVector(ids[i]);
            float dist = metric.Distance(center, v);
            r.MaxWith(dist);
        }

        radius = r;
    }

    private int FarthestIndexFromPoint(Node node, ReadOnlySpan<float> point, int skipIndex = -1, Out<float> farthestDistance = default)
    {
        int farthestIndex = -1;
        float farthestDist = float.NegativeInfinity;

        for (int i = 0; i < node.ChildVectorLength; i++)
        {
            if (i == skipIndex) continue;
            var v = node.GetChildVector(this, i);
            float d = _metric.Distance(v, point);
            if (d > farthestDist)
            {
                farthestDist = d;
                farthestIndex = i;
            }
        }

        farthestDistance.Value = farthestDist;
        return farthestIndex;
    }

    // ----------------------------
    // Node allocation
    // ----------------------------

    private LeafNode NewLeaf(RoutingNode? parent, ReadOnlySpan<float> center)
    {
        var id = new NodeId(_leaves.Count);
        var leaf = new LeafNode(nodeId: id, neighborCount: LeafNeighborCount)
        {
            Parent = parent,
            Center = center.ToArray(),
            Radius = 0
        };

        _leaves.Add(leaf);
        return leaf;
    }

    private RoutingNode NewRouting(RoutingNode? parent, ReadOnlySpan<float> center)
    {
        var rn = new RoutingNode(nodeId: new NodeId(++_nodeIdGen), maxChildren: RoutingMaxChildren, dim: _store.Dimensions)
        {
            Center = center.Length == 0 ? Array.Empty<float>() : center.ToArray(),
            Parent = parent,
            Radius = 0
        };
        return rn;
    }

    // ----------------------------
    // Small vector helpers
    // ----------------------------

    private static void AddScaled(float[] acc, float[] v, int scale)
    {
        for (int i = 0; i < acc.Length; i++)
            acc[i] += v[i] * scale;
    }

    private static void ScaleInPlace(float[] v, float s)
    {
        for (int i = 0; i < v.Length; i++)
            v[i] *= s;
    }

    // ----------------------------
    // Query top-k helper
    // ----------------------------

    private sealed class BoundedMaxK
    {
        private readonly int _k;
        private readonly PriorityQueue<VectorId, float> _pq;
        private readonly IComparer<float> _maxComparer;

        public bool HasWorst => _pq.Count >= _k;

        public float WorstDistance
        {
            get
            {
                _pq.TryPeek(out _, out float worst);
                return worst;
            }
        }

        public BoundedMaxK(int k)
        {
            _k = k;
            _maxComparer = Comparer<float>.Create((a, b) => b.CompareTo(a));
            _pq = new PriorityQueue<VectorId, float>(k, _maxComparer);
        }

        public void Add(VectorId id, float distance)
        {
            if (_pq.Count < _k)
            {
                _pq.Enqueue(id, distance);
                return;
            }

            _pq.TryPeek(out _, out float worst);
            if (distance >= worst)
                return;

            _pq.EnqueueDequeue(id, distance);
        }

        public (VectorId Id, float Distance)[] ToSortedArray()
        {
            var arr = _pq.UnorderedItems.ToArray();
            Array.Sort(arr, static (a, b) =>
            {
                int cmp = a.Priority.CompareTo(b.Priority);
                return cmp != 0 ? cmp : a.Element.Index.CompareTo(b.Element.Index);
            });

            var res = new (VectorId, float)[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                res[i] = (arr[i].Element, arr[i].Priority);

            return res;
        }
    }
}
