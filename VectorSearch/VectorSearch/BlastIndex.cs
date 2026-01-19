#nullable enable
using System.Numerics.Tensors;
using static VectorSearch.Helpers;

namespace VectorSearch;

/// <summary>
/// Blast-Driven Hierarchical Graph Index for approximate nearest-neighbor search.
/// 
/// Key characteristics:
/// - Multi-level structure: vectors → buckets → routing nodes
/// - Graph overlay with bidirectional neighbor edges
/// - Static representatives (epicenters) instead of centroids
/// - BLAST reorganization triggered by bucket overflow
/// - Heat tracking for epicenter selection
/// </summary>
public sealed class BlastIndex
{
    internal readonly IMetricModel _metric;
    internal readonly IReadOnlyVectorStore _store;

    /// <summary>Maximum vectors per bucket before BLAST triggers.</summary>
    public int BucketCapacity { get; }

    /// <summary>Maximum outgoing neighbor edges per node.</summary>
    public int OutgoingNeighborCount { get; }

    /// <summary>Maximum hops for neighbor traversal during candidate collection.</summary>
    public int NeighborHops { get; }

    /// <summary>Size of sliding window for window linking during traversal.</summary>
    public int WindowSize { get; }

    private readonly ArrayBuilder<VectorNode> _vectors = new();
    private readonly ArrayBuilder<BucketNode> _buckets = new();
    private Node _root;

    public BlastIndex(
        IMetricModel metric,
        IReadOnlyVectorStore store,
        int bucketCapacity = 128,
        int outgoingNeighborCount = 8,
        int neighborHops = 2,
        int windowSize = 4)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        if (bucketCapacity < 2) throw new ArgumentOutOfRangeException(nameof(bucketCapacity));
        if (outgoingNeighborCount < 1) throw new ArgumentOutOfRangeException(nameof(outgoingNeighborCount));

        BucketCapacity = bucketCapacity;
        OutgoingNeighborCount = outgoingNeighborCount;
        NeighborHops = neighborHops;
        WindowSize = windowSize;

