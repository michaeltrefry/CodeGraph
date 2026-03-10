using Microsoft.EntityFrameworkCore;

namespace TC.CodeGraphApi.Data;

public class CodeGraphDbContext : DbContext
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<NodeEntity> Nodes => Set<NodeEntity>();
    public DbSet<EdgeEntity> Edges => Set<EdgeEntity>();
    public DbSet<CrossRepoEdgeEntity> CrossRepoEdges => Set<CrossRepoEdgeEntity>();
    public DbSet<FileHashEntity> FileHashes => Set<FileHashEntity>();
    public DbSet<ProjectSummaryEntity> ProjectSummaries => Set<ProjectSummaryEntity>();
    public DbSet<SyncStateEntity> SyncStates => Set<SyncStateEntity>();
    public DbSet<MigrationHistoryEntity> MigrationHistory => Set<MigrationHistoryEntity>();

    public CodeGraphDbContext(DbContextOptions<CodeGraphDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEntity>(e =>
        {
            e.ToTable("projects");
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.RepoUrl).HasColumnName("repo_url");
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
            e.Property(n => n.Label).HasColumnName("label");
            e.Property(n => n.Name).HasColumnName("name");
            e.Property(n => n.QualifiedName).HasColumnName("qualified_name");
            e.Property(n => n.FilePath).HasColumnName("file_path");
            e.Property(n => n.StartLine).HasColumnName("start_line");
            e.Property(n => n.EndLine).HasColumnName("end_line");
            e.Property(n => n.Properties).HasColumnName("properties").HasColumnType("json");
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

        modelBuilder.Entity<ProjectSummaryEntity>(e =>
        {
            e.ToTable("project_summaries");
            e.HasKey(s => s.Project);
            e.Property(s => s.Project).HasColumnName("project");
            e.Property(s => s.Summary).HasColumnName("summary");
            e.Property(s => s.Confidence).HasColumnName("confidence");
            e.Property(s => s.SourceHash).HasColumnName("source_hash");
            e.Property(s => s.ModelUsed).HasColumnName("model_used");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
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
    }
}
