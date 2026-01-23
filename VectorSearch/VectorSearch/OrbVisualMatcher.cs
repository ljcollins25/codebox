using OpenCvSharp;

namespace VectorSearch;

/// <summary>
/// ORB-based visual verification for ranking candidate images by visual similarity to a query.
/// Uses classical computer vision (ORB features + RANSAC homography) rather than ML/embeddings.
/// Designed as a second-stage verification after initial embedding-based filtering.
/// </summary>
public sealed class OrbVisualMatcher : IDisposable
{
    // ORB detector configuration
    private readonly ORB _orb;

    // Matcher for binary descriptors (ORB uses binary descriptors, so Hamming distance is appropriate)
    private readonly BFMatcher _matcher;

    // Lowe's ratio test threshold (typical values: 0.7-0.8)
    private const float RatioThreshold = 0.75f;

    // Minimum inliers required for a valid homography
    private const int MinInliersForValidMatch = 8;

    // RANSAC parameters for homography estimation
    private const double RansacReprojThreshold = 5.0;

    /// <summary>
    /// Result of matching a candidate image against a query.
    /// </summary>
    public readonly record struct MatchResult(
        string CandidatePath,
        int InlierCount,
        int TotalGoodMatches,
        bool WasFlippedMatch);

    /// <summary>
    /// Creates an ORB visual matcher with configurable parameters.
    /// </summary>
    /// <param name="nFeatures">Maximum number of features to detect (default: 2000)</param>
    /// <param name="scaleFactor">Pyramid decimation ratio (default: 1.2)</param>
    /// <param name="nLevels">Number of pyramid levels (default: 8)</param>
    /// <param name="edgeThreshold">Border size where features are not detected (default: 31)</param>
    /// <param name="patchSize">Size of the patch used by the oriented BRIEF descriptor (default: 31)</param>
    public OrbVisualMatcher(
        int nFeatures = 2000,
        float scaleFactor = 1.2f,
        int nLevels = 8,
        int edgeThreshold = 31,
        int patchSize = 31)
    {
        // Create ORB detector with specified parameters
        _orb = ORB.Create(
            nFeatures: nFeatures,
            scaleFactor: scaleFactor,
            nLevels: nLevels,
            edgeThreshold: edgeThreshold,
            firstLevel: 0,
            wtaK: 2,  // WTA_K=2 produces binary descriptors suitable for Hamming distance
            scoreType: ORBScoreType.Harris,
            patchSize: patchSize,
            fastThreshold: 20);

        // Brute-force matcher with Hamming distance (appropriate for binary ORB descriptors)
        // crossCheck=false because we'll use knnMatch for ratio test
        _matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
    }

    /// <summary>
    /// Ranks candidate images by visual similarity to the query image.
    /// Handles horizontal mirroring by also matching against a flipped query.
    /// </summary>
    /// <param name="queryImagePath">Path to the query image</param>
    /// <param name="candidateImagePaths">Paths to candidate images to rank</param>
    /// <returns>Ranked list of match results, sorted by inlier count descending</returns>
    public List<MatchResult> RankCandidates(string queryImagePath, IEnumerable<string> candidateImagePaths)
    {
        // Load query image and convert to grayscale (ORB works on grayscale)
        using var queryColor = Cv2.ImRead(queryImagePath, ImreadModes.Color);
        if (queryColor.Empty())
            throw new FileNotFoundException($"Could not load query image: {queryImagePath}");

        using var queryGray = new Mat();
        Cv2.CvtColor(queryColor, queryGray, ColorConversionCodes.BGR2GRAY);

        // Extract ORB features from original query
        using var queryKeypoints = new Mat();
        using var queryDescriptors = new Mat();
        ExtractOrbFeatures(queryGray, out var queryKp, queryDescriptors);

        // Create horizontally flipped version of query and extract features
        using var queryFlipped = new Mat();
        Cv2.Flip(queryGray, queryFlipped, FlipMode.Y); // Y-axis flip = horizontal mirror

        using var flippedKeypoints = new Mat();
        using var flippedDescriptors = new Mat();
        ExtractOrbFeatures(queryFlipped, out var flippedKp, flippedDescriptors);

        var results = new List<MatchResult>();

        foreach (var candidatePath in candidateImagePaths)
        {
            try
            {
                var result = MatchCandidate(
                    candidatePath,
                    queryDescriptors, queryKp,
                    flippedDescriptors, flippedKp);
                results.Add(result);
            }
            catch (Exception ex)
            {
                // Log error but continue with other candidates
                Console.WriteLine($"Warning: Failed to process {candidatePath}: {ex.Message}");
                results.Add(new MatchResult(candidatePath, 0, 0, false));
            }
        }

        // Sort by inlier count descending (best matches first)
        results.Sort((a, b) => b.InlierCount.CompareTo(a.InlierCount));

        return results;
    }