        // Start with a single empty bucket as root
        var rootBucket = NewBucket(parent: null, representativeVector: ReadOnlySpan<float>.Empty);
        _root = rootBucket;
    }

    public IEnumerable<VectorNode> Vectors() => _vectors.Segment.Where(v => !v.IsDisposed);
    public IEnumerable<BucketNode> Buckets() => _buckets.Segment.Where(b => !b.IsDisposed);

    // ----------------------------
    // Public API
    // ----------------------------

    /// <summary>
    /// Inserts a vector ID into the index.
    /// </summary>
    public void Insert(VectorId id)
    {
        if (!id.IsValid) throw new ArgumentException("Invalid VectorId", nameof(id));

        ReadOnlySpan<float> v = _store.GetVector(id);

        // Create vector node
        var vectorNode = NewVectorNode(id);

        // Traverse from root to find target bucket
        var (targetBucket, visitedBuckets) = TraverseForInsert(_root, v);

        // Attach vector to bucket
        targetBucket.AddChild(vectorNode);

        // Window linking: create edges with vectors in visited buckets
        ProcessWindowLinks(vectorNode, visitedBuckets, v);

        // Check for BLAST trigger
        if (targetBucket.ChildCount > BucketCapacity)
        {
            Blast(targetBucket);
        }
    }

    /// <summary>
    /// Queries for the top-K nearest neighbors to the given query vector.
    /// </summary>
    public (VectorId Id, float Distance)[] Query(ReadOnlySpan<float> query, int k)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        if (query.Length != _store.Dimensions) throw new ArgumentException("Query dimension mismatch", nameof(query));

        var best = new BoundedMaxK(k);
        var visited = new HashSet<Node>();
        var enqueued = new HashSet<Node>();

        // Priority queue for traversal: (distance, node)
        var pq = new PriorityQueue<Node, float>();

        // Start from root
        float rootDist = GetDistanceToNode(_root, query);
        pq.Enqueue(_root, rootDist);
        enqueued.Add(_root);

        int maxVisits = Math.Max(k * 20, 200); // Limit exploration
        int visits = 0;

        while (pq.TryDequeue(out var current, out var currentDist) && visits < maxVisits)
        {
            if (!visited.Add(current))
                continue;

            visits++;

            if (current is VectorNode vn)
            {
                // Scan this vector
                float d = _metric.Distance(query, _store.GetVector(vn.VectorId));
                best.Add(vn.VectorId, d);

                // Expand to neighbors (up to NeighborHops)
                ExpandVectorNeighbors(vn, query, pq, enqueued, best);
            }
            else if (current is BucketNode bn)
            {
                // Expand children
                foreach (var child in bn.Children.Span)
                {
                    if (!enqueued.Add(child)) continue;

                    float childDist = GetDistanceToNode(child, query);

                    // Prune
                    if (best.HasWorst && childDist > best.WorstDistance)
                        continue;

                    pq.Enqueue(child, childDist);
                }

                // Expand outgoing neighbors
                foreach (var neighbor in bn.OutgoingNeighbors.Span)
                {
                    if (neighbor is null || !enqueued.Add(neighbor)) continue;

                    float neighborDist = GetDistanceToNode(neighbor, query);
                    if (best.HasWorst && neighborDist > best.WorstDistance)
                        continue;

                    pq.Enqueue(neighbor, neighborDist);
                }

                // Expand incoming neighbors (allowed per spec)
                foreach (var neighbor in bn.IncomingNeighbors)
                {
                    if (!enqueued.Add(neighbor)) continue;

                    float neighborDist = GetDistanceToNode(neighbor, query);
                    if (best.HasWorst && neighborDist > best.WorstDistance)
                        continue;

                    pq.Enqueue(neighbor, neighborDist);
                }
            }
        }

        return best.ToSortedArray();
    }

    /// <summary>
    /// Query trace event types per spec 9.1.
    /// </summary>
    public enum TraceEventType
    {
        PopCandidate,
        SetCurrent,
        AddCandidate,
        ScanVector,
        Terminate
    }

    /// <summary>
    /// A single trace event per spec 9.1.
    /// </summary>
    public readonly record struct TraceEvent(TraceEventType Type, string Id, float Distance, string Reason);

    /// <summary>
    /// Query trace result containing events and stats per spec 9.1 and 9.2.
    /// </summary>
    public sealed class QueryTraceResult
    {
        public (VectorId Id, float Distance)[] Results { get; set; } = Array.Empty<(VectorId, float)>();
        public List<TraceEvent> Events { get; } = new();
        public int Popped { get; set; }
        public int CandidatesAdded { get; set; }
        public int Scanned { get; set; }
        public string TerminateReason { get; set; } = "";
    }

    /// <summary>
    /// Queries with full debug tracing per spec 9.1 and 9.2.
    /// </summary>
    public QueryTraceResult QueryWithTrace(ReadOnlySpan<float> query, int k)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        if (query.Length != _store.Dimensions) throw new ArgumentException("Query dimension mismatch", nameof(query));

        var trace = new QueryTraceResult();
        var best = new BoundedMaxK(k);
        var visited = new HashSet<Node>();
        var enqueued = new HashSet<Node>();
        var pq = new PriorityQueue<Node, float>();

        // Start from root (seed)
        float rootDist = GetDistanceToNode(_root, query);
        pq.Enqueue(_root, rootDist);
        enqueued.Add(_root);
        trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(_root), rootDist, "seed"));
        trace.CandidatesAdded++;

        int maxVisits = Math.Max(k * 20, 200);
        int visits = 0;

        while (pq.TryDequeue(out var current, out var currentDist))
        {
            trace.Events.Add(new TraceEvent(TraceEventType.PopCandidate, GetNodeId(current), currentDist, ""));
            trace.Popped++;

            if (!visited.Add(current))
                continue;

            if (visits >= maxVisits)
            {
                trace.TerminateReason = "max_visits";
                trace.Events.Add(new TraceEvent(TraceEventType.Terminate, "", 0, "max_visits"));
                break;
            }

            visits++;
            trace.Events.Add(new TraceEvent(TraceEventType.SetCurrent, GetNodeId(current), currentDist, ""));

            if (current is VectorNode vn)
            {
                // Scan this vector
                float d = _metric.Distance(query, _store.GetVector(vn.VectorId));
                best.Add(vn.VectorId, d);
                trace.Events.Add(new TraceEvent(TraceEventType.ScanVector, $"V{vn.VectorId.Index}", d, ""));
                trace.Scanned++;

                // Expand to neighbors
                ExpandVectorNeighborsWithTrace(vn, query, pq, enqueued, best, trace);
            }
            else if (current is BucketNode bn)
            {
                // Expand children
                foreach (var child in bn.Children.Span)
                {
                    if (!enqueued.Add(child)) continue;
                    float childDist = GetDistanceToNode(child, query);
                    if (best.HasWorst && childDist > best.WorstDistance) continue;
                    pq.Enqueue(child, childDist);
                    trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(child), childDist, "child"));
                    trace.CandidatesAdded++;
                }

                // Expand outgoing neighbors
                foreach (var neighbor in bn.OutgoingNeighbors.Span)
                {
                    if (neighbor is null || !enqueued.Add(neighbor)) continue;
                    float neighborDist = GetDistanceToNode(neighbor, query);
                    if (best.HasWorst && neighborDist > best.WorstDistance) continue;
                    pq.Enqueue(neighbor, neighborDist);
                    trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(neighbor), neighborDist, "neighbor"));
                    trace.CandidatesAdded++;
                }

                // Expand incoming neighbors
                foreach (var neighbor in bn.IncomingNeighbors)
                {
                    if (!enqueued.Add(neighbor)) continue;
                    float neighborDist = GetDistanceToNode(neighbor, query);
                    if (best.HasWorst && neighborDist > best.WorstDistance) continue;
                    pq.Enqueue(neighbor, neighborDist);
                    trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(neighbor), neighborDist, "neighbor"));
                    trace.CandidatesAdded++;
                }
            }
        }

        if (string.IsNullOrEmpty(trace.TerminateReason))
        {
            trace.TerminateReason = "pq_empty";
            trace.Events.Add(new TraceEvent(TraceEventType.Terminate, "", 0, "pq_empty"));
        }

        trace.Results = best.ToSortedArray();
        return trace;
    }

    private void ExpandVectorNeighborsWithTrace(VectorNode start, ReadOnlySpan<float> query, PriorityQueue<Node, float> pq, HashSet<Node> enqueued, BoundedMaxK best, QueryTraceResult trace)
    {
        // First hop: direct neighbors
        foreach (var neighbor in start.OutgoingNeighbors.Span)
        {
            if (neighbor is null || !enqueued.Add(neighbor)) continue;
            float d = GetDistanceToNode(neighbor, query);
            if (best.HasWorst && d > best.WorstDistance) continue;
            pq.Enqueue(neighbor, d);
            trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(neighbor), d, "neighbor"));
            trace.CandidatesAdded++;

            // Second hop
            if (NeighborHops >= 2)
            {
                foreach (var neighbor2 in neighbor.OutgoingNeighbors.Span)
                {
                    if (neighbor2 is null || !enqueued.Add(neighbor2)) continue;
                    float d2 = GetDistanceToNode(neighbor2, query);
                    if (best.HasWorst && d2 > best.WorstDistance) continue;
                    pq.Enqueue(neighbor2, d2);
                    trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(neighbor2), d2, "neighbor"));
                    trace.CandidatesAdded++;
                }
            }
        }

        // Incoming neighbors
        foreach (var neighbor in start.IncomingNeighbors)
        {
            if (!enqueued.Add(neighbor)) continue;
            float d = GetDistanceToNode(neighbor, query);
            if (best.HasWorst && d > best.WorstDistance) continue;
            pq.Enqueue(neighbor, d);
            trace.Events.Add(new TraceEvent(TraceEventType.AddCandidate, GetNodeId(neighbor), d, "neighbor"));
            trace.CandidatesAdded++;
        }
    }

    private static string GetNodeId(Node node) => node switch
    {
        VectorNode vn => $"V{vn.VectorId.Index}",
        BucketNode bn => bn.Path,
        _ => "?"
    };

    private void ExpandVectorNeighbors(VectorNode start, ReadOnlySpan<float> query, PriorityQueue<Node, float> pq, HashSet<Node> enqueued, BoundedMaxK best)
    {
        // First hop: direct neighbors
        foreach (var neighbor in start.OutgoingNeighbors.Span)
        {
            if (neighbor is null || !enqueued.Add(neighbor)) continue;

            float d = GetDistanceToNode(neighbor, query);
            if (best.HasWorst && d > best.WorstDistance) continue;

            pq.Enqueue(neighbor, d);

            // Second hop: neighbors of neighbors
            if (NeighborHops >= 2)
            {
                foreach (var neighbor2 in neighbor.OutgoingNeighbors.Span)
                {
                    if (neighbor2 is null || !enqueued.Add(neighbor2)) continue;

                    float d2 = GetDistanceToNode(neighbor2, query);
                    if (best.HasWorst && d2 > best.WorstDistance) continue;

                    pq.Enqueue(neighbor2, d2);
                }
            }
        }

        // Also check incoming neighbors
        foreach (var neighbor in start.IncomingNeighbors)
        {
            if (!enqueued.Add(neighbor)) continue;

            float d = GetDistanceToNode(neighbor, query);
            if (best.HasWorst && d > best.WorstDistance) continue;

            pq.Enqueue(neighbor, d);
        }
    }

    private float GetDistanceToNode(Node node, ReadOnlySpan<float> query)
    {
        if (node is VectorNode vn)
        {
            return _metric.Distance(query, _store.GetVector(vn.VectorId));
        }
        else if (node is BucketNode bn && bn.Representative.Length > 0 && bn.Representative.Length == query.Length)
        {
            return _metric.Distance(query, bn.Representative);
        }
        return float.MaxValue; // Return large distance for empty representatives to deprioritize
    }

    // ----------------------------
    // Traversal for Insert
    // ----------------------------

    /// <summary>
    /// Traverses from root to find the best bucket for insertion.
    /// Returns the target bucket and the list of visited buckets for window linking.
    /// </summary>
    private (BucketNode Bucket, List<BucketNode> VisitedBuckets) TraverseForInsert(Node root, ReadOnlySpan<float> v)
    {
        var visitedBuckets = new List<BucketNode>(WindowSize);
        var pq = new PriorityQueue<Node, float>();
        var visited = new HashSet<Node>();

        float rootDist = GetDistanceToNode(root, v);
        pq.Enqueue(root, rootDist);

        Node? current = null;
        BucketNode? lastBucket = null;

        while (pq.TryDequeue(out var candidate, out var candidateDist))
        {
            if (!visited.Add(candidate))
                continue;

            current = candidate;

            // Only the winning candidate increments heat
            current.Heat++;

            if (current is BucketNode bucket)
            {
                lastBucket = bucket;

                // Track visited buckets for window linking
                if (visitedBuckets.Count < WindowSize)
                    visitedBuckets.Add(bucket);

                // Add children to candidates
                foreach (var child in bucket.Children.Span)
                {
                    if (visited.Contains(child)) continue;
                    float childDist = GetDistanceToNode(child, v);
                    pq.Enqueue(child, childDist);
                }

                // Add outgoing neighbors
                foreach (var neighbor in bucket.OutgoingNeighbors.Span)
                {
                    if (neighbor is null || visited.Contains(neighbor)) continue;
                    float neighborDist = GetDistanceToNode(neighbor, v);
                    pq.Enqueue(neighbor, neighborDist);
                }

                // Add incoming neighbors (allowed per spec)
                foreach (var neighbor in bucket.IncomingNeighbors)
                {
                    if (visited.Contains(neighbor)) continue;
                    float neighborDist = GetDistanceToNode(neighbor, v);
                    pq.Enqueue(neighbor, neighborDist);
                }
            }
            else if (current is VectorNode vn)
            {
                // Reached a vector - increment its heat and use its parent bucket
                if (current.Parent is BucketNode parentBucket)
                {
                    lastBucket = parentBucket;
                }
                break;
            }
        }

        // If we didn't find a bucket, use root
        if (lastBucket is null && root is BucketNode rootBucket)
        {
            lastBucket = rootBucket;
        }

        return (lastBucket!, visitedBuckets);
    }

    // ----------------------------
    // Window Linking
    // ----------------------------

    /// <summary>
    /// During traversal, create/replace bidirectional edges with vectors in visited buckets.
    /// </summary>
    private void ProcessWindowLinks(VectorNode newVector, List<BucketNode> visitedBuckets, ReadOnlySpan<float> v)
    {
        if (visitedBuckets.Count == 0)
            return;

        // Collect candidate vectors from visited buckets (excluding the new vector)
        var candidates = new List<(VectorNode Vector, float Distance)>();

        foreach (var bucket in visitedBuckets)
        {
            foreach (var child in bucket.Children.Span)
            {
                if (child is VectorNode vn && !ReferenceEquals(vn, newVector))
                {
                    float dist = _metric.Distance(v, _store.GetVector(vn.VectorId));
                    candidates.Add((vn, dist));
                }
            }
        }

        // Sort by distance and take top candidates for linking
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // Link with closest vectors (bounded by OutgoingNeighborCount)
        int linkCount = Math.Min(candidates.Count, OutgoingNeighborCount);
        for (int i = 0; i < linkCount; i++)
        {
            var (candidate, dist) = candidates[i];
            TryAddBidirectionalEdge(newVector, candidate, dist);
        }
    }

    /// <summary>
    /// Attempts to create a bidirectional edge between two nodes.
    /// Outgoing degree is bounded; incoming degree is unbounded.
    /// </summary>
    private void TryAddBidirectionalEdge(VectorNode a, VectorNode b, float distance)
    {
        // Add b to a's outgoing (bounded)
        a.OutgoingDistances.SortedInsertWithMirror(
            ref a.OutgoingNeighbors,
            OutgoingNeighborCount,
            distance,
            b);

        // Add a to b's outgoing (bounded)
        b.OutgoingDistances.SortedInsertWithMirror(
            ref b.OutgoingNeighbors,
            OutgoingNeighborCount,
            distance,
            a);

        // Add to incoming lists (unbounded)
        if (!a.IncomingNeighbors.Contains(b))
            a.IncomingNeighbors.Add(b);

        if (!b.IncomingNeighbors.Contains(a))
            b.IncomingNeighbors.Add(a);
    }

    /// <summary>
    /// Adds a neighbor edge from one bucket to another.
    /// Outgoing is bounded; incoming is unbounded.
    /// </summary>
    private void AddBucketNeighborEdge(BucketNode from, BucketNode to, float distance)
    {
        // Add to outgoing (bounded)
        from.OutgoingDistances.SortedInsertWithMirror(
            ref from.OutgoingNeighbors,
            OutgoingNeighborCount,
            distance,
            to);

        // Add to incoming (unbounded)
        if (!to.IncomingNeighbors.Contains(from))
            to.IncomingNeighbors.Add(from);
    }

    // ----------------------------
    // BLAST (Bucketization)
    // ----------------------------

    /// <summary>
    /// BLAST is the only structural reorganization mechanism.
    /// Triggered when a bucket exceeds capacity.
    /// </summary>
    private void Blast(BucketNode bucket)
    {
        if (bucket.ChildCount <= BucketCapacity)
            return;

        // Select epicenter: prefer hottest nodes in ≥50th percentile distance range
        var epicenter = SelectEpicenter(bucket);
        if (epicenter is null)
            return;

        // Collect candidates: bucket children + neighbor traversal
        var candidates = CollectBlastCandidates(bucket, epicenter);

        // Filter to eligible candidates (strictly improving)
        var eligible = FilterEligibleCandidates(candidates, epicenter, bucket);

        if (eligible.Count == 0)
            return;

        // Create new bucket with epicenter as representative
        ReadOnlySpan<float> epicenterVector = epicenter is VectorNode evn
            ? _store.GetVector(evn.VectorId)
            : ((BucketNode)epicenter).Representative;

        var newBucket = NewBucket(bucket.Parent, epicenterVector);

        // Reparent eligible candidates to new bucket
        foreach (var candidate in eligible)
        {
            // Remove from old parent
            if (candidate.Parent is BucketNode oldParent)
            {
                oldParent.RemoveChild(candidate);
            }

            // Add to new bucket
            newBucket.AddChild(candidate);
        }

        // Resort children lists by distance to representative
        bucket.SortChildrenByDistance(this);
        newBucket.SortChildrenByDistance(this);

        // Create bidirectional neighbor link between bucket and newBucket
        // This ensures the graph stays connected after BLAST
        if (bucket.Representative.Length > 0 && newBucket.Representative.Length > 0)
        {
            float bucketDist = _metric.Distance(bucket.Representative, newBucket.Representative);
            AddBucketNeighborEdge(bucket, newBucket, bucketDist);
            AddBucketNeighborEdge(newBucket, bucket, bucketDist);
        }

        // If bucket's parent exists, add new bucket as sibling
        if (bucket.Parent is BucketNode parent)
        {
            parent.AddChild(newBucket);

            // If parent overflows, propagate BLAST upward
            if (parent.ChildCount > BucketCapacity)
            {
                Blast(parent);
            }
        }
        else
        {
            // bucket was root - create new root
            var newRoot = NewBucket(parent: null, representativeVector: ReadOnlySpan<float>.Empty);
            newRoot.AddChild(bucket);
            newRoot.AddChild(newBucket);
            _root = newRoot;
        }
    }

    /// <summary>
    /// Selects the epicenter for BLAST: hottest node in ≥50th percentile distance range.
    /// </summary>
    private Node? SelectEpicenter(BucketNode bucket)
    {
        if (bucket.ChildCount == 0)
            return null;

        var children = bucket.Children.Span;

        // If bucket has no representative, just pick hottest
        if (bucket.Representative.Length == 0)
        {
            Node? hottest = null;
            int maxHeat = -1;
            foreach (var child in children)
            {
                if (child.Heat > maxHeat)
                {
                    maxHeat = child.Heat;
                    hottest = child;
                }
            }
            return hottest;
        }

        // Compute distances to representative
        var distances = new List<(Node Node, float Distance)>(children.Length);
        foreach (var child in children)
        {
            float dist = GetDistanceToNode(child, bucket.Representative);
            distances.Add((child, dist));
        }

        // Sort by distance
        distances.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // Get ≥50th percentile
        int midIndex = distances.Count / 2;
        var candidateRange = distances.Skip(midIndex).ToList();

        if (candidateRange.Count == 0)
            candidateRange = distances;

        // Pick hottest in range
        Node? epicenter = null;
        int bestHeat = -1;
        foreach (var (node, _) in candidateRange)
        {
            if (node.Heat > bestHeat)
            {
                bestHeat = node.Heat;
                epicenter = node;
            }
        }

        return epicenter;
    }

    /// <summary>
    /// Collects candidates via bucket children + neighbor traversal (1-2 hops).
    /// </summary>
    private List<(Node Node, float Distance)> CollectBlastCandidates(BucketNode bucket, Node epicenter)
    {
        ReadOnlySpan<float> epicenterVector = epicenter is VectorNode evn
            ? _store.GetVector(evn.VectorId)
            : ((BucketNode)epicenter).Representative;

        var candidates = new Dictionary<Node, float>();
        var visited = new HashSet<Node>();

        // Add bucket children
        foreach (var child in bucket.Children.Span)
        {
            if (ReferenceEquals(child, epicenter)) continue;

            float dist = GetDistanceToNode(child, epicenterVector);
            candidates[child] = dist;
            visited.Add(child);
        }

        // 1-2 hops of neighbor traversal from epicenter
        if (epicenter is VectorNode epicenterVn)
        {
            // First hop
            foreach (var neighbor in epicenterVn.OutgoingNeighbors.Span)
            {
                if (neighbor is null || visited.Contains(neighbor)) continue;
                visited.Add(neighbor);

                float dist = GetDistanceToNode(neighbor, epicenterVector);
                candidates[neighbor] = dist;

                // Second hop
                if (neighbor is VectorNode neighborVn && NeighborHops >= 2)
                {
                    foreach (var neighbor2 in neighborVn.OutgoingNeighbors.Span)
                    {
                        if (neighbor2 is null || visited.Contains(neighbor2)) continue;
                        visited.Add(neighbor2);

                        float dist2 = GetDistanceToNode(neighbor2, epicenterVector);
                        candidates[neighbor2] = dist2;
                    }
                }
            }

            foreach (var neighbor in epicenterVn.IncomingNeighbors)
            {
                if (visited.Contains(neighbor)) continue;
                visited.Add(neighbor);

                float dist = GetDistanceToNode(neighbor, epicenterVector);
                candidates[neighbor] = dist;
            }
        }

        // Return sorted by distance (best-K)
        var result = candidates.Select(kvp => (kvp.Key, kvp.Value)).ToList();
        result.Sort((a, b) => a.Value.CompareTo(b.Value));

        return result;
    }

    /// <summary>
    /// Filters candidates to those eligible for reparenting.
    /// A candidate is eligible only if: dist(candidate, epicenter) &lt; dist(candidate, current_parent)
    /// </summary>
    private List<Node> FilterEligibleCandidates(List<(Node Node, float Distance)> candidates, Node epicenter, BucketNode sourceBucket)
    {
        var eligible = new List<Node>();

        ReadOnlySpan<float> epicenterVector = epicenter is VectorNode evn
            ? _store.GetVector(evn.VectorId)
            : ((BucketNode)epicenter).Representative;

        foreach (var (candidate, distToEpicenter) in candidates)
        {
            if (ReferenceEquals(candidate, epicenter))
                continue;

            // Get distance to current parent
            float distToParent = float.PositiveInfinity;
            if (candidate.Parent is BucketNode parentBucket && parentBucket.Representative.Length > 0)
            {
                distToParent = GetDistanceToNode(candidate, parentBucket.Representative);
            }

            // Strictly improving only
            if (distToEpicenter < distToParent)
            {
                eligible.Add(candidate);
            }
        }

        return eligible;
    }

    // ----------------------------
    // Node Types
    // ----------------------------

    public record struct NodeId(int Id)
    {
        public static implicit operator int(NodeId n) => n.Id;
    }

    /// <summary>
    /// Base class for all nodes in the index.
    /// </summary>
    public abstract class Node
    {
        public NodeId NodeId;
        public BucketNode? Parent;
        public int IndexInParent = -1;

        /// <summary>Heat counter - incremented on winning traversal steps.</summary>
        public int Heat;

        /// <summary>Hierarchical path ID for debugging.</summary>
        public string Path => field ??= Parent is null ? $"/{NodeId.Id}" : $"{Parent.Path}/{NodeId.Id}";

        public override string ToString() => Path;

        public bool IsDisposed;
    }

    /// <summary>
    /// Represents a single vector (level 0).
    /// </summary>
    public sealed class VectorNode : Node
    {
        public VectorId VectorId { get; }

        /// <summary>Bounded outgoing neighbor edges.</summary>
        public StructArrayBuilder<VectorNode?> OutgoingNeighbors;
        public StructArrayBuilder<float> OutgoingDistances;

        /// <summary>Unbounded incoming neighbor edges.</summary>
        public List<VectorNode> IncomingNeighbors = new();

        public VectorNode(NodeId nodeId, VectorId vectorId, int outgoingNeighborCount)
        {
            NodeId = nodeId;
            VectorId = vectorId;
            OutgoingNeighbors = new(outgoingNeighborCount);
            OutgoingDistances = new(outgoingNeighborCount);
        }
    }

    /// <summary>
    /// Represents a bucket/routing node (level ≥1).
    /// </summary>
    public sealed class BucketNode : Node
    {
        /// <summary>Static representative vector (epicenter of blast). Not updated incrementally.</summary>
        public float[] Representative { get; private set; }

        /// <summary>Children nodes (vectors or buckets), sorted by distance to representative.</summary>
        public ArrayBuilder<Node> Children = new();

        /// <summary>Distances of children to representative.</summary>
        public ArrayBuilder<float> ChildrenDistances = new();

        /// <summary>Bounded outgoing neighbor edges.</summary>
        public StructArrayBuilder<BucketNode?> OutgoingNeighbors;
        public StructArrayBuilder<float> OutgoingDistances;

        /// <summary>Unbounded incoming neighbor edges.</summary>
        public List<BucketNode> IncomingNeighbors = new();

        public int ChildCount => Children.Count;

        public BucketNode(NodeId nodeId, ReadOnlySpan<float> representative, int outgoingNeighborCount)
        {
            NodeId = nodeId;
            Representative = representative.Length > 0 ? representative.ToArray() : Array.Empty<float>();
            OutgoingNeighbors = new(outgoingNeighborCount);
            OutgoingDistances = new(outgoingNeighborCount);
        }

        public void AddChild(Node child)
        {
            child.IndexInParent = Children.Count;
            child.Parent = this;
            Children.Add(child);

            // Compute distance to representative
            float dist = 0f;
            if (Representative.Length > 0)
            {
                if (child is VectorNode vn)
                {
                    // Need access to store - will be set later via SortChildrenByDistance
                    dist = 0f;
                }
                else if (child is BucketNode bn && bn.Representative.Length > 0)
                {
                    // Approximate - will be recomputed
                    dist = 0f;
                }
            }
            ChildrenDistances.Add(dist);
        }

        public void RemoveChild(Node child)
        {
            // Verify child actually belongs to this bucket
            if (!ReferenceEquals(child.Parent, this))
                return;

            int index = child.IndexInParent;
            if (index < 0 || index >= Children.Count)
                return;

            // Verify the index actually points to this child
            if (!ReferenceEquals(Children[index], child))
                return;

            // Ensure arrays are in sync
            if (Children.Count != ChildrenDistances.Count)
                return;

            // Swap with last and remove using SetLength to avoid ArrayBuilder.RemoveAt bug
            int lastIndex = Children.Count - 1;
            if (index < lastIndex)
            {
                Children[index] = Children[lastIndex];
                ChildrenDistances[index] = ChildrenDistances[lastIndex];
                Children[index].IndexInParent = index;
            }

            // Reduce length instead of RemoveAt to avoid the bug in ArrayBuilder.RemoveAt
            // that does CheckRange(index - 1) instead of CheckRange(index)
            Children.Length = lastIndex;
            ChildrenDistances.Length = lastIndex;

            child.Parent = null;
            child.IndexInParent = -1;
        }

        /// <summary>
        /// Sorts children by distance to representative.
        /// </summary>
        public void SortChildrenByDistance(BlastIndex index)
        {
            if (Representative.Length == 0 || Children.Count == 0)
                return;

            // Recompute distances
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                float dist;
                if (child is VectorNode vn)
                {
                    dist = index._metric.Distance(Representative, index._store.GetVector(vn.VectorId));
                }
                else if (child is BucketNode bn && bn.Representative.Length > 0)
                {
                    dist = index._metric.Distance(Representative, bn.Representative);
                }
                else
                {
                    dist = 0f;
                }
                ChildrenDistances[i] = dist;
            }

            // Sort by distance (simple bubble sort for small arrays, or use span sort)
            var distances = ChildrenDistances.Span;
            var children = Children.Span;

            // Use Array.Sort on underlying arrays
            var distArray = ChildrenDistances.Buffer![..Children.Count];
            var childArray = Children.Buffer![..Children.Count];

            Array.Sort(distArray, childArray);

            // Update IndexInParent
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].IndexInParent = i;
            }
        }
    }

    // ----------------------------
    // Node Allocation
    // ----------------------------

    private VectorNode NewVectorNode(VectorId vectorId)
    {
        var node = new VectorNode(
            nodeId: new NodeId(_vectors.Count),
            vectorId: vectorId,
            outgoingNeighborCount: OutgoingNeighborCount);

        _vectors.Add(node);
        return node;
    }

    private BucketNode NewBucket(BucketNode? parent, ReadOnlySpan<float> representativeVector)
    {
        var bucket = new BucketNode(
            nodeId: new NodeId(_buckets.Count),
            representative: representativeVector,
            outgoingNeighborCount: OutgoingNeighborCount)
        {
            Parent = parent
        };

        _buckets.Add(bucket);
        return bucket;
    }

    // ----------------------------
    // Query top-k helper
    // ----------------------------

    private sealed class BoundedMaxK
    {
        private readonly int _k;
        private readonly PriorityQueue<VectorId, float> _pq;

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
            var maxComparer = Comparer<float>.Create((a, b) => b.CompareTo(a));
            _pq = new PriorityQueue<VectorId, float>(k, maxComparer);
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
