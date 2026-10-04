# FsDedup

Finds files that are byte-for-byte identical inside a folder tree and replaces the duplicates with **ReFS block
clones** of one original, so they share clusters and cost no extra space. Windows only; the folder must be on a ReFS
volume (Windows Server, or a Dev Drive). Whole-file dedup only: no chunking.

```
fsdedup <folder> [--what-if] [--min-size N] [--threads N] [--cache path] [--verbose] [--json]
                 [--settle N] [--mode clone|hardlink]
```

| option | meaning |
| --- | --- |
| `--what-if` | Report the groups, the files that would be replaced and the bytes that would be freed. Writes nothing except the hash cache. |
| `--min-size N` | Ignore files smaller than N bytes (`64K`, `1M`, `2G` allowed). Default 64 KB; never below one cluster. |
| `--threads N` | Files hashed in parallel. Default min(4, cores). |
| `--cache path` | Hash cache file. Default `<folder>\.fsdedup-cache.jsonl`. |
| `--verbose` | List every group and every action. |
| `--json` | Print the report as JSON on stdout (progress goes to stderr). |
| `--settle N` | Longest wait (default 30 s) for the volume's free space to show what was freed; ends early once 90% is back. ReFS gives freed clusters back 10-15 s late. 0 reads it at once. |
| `--mode clone` | ReFS block cloning (default). |
| `--mode hardlink` | Replace each duplicate with a hard link to its original. Works on NTFS and ReFS. |


Exit code: 0 ok, 1 the run finished but reported errors, 2 bad arguments or unsupported filesystem for selected mode.

## What it does

1. **Walk** the tree. Reparse points (junctions, symlinks) are not followed, and files that are reparse points are
   skipped. Skipped, and counted by reason: link count above 1 (never touched, as source or target), encrypted (EFS),
   below `--min-size`, on another volume, unreadable or locked, and the tool's own files (`.fsdedup-*`).
2. **Group by size.** A size seen once is never read.
3. **Partial hash** (first and last 64 KB, xxHash-128, System.IO.Hashing) splits each size group.
4. **Full hash** only for survivors: 1 MB sequential reads, bounded parallelism (`--threads`).
5. **Hash cache**: JSON lines, versioned, written atomically, keyed by volume + 128-bit file ID + size + last-write
   time. A rerun reads only changed files.
6. **Original** per group: oldest by creation time, ties by path. Groups are split so no original has more than 8175
   referencing files, ReFS's limit of file regions per physical cluster (Microsoft, "Block cloning on ReFS",
   restrictions; one clone call must also be under 4 GB). If a clone still fails with ERROR_BLOCK_TOO_MANY_REFERENCES
   (the original was cloned in earlier runs), the duplicate that hit it becomes the next original.
7. **Already shared** duplicates (every whole cluster maps to the original's clusters, compared with
   FSCTL_GET_RETRIEVAL_POINTERS) are skipped, so reruns do no work.

### Replacing a duplicate D with a clone of the original C

1. D is opened without write sharing and held to the end. Its file ID, size and last-write time must still match the
   hash; link count, reparse, encryption and read-only are re-checked.
2. A hidden temp file T in D's folder gets C's integrity-stream and sparse settings and C's length, then
   FSCTL_DUPLICATE_EXTENTS_TO_FILE in cluster-aligned calls of at most 1 GB, and D's creation and last-write time.
3. T is compared with D **byte for byte** (not by hash).
4. D is re-checked (same file at that path, unchanged).
5. `ReplaceFileW(D, T, backup)`. Then D's ACL, attributes, creation and last-write time and named (alternate) data
   streams are compared with before; any difference restores the backup. The backup is deleted only if all match.

Every step is journaled (`<folder>\.fsdedup-journal.jsonl`, flushed) before it happens. At the start of the next real
run unfinished work is cleaned up: swap not confirmed -> the backup is moved back over D; swap confirmed -> the backup
is deleted; temp files are deleted. `--what-if` only mentions pending entries.

## Safety guarantees

- A failure at any step before the swap leaves D exactly as it was; a crash inside the swap is undone from the journal.
- Content is compared byte for byte just before the swap, with D held open so nobody can write to it.
- Clone mode excludes files that are already hard-linked; hard-link mode permits those only to recognize same-file IDs as already shared. Reparse points, encrypted files and other volumes are never touched.
- ACL, attributes, creation time, last-write time and alternate data streams are verified after the swap; otherwise
  the old file is restored.
- Only the folder given is written: cache, journal and `.fsdedup-*` temp names.

## Hard-link mode

A hard link is another name for the same file rather than a copy-on-write clone. All names share the file's content,
ACL, attributes, timestamps and alternate data streams. Before linking, FsDedup requires D and C to have identical
ACLs (owner, group, ACEs and protection) and matching read-only, hidden and system attributes; mismatches are skipped
with a reported reason. After linking, writes through **any name change the file seen by every other name**. This
shared-write behavior is the main reason to prefer clone mode where it is available.

Microsoft documents 1,023 additional links on NTFS (1,024 names total). ReFS supports hard links, but Microsoft
publishes no ReFS-specific maximum. FsDedup conservatively splits at 1,024 total names and also responds to the
filesystem's too-many-links error by selecting the next duplicate as a new original. Hard-link mode counts the full
logical size of each replaced file as freed; actual free-space readings may lag on ReFS.

## Limits and observed behaviour


- **Clone mode requires ReFS** for real runs; hard-link mode supports NTFS and ReFS. Other filesystems exit 2 for real runs.
- **The final partial cluster is not shared**: ReFS copies it. "Bytes freed" counts whole clusters only; files smaller
  than a cluster are skipped.
- **Free space lags** 10-15 s after replacement (one jump); `--settle` waits for it.
- **Freshly written files** report unallocated extents until ReFS flushes them; they are never judged "already shared".
- **ACL**: `ReplaceFileW` can add the "auto-inherited" (AI) control flag. Owner, group, every ACE and the protection
  flag are compared; the AI flag is not (it grants nothing).
- D's integrity-stream/sparse settings become C's (cloning needs them equal). Extended attributes, object IDs and
  last-access time are not carried over. Read-only duplicates are skipped.
- A file rewritten with the same size and last-write time is not noticed by the cache; the byte-for-byte check before
  the swap still catches it.
- The 8175 limit is Microsoft's figure; it was not exhausted in testing.

## Tests

```
pwsh FsDedup/scripts/New-RefsTestVolume.ps1     # admin; 4 GB dynamic VHDX; prints the root, e.g. R:\
$env:FSDEDUP_REFS_TEST_ROOT = 'R:\'
dotnet test FsDedup/FsDedup.sln
pwsh FsDedup/scripts/Remove-RefsTestVolume.ps1
```

Tests needing ReFS are skipped, with a message, when `FSDEDUP_REFS_TEST_ROOT` is unset; the others run on any Windows
folder. `New-RefsTestVolume.ps1 -GitHubEnv` exports the variable to later workflow steps.
