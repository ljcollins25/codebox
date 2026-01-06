#nullable enable
using VectorSearch;
using Xunit;

namespace VectorSearch.Tests;

using static Helpers;

/// <summary>
/// Tests for the Blast-Driven Hierarchical Graph Index.
/// </summary>
public class BlastIndexTests
{
    /// <summary>
    /// Tests the BlastIndex with a synthetic random dataset.
    /// </summary>
    [Theory]
    [InlineData(100, 8, 5)]
    [InlineData(500, 16, 10)]
    [InlineData(1000, 32, 20)]
    [InlineData(2000, 64, 50)]
    public void TestSyntheticDataset(int vectorCount, int dimensions, int k)
    {
        // Generate random vectors
        var rng = new Random(42);
        var vectors = new float[vectorCount, dimensions];

        for (int i = 0; i < vectorCount; i++)
        {
            for (int j = 0; j < dimensions; j++)
            {
                vectors[i, j] = (float)(rng.NextDouble() * 2.0 - 1.0);
            }
        }

        var store = new VectorBlockArrayStore(vectors);
        var metric = new L2FloatArrayMetric(dimensions);
        var index = new BlastIndex(
            metric,
            store,
            bucketCapacity: 32,
            outgoingNeighborCount: 8,
            neighborHops: 2,
            windowSize: 4);

        // Insert all vectors
        for (int i = 0; i < vectorCount; i++)
        {
            index.Insert(i);
        }

        // Generate random query
        var query = new float[dimensions];
        for (int j = 0; j < dimensions; j++)
        {
            query[j] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        // Query the index with tracing
        var traceResult = index.QueryWithTrace(query, k);
        var results = traceResult.Results;

        // Compute brute-force results for comparison
        var bruteForce = new List<(int Id, float Distance)>();
        for (int i = 0; i < vectorCount; i++)
        {
            var v = store.GetVector(i);
            var dist = metric.Distance(query, v);
            bruteForce.Add((i, dist));
        }
        bruteForce.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var expected = bruteForce.Take(k).ToList();

        // Verify results
        Assert.Equal(k, results.Length);

        // Results should be sorted by distance
        for (int i = 1; i < results.Length; i++)
        {
            Assert.True(results[i - 1].Distance <= results[i].Distance,
                $"Results not sorted at index {i}");
        }

        // Calculate recall@k
        var resultIds = new HashSet<int>(results.Select(r => r.Id.Index));
        var expectedIds = new HashSet<int>(expected.Select(e => e.Id));
        var intersection = resultIds.Intersect(expectedIds).Count();
        var recall = (float)intersection / k;

        // Recall threshold varies by data complexity
        float minRecall = vectorCount <= 500 ? 0.3f : 0.05f;

        Console.WriteLine($"BlastIndex test: {vectorCount} vectors, {dimensions}D, k={k}, Recall@{k}={recall:P2}");
        Console.WriteLine($"  Buckets: {index.Buckets().Count()}, Vectors: {index.Vectors().Count()}");
        Console.WriteLine();

        // Event log per spec 9.1
        Console.WriteLine("=== Event Log (spec 9.1) ===");
        foreach (var evt in traceResult.Events)
        {
            var line = evt.Type switch
            {
                BlastIndex.TraceEventType.PopCandidate => $"popCandidate id={evt.Id} dist={evt.Distance:F6}",
                BlastIndex.TraceEventType.SetCurrent => $"setCurrent  id={evt.Id} dist={evt.Distance:F6}",
                BlastIndex.TraceEventType.AddCandidate => $"addCandidate id={evt.Id} dist={evt.Distance:F6} reason={evt.Reason}",
                BlastIndex.TraceEventType.ScanVector => $"scanVector id={evt.Id} dist={evt.Distance:F6}",
                BlastIndex.TraceEventType.Terminate => $"terminate reason={evt.Reason}",
                _ => ""
            };
            Console.WriteLine(line);
        }
        Console.WriteLine();

        // End dumps per spec 9.2
        Console.WriteLine("=== End Dumps (spec 9.2) ===");
        Console.WriteLine($"topK k={k}");
        for (int i = 0; i < results.Length; i++)
        {
            Console.WriteLine($"  {i + 1}) {results[i].Id.Index} dist={results[i].Distance:F6}");
        }

        // Expected (brute force) for comparison
        Console.WriteLine($"expected k={k}");
        for (int i = 0; i < Math.Min(k, expected.Count); i++)
        {
            var inResult = resultIds.Contains(expected[i].Id) ? "✓" : "✗";
            Console.WriteLine($"  {i + 1}) {expected[i].Id} dist={expected[i].Distance:F6} {inResult}");
        }

        Console.WriteLine($"stats popped={traceResult.Popped} candidatesAdded={traceResult.CandidatesAdded} scanned={traceResult.Scanned}");
        Console.WriteLine($"stats recall={recall:P2} matched={intersection}/{k}");
        Console.WriteLine();

        // Adjacency dump per spec 9.3
        PrintAdjacencyDump(index, metric, store);

        Assert.True(recall >= minRecall,
            $"Recall@{k} = {recall:P2} is too low. Expected at least {minRecall:P0}.");
    }

    /// <summary>
    /// Tests basic insert and query operations.
    /// </summary>
    [Fact]
    public void TestBasicOperations()
    {
        var vectors = new float[,]
        {
            { 1.0f, 0.0f },
            { 0.0f, 1.0f },
            { 1.0f, 1.0f },
            { -1.0f, 0.0f },
            { 0.0f, -1.0f }
        };

        var store = new VectorBlockArrayStore(vectors);
        var metric = new L2FloatArrayMetric(2);
        var index = new BlastIndex(metric, store, bucketCapacity: 4, outgoingNeighborCount: 4);

        // Insert all vectors
        for (int i = 0; i < 5; i++)
        {
            index.Insert(i);
        }

        // Query for nearest to (0.9, 0.1)
        var query = new float[] { 0.9f, 0.1f };
        var results = index.Query(query, 2);

        Assert.Equal(2, results.Length);

        // First result should be vector 0 (1.0, 0.0) - closest to query
        Assert.Equal(0, results[0].Id.Index);

        Console.WriteLine("Basic operations test passed.");
    }

    /// <summary>
    /// Tests BLAST triggering and bucket creation.
    /// </summary>
    [Fact]
    public void TestBlastTriggering()
    {
        var rng = new Random(123);
        int vectorCount = 200;
        int dimensions = 4;

        var vectors = new float[vectorCount, dimensions];
        for (int i = 0; i < vectorCount; i++)
        {
            for (int j = 0; j < dimensions; j++)
            {
                vectors[i, j] = (float)(rng.NextDouble() * 2.0 - 1.0);
            }
        }

        var store = new VectorBlockArrayStore(vectors);
        var metric = new L2FloatArrayMetric(dimensions);

        // Small bucket capacity to trigger BLAST frequently
        var index = new BlastIndex(metric, store, bucketCapacity: 16, outgoingNeighborCount: 8);

        // Insert all vectors
        for (int i = 0; i < vectorCount; i++)
        {
            index.Insert(i);
        }

        // Should have created multiple buckets
        var bucketCount = index.Buckets().Count();
        Assert.True(bucketCount > 1, $"Expected multiple buckets, got {bucketCount}");

        Console.WriteLine($"BLAST test: {vectorCount} vectors created {bucketCount} buckets");

        // All vectors should still be queryable
        var query = new float[dimensions];
        for (int j = 0; j < dimensions; j++)
        {
            query[j] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var results = index.Query(query, 10);
        Assert.Equal(10, results.Length);

        Console.WriteLine("BLAST triggering test passed.");
    }

    /// <summary>
    /// Tests heat accumulation during traversal.
    /// </summary>
    [Fact]
    public void TestHeatAccumulation()
    {
        var vectors = new float[,]
        {
            { 0.0f, 0.0f },
            { 1.0f, 0.0f },
            { 0.0f, 1.0f },
            { 1.0f, 1.0f },
            { 0.5f, 0.5f }
        };

        var store = new VectorBlockArrayStore(vectors);
        var metric = new L2FloatArrayMetric(2);
        var index = new BlastIndex(metric, store, bucketCapacity: 10, outgoingNeighborCount: 4);

        // Insert all vectors
        for (int i = 0; i < 5; i++)
        {
            index.Insert(i);
        }

        // Check that some nodes have accumulated heat
        var totalHeat = index.Vectors().Sum(v => v.Heat) + index.Buckets().Sum(b => b.Heat);
        Assert.True(totalHeat > 0, "Expected some heat accumulation");

        Console.WriteLine($"Heat accumulation test: total heat = {totalHeat}");
        Console.WriteLine("Heat accumulation test passed.");
    }

    /// <summary>
    /// Tests neighbor graph construction.
    /// </summary>
    [Fact]
    public void TestNeighborGraph()
    {
        var rng = new Random(456);
        int vectorCount = 50;
        int dimensions = 4;

        var vectors = new float[vectorCount, dimensions];
        for (int i = 0; i < vectorCount; i++)
        {
            for (int j = 0; j < dimensions; j++)
            {
                vectors[i, j] = (float)(rng.NextDouble() * 2.0 - 1.0);
            }
        }

        var store = new VectorBlockArrayStore(vectors);
        var metric = new L2FloatArrayMetric(dimensions);
        var index = new BlastIndex(metric, store, bucketCapacity: 32, outgoingNeighborCount: 4, windowSize: 4);

        // Insert all vectors
        for (int i = 0; i < vectorCount; i++)
        {
            index.Insert(i);
        }

        // Check that neighbor edges were created
        int totalOutgoing = index.Vectors().Sum(v => v.OutgoingNeighbors.Count);
        int totalIncoming = index.Vectors().Sum(v => v.IncomingNeighbors.Count);

        Console.WriteLine($"Neighbor graph: {totalOutgoing} outgoing edges, {totalIncoming} incoming edges");

        // Should have some edges (window linking should create them)
        Assert.True(totalOutgoing > 0 || totalIncoming > 0, "Expected neighbor edges to be created");

        Console.WriteLine("Neighbor graph test passed.");
    }

    /// <summary>
    /// Prints the adjacency dump per spec section 9.3.
    /// </summary>
    private static void PrintAdjacencyDump(BlastIndex index, L2FloatArrayMetric metric, VectorBlockArrayStore store)
    {
        Console.WriteLine("=== Adjacency Dump ===");

        // Print buckets (routing nodes)
        foreach (var bucket in index.Buckets())
        {
            Console.WriteLine($"NODE {bucket.Path}");

            // Children with distances
            var children = new List<string>();
            foreach (var child in bucket.Children.Segment)
            {
                float dist;
                if (child is BlastIndex.VectorNode vn)
                {
                    dist = bucket.Representative.Length > 0
                        ? metric.Distance(bucket.Representative, store.GetVector(vn.VectorId))
                        : 0f;
                    children.Add($"V{vn.VectorId.Index}({dist:F3})");
                }
                else if (child is BlastIndex.BucketNode bn)
                {
                    dist = (bucket.Representative.Length > 0 && bn.Representative.Length > 0)
                        ? metric.Distance(bucket.Representative, bn.Representative)
                        : 0f;
                    children.Add($"B{bn.NodeId.Id}({dist:F3})");
                }
            }
            Console.WriteLine($"  children:  {(children.Count > 0 ? string.Join(" ", children) : "(none)")}");

            // Outgoing neighbors with distances
            var outgoing = new List<string>();
            for (int i = 0; i < bucket.OutgoingNeighbors.Count; i++)
            {
                var neighbor = bucket.OutgoingNeighbors[i];
                if (neighbor is not null && bucket.Representative.Length > 0 && neighbor.Representative.Length > 0)
                {
                    float dist = metric.Distance(bucket.Representative, neighbor.Representative);
                    outgoing.Add($"B{neighbor.NodeId.Id}({dist:F3})");
                }
            }
            Console.WriteLine($"  outgoing:  {(outgoing.Count > 0 ? string.Join(" ", outgoing) : "(none)")}");

            // Incoming neighbors with distances
            var incoming = new List<string>();
            foreach (var neighbor in bucket.IncomingNeighbors)
            {
                if (bucket.Representative.Length > 0 && neighbor.Representative.Length > 0)
                {
                    float dist = metric.Distance(bucket.Representative, neighbor.Representative);
                    incoming.Add($"B{neighbor.NodeId.Id}({dist:F3})");
                }
            }
            Console.WriteLine($"  incoming:  {(incoming.Count > 0 ? string.Join(" ", incoming) : "(none)")}");
        }

        // Print vectors
        foreach (var vector in index.Vectors())
        {
            Console.WriteLine($"VECTOR V{vector.VectorId.Index}");

            // Outgoing neighbors with distances (recomputed per spec)
            var outgoing = new List<string>();
            for (int i = 0; i < vector.OutgoingNeighbors.Count; i++)
            {
                var neighbor = vector.OutgoingNeighbors[i];
                if (neighbor is not null)
                {
                    float dist = metric.Distance(store.GetVector(vector.VectorId), store.GetVector(neighbor.VectorId));
                    outgoing.Add($"V{neighbor.VectorId.Index}({dist:F3})");
                }
            }
            Console.WriteLine($"  outgoing: {(outgoing.Count > 0 ? string.Join(" ", outgoing) : "(none)")}");

            // Incoming neighbors with distances (recomputed per spec)
            var incoming = new List<string>();
            foreach (var neighbor in vector.IncomingNeighbors)
            {
                float dist = metric.Distance(store.GetVector(vector.VectorId), store.GetVector(neighbor.VectorId));
                incoming.Add($"V{neighbor.VectorId.Index}({dist:F3})");
            }
            Console.WriteLine($"  incoming: {(incoming.Count > 0 ? string.Join(" ", incoming) : "(none)")}");
        }

        Console.WriteLine("=== End Adjacency Dump ===");
        Console.WriteLine();
    }
}