    /// <summary>
    /// Gets the best matching candidate from a set of candidates.
    /// </summary>
    /// <returns>The best match, or null if no valid matches found</returns>
    public MatchResult? FindBestMatch(string queryImagePath, IEnumerable<string> candidateImagePaths)
    {
        var ranked = RankCandidates(queryImagePath, candidateImagePaths);
        if (ranked.Count == 0 || ranked[0].InlierCount < MinInliersForValidMatch)
            return null;

        return ranked[0];
    }

    /// <summary>
    /// Matches a single candidate image against both original and flipped query.
    /// Returns the better of the two matches.
    /// </summary>
    private MatchResult MatchCandidate(
        string candidatePath,
        Mat queryDescriptors, KeyPoint[] queryKeypoints,
        Mat flippedDescriptors, KeyPoint[] flippedKeypoints)
    {
        // Load candidate image
        using var candidateColor = Cv2.ImRead(candidatePath, ImreadModes.Color);
        if (candidateColor.Empty())
            throw new FileNotFoundException($"Could not load candidate image: {candidatePath}");

        using var candidateGray = new Mat();
        Cv2.CvtColor(candidateColor, candidateGray, ColorConversionCodes.BGR2GRAY);

        // Extract ORB features from candidate
        using var candidateDescriptors = new Mat();
        ExtractOrbFeatures(candidateGray, out var candidateKp, candidateDescriptors);

        if (candidateDescriptors.Empty() || candidateDescriptors.Rows < MinInliersForValidMatch)
        {
            return new MatchResult(candidatePath, 0, 0, false);
        }

        // Match against original query
        var (originalInliers, originalGoodMatches) = ComputeInlierCount(
            queryDescriptors, queryKeypoints,
            candidateDescriptors, candidateKp);

        // Match against flipped query
        var (flippedInliers, flippedGoodMatches) = ComputeInlierCount(
            flippedDescriptors, flippedKeypoints,
            candidateDescriptors, candidateKp);

        // Return the better match
        if (flippedInliers > originalInliers)
        {
            return new MatchResult(candidatePath, flippedInliers, flippedGoodMatches, WasFlippedMatch: true);
        }
        else
        {
            return new MatchResult(candidatePath, originalInliers, originalGoodMatches, WasFlippedMatch: false);
        }
    }

    /// <summary>
    /// Extracts ORB keypoints and descriptors from a grayscale image.
    /// </summary>
    private void ExtractOrbFeatures(Mat grayImage, out KeyPoint[] keypoints, Mat descriptors)
    {
        _orb.DetectAndCompute(grayImage, null, out keypoints, descriptors);
    }

    /// <summary>
    /// Computes the number of inlier matches between query and candidate using RANSAC homography.
    /// </summary>
    private (int InlierCount, int GoodMatches) ComputeInlierCount(
        Mat queryDescriptors, KeyPoint[] queryKeypoints,
        Mat candidateDescriptors, KeyPoint[] candidateKeypoints)
    {
        if (queryDescriptors.Empty() || candidateDescriptors.Empty())
            return (0, 0);

        // Step 1: Find k=2 nearest neighbors for ratio test
        var knnMatches = _matcher.KnnMatch(queryDescriptors, candidateDescriptors, k: 2);

        // Step 2: Apply Lowe's ratio test to filter ambiguous matches
        var goodMatches = new List<DMatch>();
        foreach (var match in knnMatches)
        {
            // Ratio test: keep match only if best match is significantly better than second best
            if (match.Length >= 2 && match[0].Distance < RatioThreshold * match[1].Distance)
            {
                goodMatches.Add(match[0]);
            }
        }

        // Need minimum matches to estimate homography (4 point correspondences minimum)
        if (goodMatches.Count < 4)
            return (0, goodMatches.Count);

        // Step 3: Extract matched point coordinates
        var queryPoints = new List<Point2f>();
        var candidatePoints = new List<Point2f>();

        foreach (var match in goodMatches)
        {
            queryPoints.Add(queryKeypoints[match.QueryIdx].Pt);
            candidatePoints.Add(candidateKeypoints[match.TrainIdx].Pt);
        }

        // Step 4: Estimate homography using RANSAC
        // This finds a perspective transform that maps query points to candidate points
        // RANSAC robustly handles outliers (mismatches due to crops/overlays)
        using var mask = new Mat();
        using var homography = Cv2.FindHomography(
            InputArray.Create(queryPoints),
            InputArray.Create(candidatePoints),
            HomographyMethods.Ransac,
            RansacReprojThreshold,
            mask);

        // Step 5: Count inliers (points that fit the estimated homography)
        if (homography.Empty() || mask.Empty())
            return (0, goodMatches.Count);

        int inlierCount = 0;
        var maskData = new byte[mask.Rows];
        mask.GetArray(out maskData);
        foreach (var m in maskData)
        {
            if (m != 0) inlierCount++;
        }

        return (inlierCount, goodMatches.Count);
    }

    public void Dispose()
    {
        _orb.Dispose();
        _matcher.Dispose();
    }
}
