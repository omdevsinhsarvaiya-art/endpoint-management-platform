using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Key)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.Category)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.Description)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(p => p.IsHighRisk).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // Permission keys are global; they are the contract used by API policies.
        builder.HasIndex(p => p.Key)
            .IsUnique()
            .HasDatabaseName("ix_permissions_key");
    }
}
