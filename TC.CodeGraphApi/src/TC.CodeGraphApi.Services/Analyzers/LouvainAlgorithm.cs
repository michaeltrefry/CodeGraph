namespace TC.CodeGraphApi.Services.Analyzers;

/// <summary>
/// Louvain community detection algorithm for weighted undirected graphs.
/// Blondel et al., 2008 — "Fast unfolding of communities in large networks"
/// </summary>
public static class LouvainAlgorithm
{
    public record LouvainResult(
        Dictionary<string, int> Communities,
        double Modularity,
        int CommunityCount);

    /// <summary>
    /// Runs Louvain community detection on a weighted adjacency list.
    /// Nodes with no edges are assigned to singleton communities.
    /// </summary>
    public static LouvainResult Execute(Dictionary<string, Dictionary<string, double>> adjacency)
    {
        if (adjacency.Count == 0)
            return new LouvainResult(new Dictionary<string, int>(), 0, 0);

        var nodes = adjacency.Keys.ToList();
        var nodeIndex = new Dictionary<string, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
            nodeIndex[nodes[i]] = i;

        int n = nodes.Count;

        // Build symmetric weighted adjacency matrix (as sparse dict)
        // and compute total weight (m) and node strengths (k_i)
        var weights = new Dictionary<int, Dictionary<int, double>>();
        var strength = new double[n]; // k_i = sum of edge weights for node i
        double totalWeight = 0; // 2m = sum of all edge weights (each edge counted once)

        for (int i = 0; i < n; i++)
            weights[i] = new Dictionary<int, double>();

        foreach (var (source, neighbors) in adjacency)
        {
            int i = nodeIndex[source];
            foreach (var (target, weight) in neighbors)
            {
                if (!nodeIndex.TryGetValue(target, out int j)) continue;
                if (i == j) continue; // skip self-loops

                // Add both directions (undirected graph)
                if (!weights[i].ContainsKey(j))
                {
                    weights[i][j] = weight;
                    weights[j][i] = weight;
                    strength[i] += weight;
                    strength[j] += weight;
                    totalWeight += weight;
                }
            }
        }

        if (totalWeight == 0)
        {
            // No edges — each node is its own community
            var singletons = new Dictionary<string, int>(n);
            for (int i = 0; i < n; i++)
                singletons[nodes[i]] = i;
            return new LouvainResult(singletons, 0, n);
        }

        // Initialize: each node in its own community
        var community = new int[n];
        for (int i = 0; i < n; i++)
            community[i] = i;

        // Sum of weights inside each community (Σ_in)
        var sigmaIn = new double[n];
        // Sum of all weights incident to nodes in each community (Σ_tot)
        var sigmaTot = new double[n];
        for (int i = 0; i < n; i++)
            sigmaTot[i] = strength[i];

        bool improved = true;
        int iteration = 0;
        const int maxIterations = 100;

        while (improved && iteration < maxIterations)
        {
            improved = false;
            iteration++;

            for (int i = 0; i < n; i++)
            {
                int currentComm = community[i];

                // Calculate weights from node i to each neighboring community
                var commWeights = new Dictionary<int, double>();
                foreach (var (j, w) in weights[i])
                {
                    int c = community[j];
                    if (!commWeights.ContainsKey(c))
                        commWeights[c] = 0;
                    commWeights[c] += w;
                }

                // Weight to own community
                double kiIn = commWeights.GetValueOrDefault(currentComm, 0);
                double ki = strength[i];

                // Remove node i from its community
                sigmaIn[currentComm] -= 2 * kiIn; // edges within community involving i
                sigmaTot[currentComm] -= ki;

                // Find best community to move to
                int bestComm = currentComm;
                double bestGain = 0;

                foreach (var (c, kiC) in commWeights)
                {
                    // Modularity gain of moving i to community c:
                    // ΔQ = [Σ_in(c) + 2*k_i,in(c)] / 2m - [(Σ_tot(c) + k_i) / 2m]²
                    //     - [Σ_in(c) / 2m - (Σ_tot(c) / 2m)² - (k_i / 2m)²]
                    // Simplified: ΔQ = k_i,in(c) / m - Σ_tot(c) * k_i / (2m²)
                    double gain = kiC / totalWeight - sigmaTot[c] * ki / (2 * totalWeight * totalWeight);

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestComm = c;
                    }
                }

                // Move node i to best community
                community[i] = bestComm;
                sigmaIn[bestComm] += 2 * commWeights.GetValueOrDefault(bestComm, 0);
                sigmaTot[bestComm] += ki;

                if (bestComm != currentComm)
                    improved = true;
            }
        }

