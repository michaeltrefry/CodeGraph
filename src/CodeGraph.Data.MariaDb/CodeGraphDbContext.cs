using CodeGraph.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeGraph.Data.MariaDb;

public class CodeGraphDbContext(DbContextOptions<CodeGraphDbContext> options) : DbContext(options)
{
    public DbSet<RepositoryEntity> Repositories => Set<RepositoryEntity>();
    public DbSet<NodeEntity> Nodes => Set<NodeEntity>();
    public DbSet<EdgeEntity> Edges => Set<EdgeEntity>();
    public DbSet<CrossRepoEdgeEntity> CrossRepoEdges => Set<CrossRepoEdgeEntity>();
    public DbSet<FileHashEntity> FileHashes => Set<FileHashEntity>();
    public DbSet<RepositorySummaryEntity> RepositorySummaries => Set<RepositorySummaryEntity>();
    public DbSet<ProjectAnalysisEntity> ProjectAnalyses => Set<ProjectAnalysisEntity>();
    public DbSet<SyncStateEntity> SyncStates => Set<SyncStateEntity>();
    public DbSet<MigrationHistoryEntity> MigrationHistory => Set<MigrationHistoryEntity>();
    public DbSet<AnalysisBatchEntity> AnalysisBatches => Set<AnalysisBatchEntity>();
    public DbSet<AnalysisBatchRequestEntity> AnalysisBatchRequests => Set<AnalysisBatchRequestEntity>();
    public DbSet<LlmUsageEntity> LlmUsage => Set<LlmUsageEntity>();
    public DbSet<NodeAnalysisEntity> NodeAnalyses => Set<NodeAnalysisEntity>();
    public DbSet<FileMetricsEntity> FileMetrics => Set<FileMetricsEntity>();
    public DbSet<ProjectHealthSummaryEntity> ProjectHealthSummaries => Set<ProjectHealthSummaryEntity>();
    public DbSet<ProjectHealthAnalysisEntity> ProjectHealthAnalyses => Set<ProjectHealthAnalysisEntity>();
    public DbSet<SecurityFindingEntity> SecurityFindings => Set<SecurityFindingEntity>();
    public DbSet<ProjectDiagnosticEntity> ProjectDiagnostics => Set<ProjectDiagnosticEntity>();
    public DbSet<ProjectReviewRunEntity> ProjectReviewRuns => Set<ProjectReviewRunEntity>();
    public DbSet<ProjectReviewFindingEntity> ProjectReviewFindings => Set<ProjectReviewFindingEntity>();
    public DbSet<RepositoryReviewRunEntity> RepositoryReviewRuns => Set<RepositoryReviewRunEntity>();
    public DbSet<RepositoryReviewProjectSectionEntity> RepositoryReviewProjectSections => Set<RepositoryReviewProjectSectionEntity>();
    public DbSet<RepositoryReviewFindingEntity> RepositoryReviewFindings => Set<RepositoryReviewFindingEntity>();
    public DbSet<ProjectSecuritySummaryEntity> ProjectSecuritySummaries => Set<ProjectSecuritySummaryEntity>();
    public DbSet<ConventionPageEntity> ConventionPages => Set<ConventionPageEntity>();
    public DbSet<ConventionRevisionEntity> ConventionRevisions => Set<ConventionRevisionEntity>();
    public DbSet<WikiSectionEntity> WikiSections => Set<WikiSectionEntity>();
    public DbSet<WikiPageEntity> WikiPages => Set<WikiPageEntity>();
    public DbSet<WikiRevisionEntity> WikiRevisions => Set<WikiRevisionEntity>();
    public DbSet<WikiAttachmentEntity> WikiAttachments => Set<WikiAttachmentEntity>();
    public DbSet<ExclusionRuleEntity> ExclusionRules => Set<ExclusionRuleEntity>();
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();
    public DbSet<SettingsOverrideEntity> SettingsOverrides => Set<SettingsOverrideEntity>();
    public DbSet<AgentPromptOverrideEntity> AgentPromptOverrides => Set<AgentPromptOverrideEntity>();
    public DbSet<DatabaseSourceEntity> DatabaseSources => Set<DatabaseSourceEntity>();
    public DbSet<LlmConfigEntryEntity> LlmConfig => Set<LlmConfigEntryEntity>();
    public DbSet<LlmProviderModelEntity> LlmProviderModels => Set<LlmProviderModelEntity>();
    public DbSet<McpPersonalAccessTokenEntity> McpPersonalAccessTokens => Set<McpPersonalAccessTokenEntity>();
    public DbSet<McpToolInvocationEntity> McpToolInvocations => Set<McpToolInvocationEntity>();
    public DbSet<JobScheduleEntity> JobSchedules => Set<JobScheduleEntity>();
    public DbSet<IndexerRunEntity> IndexerRuns => Set<IndexerRunEntity>();
    public DbSet<AssistantRunEntity> AssistantRuns => Set<AssistantRunEntity>();
    public DbSet<AssistantChatMessageEntity> AssistantChatMessages => Set<AssistantChatMessageEntity>();
    public DbSet<AssistantRunEventEntity> AssistantRunEvents => Set<AssistantRunEventEntity>();
    public DbSet<AssistantDebugExchangeEntity> AssistantDebugExchanges => Set<AssistantDebugExchangeEntity>();
    public DbSet<AssistantDebugTraceAuditEntity> AssistantDebugTraceAudits => Set<AssistantDebugTraceAuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureRepositories(modelBuilder);
        ConfigureGraph(modelBuilder);
        ConfigureAnalysis(modelBuilder);
        ConfigureHealthAndSecurity(modelBuilder);
        ConfigureReviews(modelBuilder);
        ConfigureWiki(modelBuilder);
        ConfigureExclusions(modelBuilder);
        ConfigureAdminAndDatabaseSources(modelBuilder);
        ConfigureMcpAndTelemetry(modelBuilder);
        ConfigureJobSchedules(modelBuilder);
        ConfigureIndexerRuns(modelBuilder);
        ConfigureAssistantRuns(modelBuilder);
    }

    private static void ConfigureRepositories(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RepositoryEntity>(e =>
        {
            e.ToTable("repositories");
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.RepoUrl).HasColumnName("repo_url");
            e.Property(p => p.SourceGroup).HasColumnName("gitlab_group");
            e.Property(p => p.LocalPath).HasColumnName("local_path");
            e.Property(p => p.DefaultBranch).HasColumnName("default_branch");
            e.Property(p => p.LastCommitSha).HasColumnName("last_commit_sha");
            e.Property(p => p.IndexedAt).HasColumnName("indexed_at");
            e.Property(p => p.Language).HasColumnName("language");
            e.Property(p => p.Framework).HasColumnName("framework");
            e.Property(p => p.IsFoundational).HasColumnName("is_foundational");
            e.Property(p => p.Properties).HasColumnName("properties").HasColumnType("json");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(p => p.SourceGroup);
        });

        modelBuilder.Entity<SyncStateEntity>(e =>
        {
            e.ToTable("sync_state");
            e.HasKey(s => s.Project);
            e.Property(s => s.Project).HasColumnName("project");
            e.Property(s => s.LastSyncAt).HasColumnName("last_sync_at");
            e.Property(s => s.LastCommitSha).HasColumnName("last_commit_sha");
            e.Property(s => s.Status).HasColumnName("status");
            e.Property(s => s.ErrorMessage).HasColumnName("error_message");
        });
    }

    private static void ConfigureGraph(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NodeEntity>(e =>
        {
            e.ToTable("nodes");
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasColumnName("id");
            e.Property(n => n.Project).HasColumnName("project");
            e.Property(n => n.DotnetProject).HasColumnName("dotnet_project");
            e.Property(n => n.Label).HasColumnName("label");
            e.Property(n => n.Name).HasColumnName("name");
            e.Property(n => n.QualifiedName).HasColumnName("qualified_name");
            e.Property(n => n.FilePath).HasColumnName("file_path");
            e.Property(n => n.StartLine).HasColumnName("start_line");
            e.Property(n => n.EndLine).HasColumnName("end_line");
            e.Property(n => n.Properties).HasColumnName("properties").HasColumnType("json");
            e.Property(n => n.DoNotTrust).HasColumnName("do_not_trust");
            e.HasIndex(n => new { n.Project, n.QualifiedName }).IsUnique();
            e.HasIndex(n => new { n.Project, n.Label });
            e.HasIndex(n => new { n.Project, n.Name });
            e.HasIndex(n => new { n.Project, n.FilePath });
        });

        modelBuilder.Entity<EdgeEntity>(e =>
        {
            e.ToTable("edges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Project).HasColumnName("project");
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.Properties).HasColumnName("properties").HasColumnType("json");
            e.HasIndex(x => new { x.SourceId, x.TargetId, x.Type }).IsUnique();
        });

        modelBuilder.Entity<CrossRepoEdgeEntity>(e =>
        {
            e.ToTable("cross_repo_edges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SourceProject).HasColumnName("source_project");
            e.Property(x => x.TargetProject).HasColumnName("target_project");
            e.Property(x => x.SourceNodeId).HasColumnName("source_node_id");
            e.Property(x => x.TargetNodeId).HasColumnName("target_node_id");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.Properties).HasColumnName("properties").HasColumnType("json");
            e.HasIndex(x => new { x.SourceNodeId, x.TargetNodeId, x.Type }).IsUnique();
        });

        modelBuilder.Entity<FileHashEntity>(e =>
        {
            e.ToTable("file_hashes");
            e.HasKey(f => new { f.Project, f.RelPath });
            e.Property(f => f.Project).HasColumnName("project");
            e.Property(f => f.RelPath).HasColumnName("rel_path");
            e.Property(f => f.ContentHash).HasColumnName("content_hash");
        });
    }

    private static void ConfigureAnalysis(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationHistoryEntity>(e =>
        {
            e.ToTable("migration_history");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id");
            e.Property(m => m.ScriptName).HasColumnName("script_name");
            e.Property(m => m.AppliedAt).HasColumnName("applied_at");
            e.HasIndex(m => m.ScriptName).IsUnique();
        });

        modelBuilder.Entity<RepositorySummaryEntity>(e =>
        {
            e.ToTable("repository_summaries");
            e.HasKey(s => s.Project);
            e.Property(s => s.Project).HasColumnName("project");
            e.Property(s => s.Summary).HasColumnName("summary");
            e.Property(s => s.Confidence).HasColumnName("confidence");
            e.Property(s => s.SourceHash).HasColumnName("source_hash");
            e.Property(s => s.ModelUsed).HasColumnName("model_used");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ProjectAnalysisEntity>(e =>
        {
            e.ToTable("project_analyses");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.Repo).HasColumnName("repo");
            e.Property(a => a.ProjectName).HasColumnName("project_name");
            e.Property(a => a.Summary).HasColumnName("summary");
            e.Property(a => a.Confidence).HasColumnName("confidence");
            e.Property(a => a.Endpoints).HasColumnName("endpoints");
            e.Property(a => a.Services).HasColumnName("services");
            e.Property(a => a.ExternalDependencies).HasColumnName("external_dependencies");
            e.Property(a => a.DatabaseTables).HasColumnName("database_tables");
            e.Property(a => a.ModelUsed).HasColumnName("model_used");
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
            e.Property(a => a.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(a => new { a.Repo, a.ProjectName }).IsUnique();
        });

        modelBuilder.Entity<AnalysisBatchEntity>(e =>
        {
            e.ToTable("analysis_batches");
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasColumnName("id");
            e.Property(b => b.Repo).HasColumnName("repo");
            e.Property(b => b.ProviderBatchId).HasColumnName("anthropic_batch_id");
            e.Property(b => b.ProviderName).HasColumnName("provider_name");
            e.Property(b => b.ExecutionMode).HasColumnName("execution_mode");
            e.Property(b => b.IncludeAllSource).HasColumnName("include_all_source");
            e.Property(b => b.Status).HasColumnName("status");
            e.Property(b => b.RequestCount).HasColumnName("request_count");
            e.Property(b => b.CompletedCount).HasColumnName("completed_count");
            e.Property(b => b.SubmittedAt).HasColumnName("submitted_at");
            e.Property(b => b.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(b => b.Repo);
            e.HasIndex(b => b.Status);
            e.HasIndex(b => b.ProviderBatchId);
        });

        modelBuilder.Entity<AnalysisBatchRequestEntity>(e =>
        {
            e.ToTable("analysis_batch_requests");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.Sequence).HasColumnName("sequence");
            e.Property(r => r.CustomId).HasColumnName("custom_id");
            e.Property(r => r.NodeId).HasColumnName("node_id");
            e.Property(r => r.NodeLabel).HasColumnName("node_label");
            e.Property(r => r.RequestPayloadJson).HasColumnName("request_payload_json");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.AttemptCount).HasColumnName("attempt_count");
            e.Property(r => r.ResponseText).HasColumnName("response_text");
            e.Property(r => r.ModelUsed).HasColumnName("model_used");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(r => r.BatchId);
            e.HasIndex(r => r.NodeId);
            e.HasIndex(r => r.CustomId);
        });

        modelBuilder.Entity<LlmUsageEntity>(e =>
        {
            e.ToTable("llm_usage");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id");
            e.Property(u => u.EventId).HasColumnName("event_id");
            e.Property(u => u.Username).HasColumnName("username");
            e.Property(u => u.Path).HasColumnName("path");
            e.Property(u => u.Provider).HasColumnName("provider");
            e.Property(u => u.Model).HasColumnName("model");
            e.Property(u => u.InputTokens).HasColumnName("input_tokens");
            e.Property(u => u.OutputTokens).HasColumnName("output_tokens");
            e.Property(u => u.TotalTokens).HasColumnName("total_tokens");
            e.Property(u => u.CreatedAt).HasColumnName("created_at");
            e.HasIndex(u => u.EventId).IsUnique();
            e.HasIndex(u => u.CreatedAt);
            e.HasIndex(u => u.Username);
            e.HasIndex(u => u.Path);
            e.HasIndex(u => u.Provider);
            e.HasIndex(u => new { u.Username, u.Path, u.CreatedAt });
        });

        modelBuilder.Entity<NodeAnalysisEntity>(e =>
        {
            e.ToTable("node_analysis");
            e.HasKey(n => n.NodeId);
            e.Property(n => n.NodeId).HasColumnName("node_id");
            e.Property(n => n.Description).HasColumnName("description");
            e.Property(n => n.Confidence).HasColumnName("confidence");
            e.Property(n => n.ModelUsed).HasColumnName("model_used");
            e.Property(n => n.CreatedAt).HasColumnName("created_at");
            e.Property(n => n.UpdatedAt).HasColumnName("updated_at");
        });
    }

    private static void ConfigureHealthAndSecurity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileMetricsEntity>(e =>
        {
            e.ToTable("file_metrics");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id");
            e.Property(f => f.Project).HasColumnName("project");
            e.Property(f => f.FilePath).HasColumnName("file_path");
            e.Property(f => f.DotnetProject).HasColumnName("dotnet_project");
            e.Property(f => f.Changes).HasColumnName("changes");
            e.Property(f => f.LinesAdded).HasColumnName("lines_added");
            e.Property(f => f.LinesRemoved).HasColumnName("lines_removed");
            e.Property(f => f.AuthorCount).HasColumnName("author_count");
            e.Property(f => f.LastChangeAt).HasColumnName("last_change_at");
            e.Property(f => f.ComplexityScore).HasColumnName("complexity_score");
            e.Property(f => f.MaxNestingDepth).HasColumnName("max_nesting_depth");
            e.Property(f => f.DeepNestingLines).HasColumnName("deep_nesting_lines");
            e.Property(f => f.FunctionCount).HasColumnName("function_count");
            e.Property(f => f.LongestFunction).HasColumnName("longest_function");
            e.Property(f => f.LintErrors).HasColumnName("lint_errors");
            e.Property(f => f.LintWarnings).HasColumnName("lint_warnings");
            e.Property(f => f.TrustScore).HasColumnName("trust_score");
            e.Property(f => f.MaxCouplingStrength).HasColumnName("max_coupling_strength");
            e.Property(f => f.CouplingPartners).HasColumnName("coupling_partners");
            e.Property(f => f.TruckFactor).HasColumnName("truck_factor");
            e.Property(f => f.TopAuthors).HasColumnName("top_authors").HasColumnType("json");
            e.Property(f => f.HealthScore).HasColumnName("health_score");
            e.Property(f => f.Role).HasColumnName("role");
            e.Property(f => f.RiskScore).HasColumnName("risk_score");
            e.Property(f => f.Churn30d).HasColumnName("churn_30d");
            e.Property(f => f.Churn90d).HasColumnName("churn_90d");
            e.Property(f => f.Churn365d).HasColumnName("churn_365d");
            e.Property(f => f.BugFixCommits90d).HasColumnName("bug_fix_commits_90d");
            e.Property(f => f.BugFixCommits365d).HasColumnName("bug_fix_commits_365d");
            e.Property(f => f.BugFixRatio365d).HasColumnName("bug_fix_ratio_365d");
            e.Property(f => f.RecurringChurnScore).HasColumnName("recurring_churn_score");
            e.Property(f => f.ComputedAt).HasColumnName("computed_at");
            e.Ignore(f => f.ConcernScore);
            e.Ignore(f => f.BugFixWeightedTouches365d);
            e.HasIndex(f => new { f.Project, f.FilePath }).IsUnique();
        });

        modelBuilder.Entity<ProjectHealthSummaryEntity>(e =>
        {
            e.ToTable("project_health_summaries");
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).HasColumnName("id");
            e.Property(h => h.Project).HasColumnName("project");
            e.Property(h => h.DotnetProject).HasColumnName("dotnet_project");
            e.Property(h => h.OverallHealth).HasColumnName("overall_health");
            e.Property(h => h.TotalFiles).HasColumnName("total_files");
            e.Property(h => h.HotspotCount).HasColumnName("hotspot_count");
            e.Property(h => h.AlertCount).HasColumnName("alert_count");
            e.Property(h => h.TopHotspots).HasColumnName("top_hotspots").HasColumnType("json");
            e.Property(h => h.ComputedAt).HasColumnName("computed_at");
            e.Ignore(h => h.HistoryMaturity);
            e.Ignore(h => h.HasSufficientHistoryForTrends);
            e.Ignore(h => h.ActivityStatus);
            e.Ignore(h => h.FirefightingStatus);
            e.Ignore(h => h.MonthlyCommitCounts);
            e.Ignore(h => h.VelocityLast6Months);
            e.Ignore(h => h.VelocityPrior6Months);
            e.Ignore(h => h.VelocityChangePercent);
            e.Ignore(h => h.DormantMonths12m);
            e.Ignore(h => h.MaxInactiveStreakMonths);
            e.Ignore(h => h.FirefightingCommits90d);
            e.Ignore(h => h.FirefightingCommits365d);
            e.Ignore(h => h.FirefightingRate90d);
            e.Ignore(h => h.FirefightingRate365d);
            e.HasIndex(h => new { h.Project, h.DotnetProject }).IsUnique();
        });

        modelBuilder.Entity<ProjectHealthAnalysisEntity>(e =>
        {
            e.ToTable("project_health_analyses");
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).HasColumnName("id");
            e.Property(h => h.Project).HasColumnName("project");
            e.Property(h => h.DotnetProject).HasColumnName("dotnet_project");
            e.Property(h => h.Analysis).HasColumnName("analysis");
            e.Property(h => h.Confidence).HasColumnName("confidence");
            e.Property(h => h.ModelUsed).HasColumnName("model_used");
            e.Property(h => h.CreatedAt).HasColumnName("created_at");
            e.Property(h => h.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(h => new { h.Project, h.DotnetProject }).IsUnique();
        });

        modelBuilder.Entity<SecurityFindingEntity>(e =>
        {
            e.ToTable("security_findings");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id");
            e.Property(f => f.Project).HasColumnName("project");
            e.Property(f => f.DotnetProject).HasColumnName("dotnet_project");
            e.Property(f => f.Category).HasColumnName("category");
            e.Property(f => f.Severity).HasColumnName("severity");
            e.Property(f => f.Title).HasColumnName("title");
            e.Property(f => f.Description).HasColumnName("description");
            e.Property(f => f.FilePath).HasColumnName("file_path");
            e.Property(f => f.LineNumber).HasColumnName("line_number");
            e.Property(f => f.Package).HasColumnName("package");
            e.Property(f => f.PackageVersion).HasColumnName("package_version");
            e.Property(f => f.Advisory).HasColumnName("advisory");
            e.Property(f => f.ComputedAt).HasColumnName("computed_at");
        });

        modelBuilder.Entity<ProjectSecuritySummaryEntity>(e =>
        {
            e.ToTable("project_security_summaries");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Project).HasColumnName("project");
            e.Property(s => s.SecurityScore).HasColumnName("security_score");
            e.Property(s => s.CriticalCount).HasColumnName("critical_count");
            e.Property(s => s.HighCount).HasColumnName("high_count");
            e.Property(s => s.MediumCount).HasColumnName("medium_count");
            e.Property(s => s.LowCount).HasColumnName("low_count");
            e.Property(s => s.Analysis).HasColumnName("analysis");
            e.Property(s => s.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(s => s.Project).IsUnique();
        });
    }

    private static void ConfigureReviews(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectDiagnosticEntity>(e =>
        {
            e.ToTable("project_diagnostics");
            e.HasKey(d => new { d.Project, d.DotnetProject, d.Source, d.DiagnosticKey });
            e.Property(d => d.Project).HasColumnName("project");
            e.Property(d => d.DotnetProject).HasColumnName("dotnet_project");
            e.Property(d => d.Source).HasColumnName("source");
            e.Property(d => d.DiagnosticKey).HasColumnName("diagnostic_key");
            e.Property(d => d.DiagnosticId).HasColumnName("diagnostic_id");
            e.Property(d => d.Severity).HasColumnName("severity");
            e.Property(d => d.Message).HasColumnName("message");
            e.Property(d => d.Category).HasColumnName("category");
            e.Property(d => d.FilePath).HasColumnName("file_path");
            e.Property(d => d.LineStart).HasColumnName("line_start");
            e.Property(d => d.LineEnd).HasColumnName("line_end");
            e.Property(d => d.ComputedAt).HasColumnName("computed_at");
        });

        modelBuilder.Entity<ProjectReviewRunEntity>(e =>
        {
            e.ToTable("project_review_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.Project).HasColumnName("project");
            e.Property(r => r.ProjectName).HasColumnName("project_name");
            e.Property(r => r.ReviewedCommitSha).HasColumnName("reviewed_commit_sha");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.ReviewMode).HasColumnName("review_mode");
            e.Property(r => r.PromptVersion).HasColumnName("prompt_version");
            e.Property(r => r.OverviewJson).HasColumnName("overview_json");
            e.Property(r => r.ModelUsed).HasColumnName("model_used");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at");
            e.Property(r => r.Error).HasColumnName("error");
        });

        modelBuilder.Entity<ProjectReviewFindingEntity>(e =>
        {
            e.ToTable("project_review_findings");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id");
            e.Property(f => f.ReviewRunId).HasColumnName("review_run_id");
            e.Property(f => f.Ordinal).HasColumnName("ordinal");
            e.Property(f => f.Severity).HasColumnName("severity");
            e.Property(f => f.Category).HasColumnName("category");
            e.Property(f => f.Title).HasColumnName("title");
            e.Property(f => f.Explanation).HasColumnName("explanation");
            e.Property(f => f.Evidence).HasColumnName("evidence");
            e.Property(f => f.FilePath).HasColumnName("file_path");
            e.Property(f => f.LineStart).HasColumnName("line_start");
            e.Property(f => f.LineEnd).HasColumnName("line_end");
            e.Property(f => f.SuggestedImprovement).HasColumnName("suggested_improvement");
            e.Property(f => f.Confidence).HasColumnName("confidence");
            e.Property(f => f.ProvenanceJson).HasColumnName("provenance_json");
        });

        modelBuilder.Entity<RepositoryReviewRunEntity>(e =>
        {
            e.ToTable("repository_review_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.Repo).HasColumnName("repo");
            e.Property(r => r.ReviewedCommitSha).HasColumnName("reviewed_commit_sha");
            e.Property(r => r.BaselineReviewRunId).HasColumnName("baseline_review_run_id");
            e.Property(r => r.BaselineCommitSha).HasColumnName("baseline_commit_sha");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.ReviewMode).HasColumnName("review_mode");
            e.Property(r => r.PromptVersion).HasColumnName("prompt_version");
            e.Property(r => r.OverviewJson).HasColumnName("overview_json");
            e.Property(r => r.ModelUsed).HasColumnName("model_used");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at");
            e.Property(r => r.Error).HasColumnName("error");
        });

        modelBuilder.Entity<RepositoryReviewProjectSectionEntity>(e =>
        {
            e.ToTable("repository_review_project_sections");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.ReviewRunId).HasColumnName("review_run_id");
            e.Property(s => s.ProjectName).HasColumnName("project_name");
            e.Property(s => s.Overview).HasColumnName("overview");
            e.Property(s => s.StrengthsJson).HasColumnName("strengths_json");
            e.Property(s => s.ReviewedAreasJson).HasColumnName("reviewed_areas_json");
            e.Property(s => s.SkippedAreasJson).HasColumnName("skipped_areas_json");
            e.Property(s => s.FollowUpsJson).HasColumnName("follow_ups_json");
            e.Property(s => s.ReusedFromBaseline).HasColumnName("reused_from_baseline");
        });

        modelBuilder.Entity<RepositoryReviewFindingEntity>(e =>
        {
            e.ToTable("repository_review_findings");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id");
            e.Property(f => f.ReviewRunId).HasColumnName("review_run_id");
            e.Property(f => f.ProjectName).HasColumnName("project_name");
            e.Property(f => f.Ordinal).HasColumnName("ordinal");
            e.Property(f => f.Severity).HasColumnName("severity");
            e.Property(f => f.Category).HasColumnName("category");
            e.Property(f => f.Title).HasColumnName("title");
            e.Property(f => f.Explanation).HasColumnName("explanation");
            e.Property(f => f.Evidence).HasColumnName("evidence");
            e.Property(f => f.FilePath).HasColumnName("file_path");
            e.Property(f => f.LineStart).HasColumnName("line_start");
            e.Property(f => f.LineEnd).HasColumnName("line_end");
            e.Property(f => f.SuggestedImprovement).HasColumnName("suggested_improvement");
            e.Property(f => f.Confidence).HasColumnName("confidence");
            e.Property(f => f.ProvenanceJson).HasColumnName("provenance_json");
        });
    }

    private static void ConfigureWiki(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConventionPageEntity>(e =>
        {
            e.ToTable("convention_pages");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.Slug).HasColumnName("slug");
            e.Property(p => p.Title).HasColumnName("title");
            e.Property(p => p.Content).HasColumnName("content");
            e.Property(p => p.Author).HasColumnName("author");
            e.Property(p => p.Revision).HasColumnName("revision");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(p => p.Slug).IsUnique();
        });

        modelBuilder.Entity<ConventionRevisionEntity>(e =>
        {
            e.ToTable("convention_revisions");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.PageId).HasColumnName("page_id");
            e.Property(r => r.Revision).HasColumnName("revision");
            e.Property(r => r.Title).HasColumnName("title");
            e.Property(r => r.Content).HasColumnName("content");
            e.Property(r => r.Author).HasColumnName("author");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.HasIndex(r => new { r.PageId, r.Revision }).IsUnique();
        });

        modelBuilder.Entity<WikiSectionEntity>(e =>
        {
            e.ToTable("wiki_sections");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Slug).HasColumnName("slug");
            e.Property(s => s.Title).HasColumnName("title");
            e.Property(s => s.Description).HasColumnName("description");
            e.Property(s => s.Icon).HasColumnName("icon");
            e.Property(s => s.SortOrder).HasColumnName("sort_order");
            e.Property(s => s.IsSystem).HasColumnName("is_system");
            e.Property(s => s.AllowUserPages).HasColumnName("allow_user_pages");
            e.Property(s => s.HasRawContent).HasColumnName("has_raw_content");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(s => s.Slug).IsUnique();
        });

        modelBuilder.Entity<WikiPageEntity>(e =>
        {
            e.ToTable("wiki_pages");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.SectionId).HasColumnName("section_id");
            e.Property(p => p.ParentId).HasColumnName("parent_id");
            e.Property(p => p.Slug).HasColumnName("slug");
            e.Property(p => p.Title).HasColumnName("title");
            e.Property(p => p.Content).HasColumnName("content");
            e.Property(p => p.RawContent).HasColumnName("raw_content");
            e.Property(p => p.Author).HasColumnName("author");
            e.Property(p => p.Revision).HasColumnName("revision");
            e.Property(p => p.SortOrder).HasColumnName("sort_order");
            e.Property(p => p.IsAutoGenerated).HasColumnName("is_auto_generated");
            e.Property(p => p.Depth).HasColumnName("depth");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(p => new { p.SectionId, p.ParentId, p.Slug }).IsUnique();
            e.HasIndex(p => p.SectionId);
            e.HasIndex(p => p.ParentId);
        });

        modelBuilder.Entity<WikiRevisionEntity>(e =>
        {
            e.ToTable("wiki_revisions");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.PageId).HasColumnName("page_id");
            e.Property(r => r.Revision).HasColumnName("revision");
            e.Property(r => r.Title).HasColumnName("title");
            e.Property(r => r.Content).HasColumnName("content");
            e.Property(r => r.RawContent).HasColumnName("raw_content");
            e.Property(r => r.Author).HasColumnName("author");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.HasIndex(r => new { r.PageId, r.Revision }).IsUnique();
        });

        modelBuilder.Entity<WikiAttachmentEntity>(e =>
        {
            e.ToTable("wiki_attachments");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.PageId).HasColumnName("page_id");
            e.Property(a => a.Filename).HasColumnName("filename");
            e.Property(a => a.StoragePath).HasColumnName("storage_path");
            e.Property(a => a.ContentType).HasColumnName("content_type");
            e.Property(a => a.SizeBytes).HasColumnName("size_bytes");
            e.Property(a => a.UploadedBy).HasColumnName("uploaded_by");
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
            e.HasIndex(a => a.PageId);
        });
    }

    private static void ConfigureExclusions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExclusionRuleEntity>(e =>
        {
            e.ToTable("exclusion_rules");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.TargetType).HasColumnName("target_type");
            e.Property(r => r.TargetValue).HasColumnName("target_value");
            e.Property(r => r.ExclusionType).HasColumnName("exclusion_type");
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(r => new { r.TargetType, r.TargetValue });
            e.HasIndex(r => r.ExclusionType);
        });
    }

    private static void ConfigureJobSchedules(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobScheduleEntity>(e =>
        {
            e.ToTable("job_schedules");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Name).HasColumnName("name");
            e.Property(s => s.JobType).HasColumnName("job_type");
            e.Property(s => s.IsEnabled).HasColumnName("is_enabled");
            e.Property(s => s.CronExpression).HasColumnName("cron_expression");
            e.Property(s => s.TimeZoneId).HasColumnName("time_zone_id");
            e.Property(s => s.ArgsJson).HasColumnName("args_json").HasColumnType("json");
            e.Property(s => s.NextRunUtc).HasColumnName("next_run_utc");
            e.Property(s => s.LastRunStartedUtc).HasColumnName("last_run_started_utc");
            e.Property(s => s.LastRunCompletedUtc).HasColumnName("last_run_completed_utc");
            e.Property(s => s.LastRunStatus).HasColumnName("last_run_status");
            e.Property(s => s.LastError).HasColumnName("last_error");
            e.Property(s => s.LeaseAcquiredUtc).HasColumnName("lease_acquired_utc");
            e.Property(s => s.LeaseOwner).HasColumnName("lease_owner");
            e.Property(s => s.LeaseExpiresUtc).HasColumnName("lease_expires_utc");
            e.Property(s => s.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(s => s.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.HasIndex(s => s.Name).IsUnique();
            e.HasIndex(s => new { s.IsEnabled, s.NextRunUtc, s.LeaseExpiresUtc });
            e.HasIndex(s => s.LeaseOwner);
        });
    }

    private static void ConfigureAdminAndDatabaseSources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminUserEntity>(e =>
        {
            e.ToTable("admin_users");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.Username).HasColumnName("username");
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
            e.HasIndex(a => a.Username).IsUnique();
        });

        modelBuilder.Entity<SettingsOverrideEntity>(e =>
        {
            e.ToTable("settings_overrides");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.SettingsJson).HasColumnName("settings_json").HasColumnType("json");
            e.Property(s => s.UpdatedBy).HasColumnName("updated_by");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AgentPromptOverrideEntity>(e =>
        {
            e.ToTable("agent_prompt_overrides");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.PromptKey).HasColumnName("prompt_key");
            e.Property(p => p.PromptText).HasColumnName("prompt_text");
            e.Property(p => p.UpdatedBy).HasColumnName("updated_by");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(p => p.PromptKey).IsUnique();
            e.HasIndex(p => p.UpdatedAt);
        });

        modelBuilder.Entity<DatabaseSourceEntity>(e =>
        {
            e.ToTable("database_sources");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnName("id");
            e.Property(d => d.ServerName).HasColumnName("server_name");
            e.Property(d => d.DatabaseName).HasColumnName("database_name");
            e.Property(d => d.ConnectionString).HasColumnName("connection_string");
            e.Property(d => d.Enabled).HasColumnName("enabled");
            e.Property(d => d.LastSyncedAt).HasColumnName("last_synced_at");
            e.Property(d => d.CreatedAt).HasColumnName("created_at");
            e.Property(d => d.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(d => new { d.ServerName, d.DatabaseName }).IsUnique();
        });

        modelBuilder.Entity<LlmConfigEntryEntity>(e =>
        {
            e.ToTable("llm_config");
            e.HasKey(c => c.ConfigKey);
            e.Property(c => c.ConfigKey).HasColumnName("config_key").HasMaxLength(255);
            e.Property(c => c.ConfigValue).HasColumnName("config_value").HasColumnType("LONGTEXT");
            e.Property(c => c.UpdatedBy).HasColumnName("updated_by").HasMaxLength(255);
            e.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc");
        });

        modelBuilder.Entity<LlmProviderModelEntity>(e =>
        {
            e.ToTable("llm_provider_models");
            e.HasKey(m => new { m.ProviderKey, m.ModelId });
            e.Property(m => m.ProviderKey).HasColumnName("provider_key").HasMaxLength(64);
            e.Property(m => m.ModelId).HasColumnName("model_id").HasMaxLength(255);
            e.Property(m => m.DisplayOrder).HasColumnName("display_order");
            e.HasIndex(m => new { m.ProviderKey, m.DisplayOrder });
        });
    }

    private static void ConfigureMcpAndTelemetry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpPersonalAccessTokenEntity>(e =>
        {
            e.ToTable("mcp_personal_access_tokens");
            e.HasKey(token => token.Id);
            e.Property(token => token.Id).HasColumnName("id");
            e.Property(token => token.Username).HasColumnName("username");
            e.Property(token => token.TokenName).HasColumnName("token_name");
            e.Property(token => token.TokenPrefixValue).HasColumnName("token_prefix");
            e.Property(token => token.TokenHash).HasColumnName("token_hash");
            e.Property(token => token.LastFour).HasColumnName("last_four");
            e.Property(token => token.CreatedAt).HasColumnName("created_at");
            e.Property(token => token.ExpiresAt).HasColumnName("expires_at");
            e.Property(token => token.RevokedAt).HasColumnName("revoked_at");
            e.Property(token => token.LastUsedAt).HasColumnName("last_used_at");
            e.Property(token => token.LastUsedFrom).HasColumnName("last_used_from");
            e.HasIndex(token => token.Username);
            e.HasIndex(token => token.ExpiresAt);
            e.HasIndex(token => token.RevokedAt);
            e.HasIndex(token => new { token.Username, token.RevokedAt, token.ExpiresAt });
            e.HasIndex(token => token.TokenHash).IsUnique();
        });

        modelBuilder.Entity<McpToolInvocationEntity>(e =>
        {
            e.ToTable("mcp_tool_invocations");
            e.HasKey(invocation => invocation.Id);
            e.Property(invocation => invocation.Id).HasColumnName("id");
            e.Property(invocation => invocation.EventId).HasColumnName("event_id");
            e.Property(invocation => invocation.Username).HasColumnName("username");
            e.Property(invocation => invocation.TokenId).HasColumnName("token_id");
            e.Property(invocation => invocation.ToolName).HasColumnName("tool_name");
            e.Property(invocation => invocation.Success).HasColumnName("success");
            e.Property(invocation => invocation.DurationMs).HasColumnName("duration_ms");
            e.Property(invocation => invocation.ErrorCode).HasColumnName("error_code");
            e.Property(invocation => invocation.CreatedAt).HasColumnName("created_at");
            e.HasIndex(invocation => invocation.EventId).IsUnique();
            e.HasIndex(invocation => invocation.CreatedAt);
            e.HasIndex(invocation => new { invocation.Username, invocation.CreatedAt });
            e.HasIndex(invocation => new { invocation.ToolName, invocation.CreatedAt });
            e.HasIndex(invocation => new { invocation.TokenId, invocation.CreatedAt });
            e.HasIndex(invocation => new { invocation.Success, invocation.CreatedAt });
        });
    }

    private static void ConfigureIndexerRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IndexerRunEntity>(e =>
        {
            e.ToTable("indexer_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.Operation).HasColumnName("operation");
            e.Property(r => r.RequestedByUsername).HasColumnName("requested_by_username");
            e.Property(r => r.Target).HasColumnName("target");
            e.Property(r => r.ArgsJson).HasColumnName("args_json");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.Message).HasColumnName("message");
            e.Property(r => r.Error).HasColumnName("error");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(r => new { r.Status, r.CreatedAt });
            e.HasIndex(r => new { r.RequestedByUsername, r.CreatedAt });
            e.HasIndex(r => new { r.Operation, r.CreatedAt });
        });
    }

    private static void ConfigureAssistantRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssistantRunEntity>(e =>
        {
            e.ToTable("assistant_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.ChatId).HasColumnName("chat_id");
            e.Property(r => r.Username).HasColumnName("username");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.Question).HasColumnName("question");
            e.Property(r => r.Context).HasColumnName("context");
            e.Property(r => r.HistoryJson).HasColumnName("history_json").HasColumnType("json");
            e.Property(r => r.ProviderRequested).HasColumnName("provider_requested");
            e.Property(r => r.ModelRequested).HasColumnName("model_requested");
            e.Property(r => r.ProviderUsed).HasColumnName("provider_used");
            e.Property(r => r.ModelUsed).HasColumnName("model_used");
            e.Property(r => r.FinalAnswer).HasColumnName("final_answer");
            e.Property(r => r.WarningsJson).HasColumnName("warnings_json").HasColumnType("json");
            e.Property(r => r.Error).HasColumnName("error");
            e.Property(r => r.MessageIndexStart).HasColumnName("message_index_start");
            e.Property(r => r.MessageIndexEnd).HasColumnName("message_index_end");
            e.Property(r => r.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(r => r.RequestHash).HasColumnName("request_hash");
            e.Property(r => r.ExecutionStateJson).HasColumnName("execution_state_json").HasColumnType("json");
            e.Property(r => r.ExecutionOwner).HasColumnName("execution_owner");
            e.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
            e.Property(r => r.CancelRequestedAt).HasColumnName("cancel_requested_at");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at");
            e.Property(r => r.LastSequence).HasColumnName("last_sequence");
            e.HasIndex(r => new { r.Username, r.CreatedAt });
            e.HasIndex(r => new { r.Username, r.ChatId, r.CreatedAt });
            e.HasIndex(r => new { r.Status, r.CreatedAt });
            e.HasIndex(r => new { r.Status, r.LeaseExpiresAt });
            e.HasIndex(r => r.ExecutionOwner);
            e.HasIndex(r => new { r.Username, r.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<AssistantChatMessageEntity>(e =>
        {
            e.ToTable("assistant_chat_messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id");
            e.Property(m => m.Username).HasColumnName("username");
            e.Property(m => m.ChatId).HasColumnName("chat_id");
            e.Property(m => m.MessageIndex).HasColumnName("message_index");
            e.Property(m => m.Role).HasColumnName("role");
            e.Property(m => m.Content).HasColumnName("content");
            e.Property(m => m.SourceRunId).HasColumnName("source_run_id");
            e.Property(m => m.CreatedAt).HasColumnName("created_at");
            e.HasIndex(m => new { m.Username, m.ChatId, m.MessageIndex }).IsUnique();
            e.HasIndex(m => new { m.Username, m.ChatId, m.CreatedAt });
        });

        modelBuilder.Entity<AssistantRunEventEntity>(e =>
        {
            e.ToTable("assistant_run_events");
            e.HasKey(evt => evt.Id);
            e.Property(evt => evt.Id).HasColumnName("id");
            e.Property(evt => evt.RunId).HasColumnName("run_id");
            e.Property(evt => evt.Sequence).HasColumnName("sequence");
            e.Property(evt => evt.Type).HasColumnName("type");
            e.Property(evt => evt.ContentJson).HasColumnName("content_json").HasColumnType("json");
            e.Property(evt => evt.CreatedAt).HasColumnName("created_at");
            e.HasIndex(evt => new { evt.RunId, evt.Sequence }).IsUnique();
            e.HasIndex(evt => new { evt.RunId, evt.CreatedAt });
        });

        modelBuilder.Entity<AssistantDebugExchangeEntity>(e =>
        {
            e.ToTable("assistant_debug_exchanges");
            e.HasKey(exchange => exchange.Id);
            e.Property(exchange => exchange.Id).HasColumnName("id");
            e.Property(exchange => exchange.RunId).HasColumnName("run_id");
            e.Property(exchange => exchange.ChatId).HasColumnName("chat_id");
            e.Property(exchange => exchange.Username).HasColumnName("username");
            e.Property(exchange => exchange.ExchangeIndex).HasColumnName("exchange_index");
            e.Property(exchange => exchange.TurnIndex).HasColumnName("turn_index");
            e.Property(exchange => exchange.Provider).HasColumnName("provider");
            e.Property(exchange => exchange.Model).HasColumnName("model");
            e.Property(exchange => exchange.RequestId).HasColumnName("request_id");
            e.Property(exchange => exchange.ResponseId).HasColumnName("response_id");
            e.Property(exchange => exchange.ToolUsesJson).HasColumnName("tool_uses_json").HasColumnType("json");
            e.Property(exchange => exchange.RequestMetadataJson).HasColumnName("request_metadata_json").HasColumnType("json");
            e.Property(exchange => exchange.ResponseMetadataJson).HasColumnName("response_metadata_json").HasColumnType("json");
            e.Property(exchange => exchange.RequestBodyJson).HasColumnName("request_body_json");
            e.Property(exchange => exchange.ResponseBodyJson).HasColumnName("response_body_json");
            e.Property(exchange => exchange.RequestText).HasColumnName("request_text");
            e.Property(exchange => exchange.ResponseText).HasColumnName("response_text");
            e.Property(exchange => exchange.InputTokens).HasColumnName("input_tokens");
            e.Property(exchange => exchange.OutputTokens).HasColumnName("output_tokens");
            e.Property(exchange => exchange.TotalTokens).HasColumnName("total_tokens");
            e.Property(exchange => exchange.CreatedAt).HasColumnName("created_at");
            e.HasIndex(exchange => new { exchange.RunId, exchange.ExchangeIndex }).IsUnique();
            e.HasIndex(exchange => new { exchange.RunId, exchange.CreatedAt });
            e.HasIndex(exchange => new { exchange.Username, exchange.ChatId, exchange.CreatedAt });
        });

        modelBuilder.Entity<AssistantDebugTraceAuditEntity>(e =>
        {
            e.ToTable("assistant_debug_trace_audit");
            e.HasKey(audit => audit.Id);
            e.Property(audit => audit.Id).HasColumnName("id");
            e.Property(audit => audit.RunId).HasColumnName("run_id");
            e.Property(audit => audit.ChatId).HasColumnName("chat_id");
            e.Property(audit => audit.RunUsername).HasColumnName("run_username");
            e.Property(audit => audit.ViewedByUsername).HasColumnName("viewed_by_username");
            e.Property(audit => audit.RemoteIp).HasColumnName("remote_ip");
            e.Property(audit => audit.UserAgent).HasColumnName("user_agent");
            e.Property(audit => audit.ViewedAt).HasColumnName("viewed_at");
            e.HasIndex(audit => new { audit.RunId, audit.ViewedAt });
            e.HasIndex(audit => new { audit.ViewedByUsername, audit.ViewedAt });
        });
    }
}
