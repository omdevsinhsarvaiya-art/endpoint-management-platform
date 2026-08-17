using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    public void Configure(EntityTypeBuilder<EnrollmentToken> builder)
    {
        builder.ToTable("enrollment_tokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizationId).IsRequired();

        builder.Property(t => t.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.SecretHash)
            .HasMaxLength(EnrollmentToken.SecretHashLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(t => t.CreatedByUserId).IsRequired();

        builder.Property(t => t.CreatedByDisplay)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(t => t.ExpiresAt).IsRequired();
        builder.Property(t => t.MaxUses).IsRequired();
        builder.Property(t => t.UseCount).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        // Two agents racing for the last remaining use must not both win. xmin is
        // PostgreSQL's system row-version column; EF turns a lost race into a
        // DbUpdateConcurrencyException, which the enrollment service retries from
        // a fresh read (and then refuses, because the token is exhausted).
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        builder.HasOne<Domain.Identity.Organization>()
            .WithMany()
            .HasForeignKey(t => t.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Enrollment looks tokens up by the hash of what the agent presented.
        builder.HasIndex(t => t.SecretHash)
            .IsUnique()
            .HasDatabaseName("ix_enrollment_tokens_secret_hash");

        builder.HasIndex(t => new { t.OrganizationId, t.ExpiresAt })
            .HasDatabaseName("ix_enrollment_tokens_organization_id_expires_at");
    }
}
