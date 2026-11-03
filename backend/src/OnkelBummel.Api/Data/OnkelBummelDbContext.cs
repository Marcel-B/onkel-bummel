using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace OnkelBummel.Api.Data;

public sealed class OnkelBummelDbContext(DbContextOptions<OnkelBummelDbContext> options) : DbContext(options)
{
    public DbSet<Topic> Topics => Set<Topic>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<MessageAction> MessageActions => Set<MessageAction>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<PublishToken> PublishTokens => Set<PublishToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Topic>(topic =>
        {
            topic.HasKey(t => t.Name);
            topic.Property(t => t.Name).HasMaxLength(64);
            topic.HasMany(t => t.Messages).WithOne().HasForeignKey(m => m.Topic).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Message>(message =>
        {
            message.Property(m => m.Id).ValueGeneratedOnAdd();
            // Paging goes by id within the chosen topics; unread counts by topic and ReadAt.
            message.HasIndex(m => new { m.Topic, m.Id });
            message.HasIndex(m => new { m.Topic, m.ReadAt });
            message.HasIndex(m => m.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            message.HasIndex(m => m.CreatedAt);
            message.HasMany(m => m.Attachments).WithOne().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
            message.HasMany(m => m.Actions).WithOne().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MessageAction>(action =>
        {
            action.HasKey(a => new { a.MessageId, a.Index });
            action.Property(a => a.Headers).HasConversion(
                headers => JsonSerializer.Serialize(headers, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Default) ?? new(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    headers => headers.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    headers => new Dictionary<string, string>(headers)));
        });

        model.Entity<Device>(device =>
        {
            device.HasIndex(d => d.ApnsToken).IsUnique();
            device.HasMany(d => d.Subscriptions).WithOne().HasForeignKey(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Subscription>(subscription =>
        {
            subscription.HasKey(s => new { s.DeviceId, s.Topic });
            subscription.HasIndex(s => s.Topic);
        });

        model.Entity<PublishToken>(token => token.HasIndex(t => t.Hash).IsUnique());
    }
}
