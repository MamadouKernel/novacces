using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovAcces.Domain.Entities;

namespace NovAcces.Infrastructure.Persistence.Configurations;

public sealed class ScanConfirmationRequestConfiguration : IEntityTypeConfiguration<ScanConfirmationRequest>
{
    public void Configure(EntityTypeBuilder<ScanConfirmationRequest> builder)
    {
        builder.ToTable("scan_confirmation_requests");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.VisitorName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CheckpointId).HasMaxLength(100);
        builder.Property(e => e.AgentId).HasMaxLength(100).IsRequired();
        builder.Property(e => e.DecidedBy).HasMaxLength(200);

        builder.HasIndex(e => e.VisitId);
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.RequestingTerminalId);
    }
}
