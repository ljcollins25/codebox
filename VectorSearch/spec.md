# IHCI Tree – Design Specification

## 1. Purpose and Scope

The IHCI Tree is a spatial partitioning data structure for approximate nearest‑neighbor search over high‑dimensional vector embeddings. It is designed to be:

* Incrementally maintainable under insert‑only workloads
* Storage‑friendly (flat vector store + tree metadata)
* Robust to delayed / partial maintenance via explicit repair
* Amenable to pruning via centroid + radius bounds

This specification defines **structure, invariants, and protocols** only. It intentionally avoids low‑level implementation detail.

---

## 2. Core Concepts

### 2.1 Vector Store

* Vectors are stored externally in an immutable `IReadOnlyVectorStore`
* Tree nodes store only `VectorId` references
* All distance computations are delegated to an `IMetricModel`

---

## 3. Node Model

### 3.1 Node Types

There are exactly two node types:

#### LeafNode

* Stores a bounded list of `VectorId`
* Represents a local spatial region
* Has a centroid and an upper‑bound radius
* Maintains a neighbor list (local graph edges)

#### RoutingNode

* Stores references to child nodes (2–16 typical)
* Represents a union of child regions
* Has a centroid and an upper‑bound radius
* Does **not** store vectors directly

Both node types share a common abstract base: `Node`.

---

### 3.2 Common Node Fields

Each node has:

* `Parent : Node?`
* `Centroid : float[]` (may be stale)
* `RadiusUpperBound : float`
* `DescendantCount : int`
* `IsQueuedForRepair : bool`

Invariants:

* `RadiusUpperBound` ≥ true maximum distance to any descendant vector
* `DescendantCount` equals sum of descendants

---

## 4. Tree Structure

* The tree is rooted; root may be a LeafNode or RoutingNode
* Leaves only split into **two** children at a time
* RoutingNodes may accumulate multiple children via descendant splits
* Parent pointers are always consistent

The tree is **acyclic** and strictly hierarchical.

---

## 5. Capacity Rules

### 5.1 Leaf Capacity

* Default: 128 vectors
* Configurable (expected range 64–256)

### 5.2 Routing Node Capacity

* Default max children: 16
* If exceeded, the routing node itself splits recursively

---

## 6. Split Protocol

### 6.1 When a Leaf Overflows

1. Compute an approximate centroid from contained vectors
2. Select two seed vectors (farthest‑pair heuristic)
3. Partition vectors by nearest seed
4. Create two new LeafNodes
5. Create or update parent RoutingNode
6. Schedule repair for both children and parent

### 6.2 When a RoutingNode Overflows

* Treat child centroids as points
* Apply the same 2‑way split logic
* Replace the node with a new parent if necessary

---

## 7. Centroid and Radius Management

### 7.1 Centroid Policy

* **Not updated on every insert**
* Updated during repair
* May be recomputed exactly or approximately

### 7.2 Radius Policy

* Updated incrementally on insert
* Always conservative (overestimate allowed)
* Propagates upward only if containment fails

Containment test:

* If child sphere ⊆ parent sphere → stop propagation
* Else → expand parent radius and continue upward

---

## 8. Repair System

### 8.1 Repair Queue

* Explicit queue of nodes needing repair
* Each node has `IsQueuedForRepair` guard

### 8.2 When Nodes Are Enqueued

* Leaf split
* Routing split
* Radius expansion
* Structural change (child added/removed)

### 8.3 Repair Execution

* Repairs are **incremental and synchronous**
* Triggered after N insertions (configurable)
* Each repair processes exactly one node

### 8.4 Repair Actions

For a node:

1. Recompute centroid
2. Recompute exact radius
3. Update neighbors (leaf only)
4. Validate containment in parent
5. Enqueue parent if needed

---

## 9. Neighbor Graph (Leaf‑Only)

### 9.1 Purpose

* Improve local recall
* Enable lateral exploration during query
* Reduce reliance on deep tree traversal

### 9.2 Neighbor Properties

* Default count: 8
* Asymmetric allowed
* Stored only on LeafNodes

### 9.3 Neighbor Maintenance

* On leaf split: inherit neighbors
* During repair: search locally (depth‑2 graph walk)
* Update mutual links opportunistically

---

## 10. Query Algorithm

### 10.1 Objective

Find top‑K nearest vectors using pruning + local expansion.

### 10.2 Query State

* Priority queue of candidate nodes (by lower‑bound distance)
* Top‑K result heap
* Visited set of VectorIds

### 10.3 Traversal

1. Start at root
2. At routing node:

   * Rank children by centroid distance
   * Descend into best child
   * Prune if centroid distance − radius > worst result
3. At leaf:

   * Evaluate all vectors
   * Expand to neighbors

### 10.4 Pruning Rule

A node may be pruned if:

```
Distance(query, node.centroid) − node.radius > currentWorst
```

---

## 11. Metric Requirements

The metric must:

* Support distance computation
* Obey triangle inequality (for pruning correctness)
* Work with centroids (L2 or cosine typical)

---

## 12. Non‑Goals (Explicit)

* No pivots or projections
* No concurrent mutation guarantees
* No deletion support (insert‑only)
* No background repair threads

---

## 13. Design Intent Summary

The IHCI Tree prioritizes:

* Correctness over immediacy
* Explicit maintenance over hidden heuristics
* Structural clarity over clever routing

All performance optimizations must preserve invariants defined above.