        // Renumber communities to be contiguous starting from 0
        var communityMap = new Dictionary<int, int>();
        int nextId = 0;
        var result = new Dictionary<string, int>(n);
        for (int i = 0; i < n; i++)
        {
            int c = community[i];
            if (!communityMap.TryGetValue(c, out int mapped))
            {
                mapped = nextId++;
                communityMap[c] = mapped;
            }
            result[nodes[i]] = mapped;
        }

        double modularity = ComputeModularity(weights, strength, community, totalWeight, n);

        return new LouvainResult(result, modularity, nextId);
    }

    private static double ComputeModularity(
        Dictionary<int, Dictionary<int, double>> weights,
        double[] strength,
        int[] community,
        double totalWeight,
        int n)
    {
        double q = 0;
        for (int i = 0; i < n; i++)
        {
            foreach (var (j, w) in weights[i])
            {
                if (community[i] != community[j]) continue;
                q += w - strength[i] * strength[j] / (2 * totalWeight);
            }
        }
        return q / (2 * totalWeight);
    }

    /// <summary>
    /// Computes approximate betweenness centrality using BFS from every node.
    /// Returns normalized values (0–1). Suitable for small graphs (< 1000 nodes).
    /// </summary>
    public static Dictionary<string, double> ComputeBetweennessCentrality(
        Dictionary<string, Dictionary<string, double>> adjacency)
    {
        var nodes = adjacency.Keys.ToList();
        int n = nodes.Count;
        if (n <= 2) return nodes.ToDictionary(nd => nd, _ => 0.0);

        var centrality = new Dictionary<string, double>(n);
        foreach (var node in nodes)
            centrality[node] = 0;

        // Brandes' algorithm for unweighted betweenness centrality
        foreach (var source in nodes)
        {
            var stack = new Stack<string>();
            var predecessors = new Dictionary<string, List<string>>(n);
            var sigma = new Dictionary<string, double>(n); // # of shortest paths
            var dist = new Dictionary<string, int>(n);
            var delta = new Dictionary<string, double>(n);

            foreach (var v in nodes)
            {
                predecessors[v] = new List<string>();
                sigma[v] = 0;
                dist[v] = -1;
                delta[v] = 0;
            }

            sigma[source] = 1;
            dist[source] = 0;

            var queue = new Queue<string>();
            queue.Enqueue(source);

            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                stack.Push(v);

                if (!adjacency.TryGetValue(v, out var neighbors)) continue;

                foreach (var w in neighbors.Keys)
                {
                    // Skip neighbors not in the node set (defensive)
                    if (!dist.ContainsKey(w)) continue;

                    // First visit?
                    if (dist[w] < 0)
                    {
                        dist[w] = dist[v] + 1;
                        queue.Enqueue(w);
                    }

                    // Shortest path via v?
                    if (dist[w] == dist[v] + 1)
                    {
                        sigma[w] += sigma[v];
                        predecessors[w].Add(v);
                    }
                }
            }

            // Back-propagation
            while (stack.Count > 0)
            {
                var w = stack.Pop();
                foreach (var v in predecessors[w])
                {
                    delta[v] += sigma[v] / sigma[w] * (1 + delta[w]);
                }

                if (w != source)
                    centrality[w] += delta[w];
            }
        }

        // Normalize: divide by (n-1)(n-2) for undirected graphs
        double normFactor = (n - 1) * (n - 2);
        if (normFactor > 0)
        {
            foreach (var node in nodes)
                centrality[node] /= normFactor;
        }

        return centrality;
    }
}
