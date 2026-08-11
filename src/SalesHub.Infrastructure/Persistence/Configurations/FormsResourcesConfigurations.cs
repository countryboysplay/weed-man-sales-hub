using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class FormConfiguration : IEntityTypeConfiguration<Form>
{
    public void Configure(EntityTypeBuilder<Form> builder)
    {
        builder.ToTable("forms");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(f => f.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(f => f.ExternalUrl).HasMaxLength(2048);
    }
}

public sealed class FormVersionConfiguration : IEntityTypeConfiguration<FormVersion>
{
    public void Configure(EntityTypeBuilder<FormVersion> builder)
    {
        builder.ToTable("form_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.DefinitionJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(v => new { v.FormId, v.VersionNumber }).IsUnique();
    }
}

public sealed class FormSubmissionConfiguration : IEntityTypeConfiguration<FormSubmission>
{
    public void Configure(EntityTypeBuilder<FormSubmission> builder)
    {
        builder.ToTable("form_submissions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.AnswersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(s => new { s.FormId, s.SubmittedAtUtc });
        builder.HasIndex(s => s.UserId);
    }
}

public sealed class EmailRequestConfiguration : IEntityTypeConfiguration<EmailRequest>
{
    public void Configure(EntityTypeBuilder<EmailRequest> builder)
    {
        builder.ToTable("email_requests");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Cid).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CustomerEmail).HasMaxLength(320).IsRequired();
        builder.Property(e => e.QuoteType).HasMaxLength(64);
        builder.Property(e => e.LawnArea).HasMaxLength(128);
        builder.Property(e => e.Coverage).HasMaxLength(256);
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}

public sealed class ResourceFolderConfiguration : IEntityTypeConfiguration<ResourceFolder>
{
    public void Configure(EntityTypeBuilder<ResourceFolder> builder)
    {
        builder.ToTable("resource_folders");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).HasMaxLength(128).IsRequired();
        builder.HasIndex(f => f.ParentId);
    }
}

public sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("resources");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Title).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(2000);
        builder.Property(r => r.ExternalUrl).HasMaxLength(2048);
        builder.HasIndex(r => r.FolderId);
    }
}

public sealed class ResourceFavoriteConfiguration : IEntityTypeConfiguration<ResourceFavorite>
{
    public void Configure(EntityTypeBuilder<ResourceFavorite> builder)
    {
        builder.ToTable("resource_favorites");
        builder.HasKey(f => new { f.UserId, f.ResourceId });
    }
}

public sealed class ResourceDownloadAuditConfiguration
    : IEntityTypeConfiguration<ResourceDownloadAudit>
{
    public void Configure(EntityTypeBuilder<ResourceDownloadAudit> builder)
    {
        builder.ToTable("resource_download_audit");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ResourceTitle).HasMaxLength(256);
        builder.HasIndex(a => a.OccurredAtUtc); // 365-day retention sweep
    }
}
