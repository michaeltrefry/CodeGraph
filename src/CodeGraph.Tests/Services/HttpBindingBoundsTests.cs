using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit.Abstractions;

namespace CodeGraph.Tests.Services;

public sealed class HttpBindingBoundsTests(ITestOutputHelper output)
{
    private static LinkNodeRow Route(string path, string repository="Api", string? suffix=null, Dictionary<string,string?>? extra=null) =>
        new(1,repository,NodeLabel.Route,path,$"route:{repository}:GET:{path}{suffix}",new Dictionary<string,string?>(extra ?? []) { ["http_method"]="GET",["route_template"]=path });
    private static List<LinkNodeRow> Match(HttpRouteIndex index,string path,HashSet<string> names,string? service=null,string? origin=null,string? dto=null) =>
        CrossRepoLinker.SelectHttpRouteMatches("GET",path,service,dto,null,origin,names,index);
    private static HttpBindingBudget Work(long limit=100_000_000,int depth=128,CancellationToken ct=default) =>
        new(new IndexingOptions {MaxHttpBindingWork=limit,MaxHttpBindingDepth=depth},ct);

    [Fact]
    public void TenThousandExactMissesVisitSelectiveBranchesInsteadOfEverySameShapeRoute()
    {
        var work=Work();
        var index=CrossRepoLinker.BuildHttpRouteLookup(Enumerable.Range(0,10_000).Select(i=>Route($"/api/item-{i}/details/{{id}}")),work);
        var built=work.WorkUsed;
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"Api"};
        for(var i=0;i<10_000;i++)Match(index,$"/api/absent-{i}/details/value",names).ShouldBeEmpty();
        var queried=work.WorkUsed-built;
        output.WriteLine($"10k route build: {built:N0}; 10k selective misses: {queried:N0}");
        queried.ShouldBeLessThan(30_000_000);
        work.WorkUsed.ShouldBeLessThan(100_000_000);
    }

    [Fact]
    public void SamePatternCandidatesUseRepositoryAndOriginPartitionsAndStopAtTwoAmbiguityWitnesses()
    {
        var work=Work();
        var routes=Enumerable.Range(0,10_000).Select(i=>Route("/api/{id}","Repo"+i,extra:new(){["origin"]=$"https://host-{i}.example"})).ToArray();
        var index=CrossRepoLinker.BuildHttpRouteLookup(routes,work);
        var names=routes.Select(row=>row.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before=work.WorkUsed;
        for(var i=0;i<1_000;i++)Match(index,"/api/value",names,"Repo"+i,$"https://host-{i}.example").ShouldHaveSingleItem().Repository.ShouldBe("Repo"+i);
        (work.WorkUsed-before).ShouldBeLessThan(5_000_000);
        before=work.WorkUsed;
        Match(index,"/api/value",names).ShouldBeEmpty();
        (work.WorkUsed-before).ShouldBeLessThan(5_000);
        output.WriteLine($"10k shared-pattern build + 1k scoped matches: {work.WorkUsed:N0}");
    }

    [Fact]
    public void PriorityFilteringAndCatchAllTraversalRetainExistingSemantics()
    {
        var work=Work();
        var literal=Route("/api/items/123","Local");
        var parameter=Route("/api/items/{id}","Api");
        var catchall=Route("/api/items/{*tail}","Api");
        var index=CrossRepoLinker.BuildHttpRouteLookup([literal,parameter,catchall],work);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"Api","Local"};
        Match(index,"/api/items/123",names).ShouldBe([literal]);
        Match(index,"/api/items/123",names,"Api").ShouldBe([parameter]);
        Match(index,"/api/items/a/b",names).ShouldBe([catchall]);
        Match(index,"/api/items/slug",names).ShouldBeEmpty();
        Match(index,"/api/items/123",names,origin:"https://external.example").ShouldBeEmpty();
    }

    [Fact]
    public void LocalAndGlobalIndexBuildsAndCacheHitsConsumeOneCancellableBudget()
    {
        var rows=Enumerable.Range(0,50).Select(i=>Route("/api/item-"+i)).ToArray();
        var probe=Work();CrossRepoLinker.BuildHttpRouteLookup(rows,probe);
        var shared=Work(probe.WorkUsed+1);
        CrossRepoLinker.BuildHttpRouteLookup(rows,shared).Budget.ShouldBeSameAs(shared);
        Should.Throw<InvalidOperationException>(()=>CrossRepoLinker.BuildHttpRouteLookup(rows,shared)).Message.ShouldContain("HTTP binding");
        using var cancellation=new CancellationTokenSource();
        var cancellable=Work(ct:cancellation.Token);var index=CrossRepoLinker.BuildHttpRouteLookup(rows,cancellable);
        var names=new HashSet<string>{"Api"};Match(index,"/api/item-0",names).ShouldHaveSingleItem();
        cancellation.Cancel();Should.Throw<OperationCanceledException>(()=>Match(index,"/api/item-0",names));
    }

    [Fact]
    public void LongPathsDtoPrefixesAndNestingAreChargedBeforeExpansion()
    {
        Should.Throw<InvalidOperationException>(()=>CrossRepoLinker.BuildHttpRouteLookup([Route("/"+new string('x',10_000))],Work(2_000)));
        Should.Throw<InvalidOperationException>(()=>CrossRepoLinker.BuildHttpRouteLookup([Route("/a/b/c/d")],Work(depth:3))).Message.ShouldContain("depth");
        var names=new HashSet<string>{"Api"};
        var index=CrossRepoLinker.BuildHttpRouteLookup([Route("/api")],Work(100_000,8));
        Should.Throw<InvalidOperationException>(()=>Match(index,"/api",names,dto:string.Join('.',Enumerable.Repeat("LongNamespace",40)))).Message.ShouldContain("depth");
        var prefixWork=Work(100_000,128);var prefixIndex=CrossRepoLinker.BuildHttpRouteLookup([Route("/api")],prefixWork);
        var dto=string.Join('.',Enumerable.Repeat(new string('a',100),20));
        Should.Throw<InvalidOperationException>(()=>Match(prefixIndex,"/api",names,dto:dto)).Message.ShouldContain("budget");
    }

    [Fact]
    public void MalformedBraceSegmentsAreScannedLinearlyWithinTheSameTextBudget()
    {
        var work=Work(2_000_000);
        var index=CrossRepoLinker.BuildHttpRouteLookup([Route("/api/items/{id}")],work);
        Match(index,"/api/"+new string('{',20_000),new HashSet<string>{"Api"}).ShouldBeEmpty();
        work.WorkUsed.ShouldBeLessThan(2_000_000);
        CrossRepoLinker.NormalizeRouteTemplate("GET","/api/{name}/{*tail}").ShouldBe("GET:api/{*}/{**}");
    }

    private static GraphNode Endpoint(string repo,string path="/shared") => new(){Repository=repo,Label=NodeLabel.Route,Name=path,
        QualifiedName=$"route:{repo}:GET:{path}",FilePath="api.go",Properties=new(){["http_method"]="GET",["route_template"]=path}};
    private static GraphBuffer Buffer(string repo="App", bool previous=false)
    {
        var buffer=new GraphBuffer();buffer.AddNode(new(){Repository=repo,Label=NodeLabel.Function,Name="caller",QualifiedName=repo+":caller",FilePath="main.go"});
        buffer.AddNode(Endpoint(repo));
        buffer.AddNode(new(){Repository=repo,Label=NodeLabel.Route,Name="raw",QualifiedName="route:*:GET:/shared",Properties=new(){["stub"]=true}});
        buffer.AddEdge(new(repo+":caller","route:*:GET:/shared",EdgeType.HTTP_CALLS,new(){["http_method"]="GET",["url_pattern"]="/shared",["extraction_owner"]="typescript"}));
        if(previous)buffer.AddEdge(new(repo+":caller",$"route:{repo}:GET:/shared",EdgeType.HTTP_CALLS,new(){["semantic_owner"]="http_binding"}));
        buffer.AddFileHash("main.go","unchanged");return buffer;
    }
    private static string[] BufferFacts(GraphBuffer buffer)=>buffer.AllPendingEdges.Select(edge=>JsonSerializer.Serialize(edge)).Order(StringComparer.Ordinal).ToArray();
    private static IndexingPipeline Pipeline(InMemoryGraphStore store,IndexingOptions options,params ICodeExtractor[] extractors)=>
        new(store,extractors,Options.Create(options),new LocalFileSystem(),NullLogger<IndexingPipeline>.Instance);

    [Fact]
    public async Task LateLocalGlobalRefusalPreservesBufferedDerivedEdgesAndCertifiedHashes()
    {
        var store=new InMemoryGraphStore();store.AddRepository("App");store.AddRepository("Other");
        store.AddNode(Endpoint("Other","/unrelated"));
        var probe=Work();await IndexingPipeline.RebindHttpCallsAsync(store,NullLogger.Instance,"App",Buffer(),false,new HashSet<string>(),default,work:probe);
        var buffer=Buffer(previous:true);var before=BufferFacts(buffer);var hashes=buffer.AllFileHashes.ToArray();
        var constrained=Work(probe.WorkUsed-1);
        await Should.ThrowAsync<InvalidOperationException>(()=>IndexingPipeline.RebindHttpCallsAsync(store,NullLogger.Instance,"App",buffer,false,new HashSet<string>(),default,work:constrained));
        BufferFacts(buffer).ShouldBe(before);buffer.AllFileHashes.ShouldBe(hashes);store.ReplaceRepositoryFilesCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexRefusalPreservesWholePriorGraphAndHashesForFullAndIncrementalRuns(bool incremental)
    {
        var root=Path.Combine(Path.GetTempPath(),"cg-http-budget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"main.go"),"package main\n");
            var store=new InMemoryGraphStore();store.AddRepository("App");
            var seed=Buffer(previous:true);
            await store.ReplaceRepositoryGraphAsync("App",seed.AllNodes.ToList(),seed.AllPendingEdges.ToList(),new Dictionary<string,string>{{"main.go","certified"}},new(){Name="App"},null);
            var nodes=store.Nodes.ToArray();var edges=store.Edges.ToArray();var hashes=store.FileHashesOf("App").ToArray();
            var pipeline=Pipeline(store,new(){MaxHttpBindingWork=1},new FixtureExtractor());
            await Should.ThrowAsync<InvalidOperationException>(()=>pipeline.IndexRepositoryAsync("App",root,changedFilesOnly:incremental?["main.go"]:null,replaceExistingGraph:!incremental));
            store.Nodes.ShouldBe(nodes);store.Edges.ShouldBe(edges);store.FileHashesOf("App").ShouldBe(hashes);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task LongMatchedTemplateOutputIsChargedForEveryDerivedBindingBeforeAnyBufferChange()
    {
        var store=new InMemoryGraphStore();store.AddRepository("App");
        var buffer=new GraphBuffer();
        var template="/api/{id:regex("+new string('x',4000)+")}";
        buffer.AddNode(Endpoint("App") with {Name="registered",QualifiedName="route:App:registered",Properties=new(){["http_method"]="GET",["route_template"]=template}});
        for(var i=0;i<30;i++)
        {
            buffer.AddNode(new(){Repository="App",Label=NodeLabel.Function,Name="caller",QualifiedName="App:caller"+i,FilePath="main.go"});
            buffer.AddEdge(new("App:caller"+i,"route:*:GET:/api/value"+i,EdgeType.HTTP_CALLS,new(){["http_method"]="GET",["url_pattern"]="/api/value"+i,["service_name"]="App"}));
        }
        var before=BufferFacts(buffer);
        await Should.ThrowAsync<InvalidOperationException>(()=>IndexingPipeline.RebindHttpCallsAsync(store,NullLogger.Instance,"App",buffer,false,new HashSet<string>(),default,work:Work(200_000)));
        BufferFacts(buffer).ShouldBe(before);
        store.ReplaceRepositoryFilesCallCount.ShouldBe(0);
    }

    [Fact]
    public void DefaultAllowanceCoversTwoTenThousandRouteIndexesAndTenThousandDistinctMisses()
    {
        var work=new HttpBindingBudget();
        var rows=Enumerable.Range(0,10_000).Select(i=>Route($"/api/item-{i}/details/{{id}}")).ToArray();
        CrossRepoLinker.BuildHttpRouteLookup(rows,work);
        var index=CrossRepoLinker.BuildHttpRouteLookup(rows,work);
        var names=new HashSet<string>{"Api"};
        for(var i=0;i<10_000;i++) Match(index,$"/api/missing-{i}/details/value",names).ShouldBeEmpty();
        output.WriteLine($"Default shared allowance, 2x10k route builds +10k misses: {work.WorkUsed:N0}/{new IndexingOptions().MaxHttpBindingWork:N0}");
        work.WorkUsed.ShouldBeLessThan(new IndexingOptions().MaxHttpBindingWork);
    }

    private sealed class FixtureExtractor:ICodeExtractor
    {
        public IReadOnlySet<string> SupportedExtensions{get;}=new HashSet<string>{".go"};
        public Task<ExtractionResult> ExtractAsync(string path,string content,ExtractorContext context,CancellationToken ct=default)
        {var buffer=Buffer();return Task.FromResult(new ExtractionResult{Nodes=buffer.AllNodes.ToList(),Edges=buffer.AllPendingEdges.ToList()});}
    }

    private class LateStore:InMemoryGraphStore
    {
        public bool Refuse;public CancellationTokenSource? Cancel;
        public override async Task<IReadOnlyList<GraphNode>> FindNodesByLabelAsync(string repo,NodeLabel label,int limit=10000)
        {
            var nodes=await base.FindNodesByLabelAsync(repo,label,limit);
            if(repo=="B"&&label==NodeLabel.Route){Cancel?.Cancel();if(Refuse)return nodes.Select(node=>node with{Properties=new(node.Properties){["route_template"]="/"+new string('x',30_000)}}).ToList();}
            return nodes;
        }
    }
    private static async Task Seed(LateStore store,string repo)
    {
        store.AddRepository(repo);var buffer=Buffer(repo,true);
        await store.ReplaceRepositoryFilesAsync(repo,["main.go","api.go"],buffer.AllNodes.ToList(),buffer.AllPendingEdges.ToList(),new Dictionary<string,string>{{"main.go","certified"}});
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterOwnerRefusalOrCancellationCannotPartiallyApplyAnyOwnedDelta(bool cancellation)
    {
        var store=new LateStore();await Seed(store,"A");await Seed(store,"B");store.AddRepository("C");var target=store.AddNode(Endpoint("C"));
        var a=store.Nodes.Single(node=>node.QualifiedName=="A:caller");
        await store.InsertCrossRepoEdgeAsync(new(){SourceRepository="A",TargetRepository="C",SourceNodeId=a.Id,TargetNodeId=target,Type=EdgeType.HTTP_CALLS,Properties=new(){["http_method"]="GET",["url_pattern"]="/shared"}});
        await store.InsertCrossRepoEdgeAsync(new(){SourceRepository="A",TargetRepository="C",SourceNodeId=a.Id,TargetNodeId=target,Type=EdgeType.CALLS,Properties=new(){["legacy"]="retained on refusal"}});
        var edges=store.Edges.ToArray();var cross=store.CrossEdges.ToArray();var writes=store.ReplaceRepositoryFilesCallCount;
        using var cancelled=new CancellationTokenSource();store.Cancel=cancellation?cancelled:null;store.Refuse=!cancellation;
        var linker=new CrossRepoLinker(store,NullLogger<CrossRepoLinker>.Instance,Options.Create(new IndexingOptions{MaxHttpBindingWork=100_000}));
        if(cancellation)await Should.ThrowAsync<OperationCanceledException>(()=>linker.LinkForRepositoryAsync("C",cancelled.Token));
        else await Should.ThrowAsync<InvalidOperationException>(()=>linker.LinkForRepositoryAsync("C",default));
        store.Edges.ShouldBe(edges);store.CrossEdges.ShouldBe(cross);store.ReplaceRepositoryFilesCallCount.ShouldBe(writes);
        store.FileHashesOf("A")["main.go"].ShouldBe("certified");
    }

    private sealed class ApplyCancellationStore:LateStore,IGraphStore
    {
        public CancellationTokenSource? DuringFirstOwner;
        async Task<int> IGraphStore.ReplaceRepositoryFilesAsync(string repository,IReadOnlyList<string> paths,
            IReadOnlyList<GraphNode> nodes,IReadOnlyList<PendingEdge> edges,IReadOnlyDictionary<string,string> hashes,
            IReadOnlyDictionary<string,string>? versions,GraphSliceRetractions? retractions,CancellationToken ct)
        {
            var count=await base.ReplaceRepositoryFilesAsync(repository,paths,nodes,edges,hashes,versions,retractions,ct);
            if(repository=="A")DuringFirstOwner?.Cancel();
            return count;
        }
    }

    [Fact]
    public async Task CancellationDuringApplyCompletesCurrentOwnerAndRetainsUnstartedOwners()
    {
        var store=new ApplyCancellationStore();await Seed(store,"A");await Seed(store,"B");store.AddRepository("C");
        var endpoint=store.AddNode(Endpoint("C"));var source=store.Nodes.Single(node=>node.QualifiedName=="A:caller");
        await store.InsertCrossRepoEdgeAsync(new(){SourceRepository="A",TargetRepository="C",SourceNodeId=source.Id,TargetNodeId=endpoint,Type=EdgeType.HTTP_CALLS,Properties=new(){["http_method"]="GET",["url_pattern"]="/shared"}});
        using var cancellation=new CancellationTokenSource();store.DuringFirstOwner=cancellation;
        var linker=new CrossRepoLinker(store,NullLogger<CrossRepoLinker>.Instance);
        await Should.ThrowAsync<OperationCanceledException>(()=>linker.LinkForRepositoryAsync("C",cancellation.Token));
        store.Edges.ShouldNotContain(edge=>edge.Repository=="A"&&edge.Properties.ContainsKey("semantic_owner") && edge.Properties["semantic_owner"].ToString()=="http_binding");
        store.CrossEdges.ShouldBeEmpty(); // A's cross delta completed despite cancellation after its local write.
        store.Edges.ShouldContain(edge=>edge.Repository=="B"&&edge.Properties.ContainsKey("semantic_owner") && edge.Properties["semantic_owner"].ToString()=="http_binding");
        store.FileHashesOf("A")["main.go"].ShouldBe("certified");store.FileHashesOf("B")["main.go"].ShouldBe("certified");
    }

    [Fact]
    public void HttpLimitsParticipateInRoutineSyncFingerprint()
    {
        var store=new InMemoryGraphStore();
        Pipeline(store,new(){MaxHttpBindingWork=1}).ExtractionFingerprint.ShouldNotBe(Pipeline(store,new(){MaxHttpBindingWork=2}).ExtractionFingerprint);
        Pipeline(store,new(){MaxHttpBindingDepth=1}).ExtractionFingerprint.ShouldNotBe(Pipeline(store,new(){MaxHttpBindingDepth=2}).ExtractionFingerprint);
        ExtractionVersionCatalog.PipelineVersion.ShouldBeGreaterThanOrEqualTo(12);
    }
}
