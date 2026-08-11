using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class TaskDefinitionConfiguration : IEntityTypeConfiguration<TaskDefinition>
{
    public void Configure(EntityTypeBuilder<TaskDefinition> builder)
    {
        builder.ToTable("task_definitions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Title).HasMaxLength(256).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(8000);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Recurrence).HasConversion<string>().HasMaxLength(16);
    }
}

public sealed class TaskInstanceConfiguration : IEntityTypeConfiguration<TaskInstance>
{
    public void Configure(EntityTypeBuilder<TaskInstance> builder)
    {
        builder.ToTable("task_instances");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.PeriodKey).HasMaxLength(16);
        builder.HasIndex(t => new { t.AssigneeUserId, t.Status });
        builder.HasIndex(t => new { t.DefinitionId, t.AssigneeUserId, t.PeriodKey }).IsUnique();
    }
}

public sealed class TaskCommentConfiguration : IEntityTypeConfiguration<TaskComment>
{
    public void Configure(EntityTypeBuilder<TaskComment> builder)
    {
        builder.ToTable("task_comments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Body).HasMaxLength(4000).IsRequired();
        builder.HasIndex(c => c.InstanceId);
    }
}

public sealed class TaskAttachmentConfiguration : IEntityTypeConfiguration<TaskAttachment>
{
    public void Configure(EntityTypeBuilder<TaskAttachment> builder)
    {
        builder.ToTable("task_attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.OriginalName).HasMaxLength(512);
        builder.Property(a => a.ContentType).HasMaxLength(255);
        builder.HasIndex(a => a.DefinitionId);
    }
}

public sealed class RecognitionBadgeConfiguration : IEntityTypeConfiguration<RecognitionBadge>
{
    public void Configure(EntityTypeBuilder<RecognitionBadge> builder)
    {
        builder.ToTable("recognition_badges");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).HasMaxLength(64).IsRequired();
        builder.Property(b => b.Emoji).HasMaxLength(16);
        builder.HasIndex(b => b.Name).IsUnique();
    }
}

public sealed class RecognitionConfiguration : IEntityTypeConfiguration<Recognition>
{
    public void Configure(EntityTypeBuilder<Recognition> builder)
    {
        builder.ToTable("recognitions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Category).HasMaxLength(64);
        builder.Property(r => r.Message).HasMaxLength(2000);
        builder.HasIndex(r => r.ArchivedAtUtc);
        builder.HasIndex(r => r.RecipientUserId);
    }
}

public sealed class RecognitionReactionConfiguration : IEntityTypeConfiguration<RecognitionReaction>
{
    public void Configure(EntityTypeBuilder<RecognitionReaction> builder)
    {
        builder.ToTable("recognition_reactions");
        builder.HasKey(r => new { r.RecognitionId, r.UserId, r.Reaction });
        builder.Property(r => r.Reaction).HasMaxLength(32);
    }
}

public sealed class RecognitionCommentConfiguration : IEntityTypeConfiguration<RecognitionComment>
{
    public void Configure(EntityTypeBuilder<RecognitionComment> builder)
    {
        builder.ToTable("recognition_comments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Body).HasMaxLength(2000).IsRequired();
        builder.HasIndex(c => c.RecognitionId);
    }
}
