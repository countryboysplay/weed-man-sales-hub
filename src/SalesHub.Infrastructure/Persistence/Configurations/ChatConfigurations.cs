using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Name).HasMaxLength(128);
        builder.Property(c => c.DirectKey).HasMaxLength(80);
        builder.HasIndex(c => c.DirectKey).IsUnique();
    }
}

public sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.ToTable("conversation_members");
        builder.HasKey(m => new { m.ConversationId, m.UserId });
        builder.HasIndex(m => m.UserId); // "my conversations" listing
    }
}

public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Body).HasMaxLength(8000);
        // Conversation history pages newest-first by id (UUIDv7 = time-ordered).
        builder.HasIndex(m => new { m.ConversationId, m.Id });
    }
}

public sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> builder)
    {
        builder.ToTable("message_attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.OriginalName).HasMaxLength(512);
        builder.Property(a => a.ContentType).HasMaxLength(255);
        builder.HasIndex(a => a.MessageId);
    }
}

public sealed class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> builder)
    {
        builder.ToTable("message_reactions");
        builder.HasKey(r => new { r.MessageId, r.UserId, r.Reaction });
        builder.Property(r => r.Reaction).HasMaxLength(32);
    }
}
