using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("sales");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Cid).HasMaxLength(20).IsRequired();
        builder.Property(s => s.SaleType).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Campaign).HasMaxLength(16).IsRequired();
        builder.Property(s => s.Amount).HasColumnType("numeric(12,2)");
        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Version).IsRowVersion(); // PostgreSQL xmin

        // The duplicate checks and aggregates both scan active rows by
        // seller/date and by cid within a year window.
        builder.HasIndex(s => new { s.SellerUserId, s.BusinessDate });
        builder.HasIndex(s => new { s.Cid, s.BusinessDate });
        builder.HasIndex(s => s.BusinessDate);
        // The application rule (not a plain unique index) owns duplicate
        // semantics: the approved resale-confirmation flow must stay possible
        // (db/schema-notes.sql).
    }
}

public sealed class SaleCorrectionConfiguration : IEntityTypeConfiguration<SaleCorrection>
{
    public void Configure(EntityTypeBuilder<SaleCorrection> builder)
    {
        builder.ToTable("sale_corrections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CorrectionType).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.BeforeJson).HasColumnType("jsonb");
        builder.Property(c => c.AfterJson).HasColumnType("jsonb");
        builder.Property(c => c.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(c => c.SaleId);
    }
}

public sealed class SaleDuplicateOverrideConfiguration
    : IEntityTypeConfiguration<SaleDuplicateOverride>
{
    public void Configure(EntityTypeBuilder<SaleDuplicateOverride> builder)
    {
        builder.ToTable("sale_duplicate_overrides");
        builder.HasKey(o => o.Id);
        builder.HasIndex(o => o.SaleId);
        builder.HasIndex(o => o.PriorSaleId);
    }
}
