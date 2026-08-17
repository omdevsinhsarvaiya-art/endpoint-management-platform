using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(o => o.Slug)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(o => o.IsActive)
            .IsRequired();

        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.UpdatedAt).IsRequired();

        builder.HasIndex(o => o.Slug)
            .IsUnique()
            .HasDatabaseName("ix_organizations_slug");
    }
}
