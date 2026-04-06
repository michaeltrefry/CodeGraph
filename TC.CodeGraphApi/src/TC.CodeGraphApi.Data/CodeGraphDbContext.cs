using Microsoft.EntityFrameworkCore;

namespace TC.CodeGraphApi.Data;

public class CodeGraphDbContext : DbContext
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
    public DbSet<NodeAnalysisEntity> NodeAnalyses => Set<NodeAnalysisEntity>();
    public DbSet<FileMetricsEntity> FileMetrics => Set<FileMetricsEntity>();
    public DbSet<ProjectHealthSummaryEntity> ProjectHealthSummaries => Set<ProjectHealthSummaryEntity>();
    public DbSet<ProjectHealthAnalysisEntity> ProjectHealthAnalyses => Set<ProjectHealthAnalysisEntity>();
    public DbSet<ConventionPageEntity> ConventionPages => Set<ConventionPageEntity>();
    public DbSet<ConventionRevisionEntity> ConventionRevisions => Set<ConventionRevisionEntity>();
    public DbSet<WikiSectionEntity> WikiSections => Set<WikiSectionEntity>();
    public DbSet<WikiPageEntity> WikiPages => Set<WikiPageEntity>();
    public DbSet<WikiRevisionEntity> WikiRevisions => Set<WikiRevisionEntity>();
    public DbSet<WikiAttachmentEntity> WikiAttachments => Set<WikiAttachmentEntity>();
    public DbSet<SecurityFindingEntity> SecurityFindings => Set<SecurityFindingEntity>();
    public DbSet<ProjectSecuritySummaryEntity> ProjectSecuritySummaries => Set<ProjectSecuritySummaryEntity>();
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();
    public DbSet<SettingsOverrideEntity> SettingsOverrides => Set<SettingsOverrideEntity>();
    public DbSet<ExclusionRuleEntity> ExclusionRules => Set<ExclusionRuleEntity>();

    public CodeGraphDbContext(DbContextOptions<CodeGraphDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RepositoryEntity>(e =>
        {
            e.ToTable("repositories");
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.RepoUrl).HasColumnName("repo_url");
            e.Property(p => p.GitLabGroup).HasColumnName("gitlab_group");
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
        });

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

        modelBuilder.Entity<MigrationHistoryEntity>(e =>
        {
            e.ToTable("migration_history");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id");
            e.Property(m => m.ScriptName).HasColumnName("script_name");
            e.Property(m => m.AppliedAt).HasColumnName("applied_at");
            e.HasIndex(m => m.ScriptName).IsUnique();
        });

        modelBuilder.Entity<AnalysisBatchEntity>(e =>
        {
            e.ToTable("analysis_batches");
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasColumnName("id");
            e.Property(b => b.Repo).HasColumnName("repo");
            e.Property(b => b.AnthropicBatchId).HasColumnName("anthropic_batch_id");
            e.Property(b => b.Status).HasColumnName("status");
            e.Property(b => b.RequestCount).HasColumnName("request_count");
            e.Property(b => b.CompletedCount).HasColumnName("completed_count");
            e.Property(b => b.SubmittedAt).HasColumnName("submitted_at");
            e.Property(b => b.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(b => b.Repo);
            e.HasIndex(b => b.Status);
            e.HasIndex(b => b.AnthropicBatchId);
        });

        modelBuilder.Entity<AnalysisBatchRequestEntity>(e =>
        {
            e.ToTable("analysis_batch_requests");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.CustomId).HasColumnName("custom_id");
            e.Property(r => r.NodeId).HasColumnName("node_id");
            e.Property(r => r.NodeLabel).HasColumnName("node_label");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(r => r.BatchId);
            e.HasIndex(r => r.NodeId);
            e.HasIndex(r => r.CustomId);
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
            e.Property(f => f.MaxCouplingStrength).HasColumnName("max_coupling_strength");
            e.Property(f => f.CouplingPartners).HasColumnName("coupling_partners");
            e.Property(f => f.TruckFactor).HasColumnName("truck_factor");
            e.Property(f => f.TopAuthors).HasColumnName("top_authors").HasColumnType("json");
            e.Property(f => f.HealthScore).HasColumnName("health_score");
            e.Property(f => f.Role).HasColumnName("role");
            e.Property(f => f.RiskScore).HasColumnName("risk_score");
            e.Property(f => f.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(f => new { f.Project, f.HealthScore });
        });

        modelBuilder.Entity<ProjectHealthSummaryEntity>(e =>
        {
            e.ToTable("project_health_summaries");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.Project).HasColumnName("project");
            e.Property(p => p.DotnetProject).HasColumnName("dotnet_project");
            e.Property(p => p.OverallHealth).HasColumnName("overall_health");
            e.Property(p => p.TotalFiles).HasColumnName("total_files");
            e.Property(p => p.HotspotCount).HasColumnName("hotspot_count");
            e.Property(p => p.AlertCount).HasColumnName("alert_count");
            e.Property(p => p.TopHotspots).HasColumnName("top_hotspots").HasColumnType("json");
            e.Property(p => p.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(p => p.Project);
        });

        modelBuilder.Entity<ProjectHealthAnalysisEntity>(e =>
        {
            e.ToTable("project_health_analyses");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.Project).HasColumnName("project");
            e.Property(a => a.DotnetProject).HasColumnName("dotnet_project");
            e.Property(a => a.Analysis).HasColumnName("analysis");
            e.Property(a => a.Confidence).HasColumnName("confidence");
            e.Property(a => a.ModelUsed).HasColumnName("model_used");
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
            e.Property(a => a.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(a => new { a.Project, a.DotnetProject }).IsUnique();
        });

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

        modelBuilder.Entity<SecurityFindingEntity>(e =>
        {
            e.ToTable("security_findings");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Project).HasColumnName("project");
            e.Property(s => s.DotnetProject).HasColumnName("dotnet_project");
            e.Property(s => s.Category).HasColumnName("category");
            e.Property(s => s.Severity).HasColumnName("severity");
            e.Property(s => s.Title).HasColumnName("title");
            e.Property(s => s.Description).HasColumnName("description");
            e.Property(s => s.FilePath).HasColumnName("file_path");
            e.Property(s => s.LineNumber).HasColumnName("line_number");
            e.Property(s => s.Package).HasColumnName("package");
            e.Property(s => s.PackageVersion).HasColumnName("package_version");
            e.Property(s => s.Advisory).HasColumnName("advisory");
            e.Property(s => s.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(s => s.Project);
            e.HasIndex(s => new { s.Project, s.Severity });
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
            e.Property(s => s.SettingsJson).HasColumnName("settings_json");
            e.Property(s => s.UpdatedBy).HasColumnName("updated_by");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

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
            e.HasIndex(r => new { r.TargetType, r.TargetValue }).IsUnique();
        });
    }
}
