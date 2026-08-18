using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceLocalUserConfiguration : IEntityTypeConfiguration<DeviceLocalUser>
{
    public void Configure(EntityTypeBuilder<DeviceLocalUser> builder)
    {
        builder.ToTable("device_local_users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.DeviceId).IsRequired();
        builder.Property(u => u.Sid).HasMaxLength(184).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(256).IsRequired();
        builder.Property(u => u.FullName).HasMaxLength(256);
        builder.Property(u => u.Description).HasMaxLength(512);
        builder.Property(u => u.CollectedAt).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(u => u.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(u => new { u.DeviceId, u.Sid })
            .IsUnique()
            .HasDatabaseName("ix_device_local_users_device_id_sid");

        // "Which devices still have local admin accounts" is a security query.
        builder.HasIndex(u => u.IsLocalAdministrator)
            .HasFilter("is_local_administrator")
            .HasDatabaseName("ix_device_local_users_is_local_administrator");
    }
}

internal sealed class DeviceLocalGroupConfiguration : IEntityTypeConfiguration<DeviceLocalGroup>
{
    public void Configure(EntityTypeBuilder<DeviceLocalGroup> builder)
    {
        builder.ToTable("device_local_groups");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.DeviceId).IsRequired();
        builder.Property(g => g.Sid).HasMaxLength(184).IsRequired();
        builder.Property(g => g.Name).HasMaxLength(256).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(512);
        builder.Property(g => g.MembersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(g => g.MemberCount).IsRequired();
        builder.Property(g => g.CollectedAt).IsRequired();
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(g => g.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => new { g.DeviceId, g.Sid })
            .IsUnique()
            .HasDatabaseName("ix_device_local_groups_device_id_sid");
    }
}
